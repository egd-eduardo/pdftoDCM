using FellowOakDicom;
using PdfToDicom.Cli;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Core.Validation;

// Ensures accented patient names print correctly in a terminal on any platform.
Console.OutputEncoding = System.Text.Encoding.UTF8;

CommandLineOptions options = CommandLineOptions.Parse(args);

if (options.ParseErrors.Count > 0)
{
    foreach (string error in options.ParseErrors)
    {
        Console.Error.WriteLine($"error: {error}");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("Run 'pdftodicom help' for usage.");
    return ExitCodes.InvalidArguments;
}

if (options.Command is "help" or "--help" or "h" or "?")
{
    Console.WriteLine(CommandLineOptions.HelpText);
    return ExitCodes.Success;
}

using var services = new ApplicationServices(options.ConfigurationPath, options.Verbose);
services.AuditLogger.ApplicationStarted($"{ProductVersion()} (command line)");

try
{
    return options.Command switch
    {
        "send" => await RunConversionAsync(services, options, transmit: !options.NoSend),
        "generate" => await RunConversionAsync(services, options, transmit: false),
        "echo" => await RunEchoAsync(services, options),
        "inspect" => RunInspect(services, options),
        "extract" => RunExtract(services, options),
        "config" => RunShowConfiguration(services),
        _ => UnknownCommand(options.Command)
    };
}
catch (Exception ex)
{
    services.Logger.Error("The command failed with an unhandled error.", ex);
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine("The full details have been written to the log.");
    return ExitCodes.GenerationFailed;
}
finally
{
    services.AuditLogger.ApplicationClosed();
    services.Logger.Flush();
}

// ---------------------------------------------------------------------- Commands

/// <summary>Converts a PDF and, unless suppressed, transmits the result.</summary>
static async Task<int> RunConversionAsync(
    ApplicationServices services,
    CommandLineOptions options,
    bool transmit)
{
    if (string.IsNullOrWhiteSpace(options.PdfPath))
    {
        Console.Error.WriteLine("error: --pdf is required.");
        return ExitCodes.InvalidArguments;
    }

    IReadOnlyList<PacsDestination>? destinations = ResolveDestinations(services, options, out string? destinationError);

    if (destinationError is not null)
    {
        Console.Error.WriteLine($"error: {destinationError}");
        return ExitCodes.InvalidArguments;
    }

    var conversionOptions = new ConversionOptions(transmit, destinations);

    // Progress is written as plain lines rather than an animated bar so the output
    // remains readable when it is redirected to a file by a scheduled task.
    var progress = new Progress<TransmissionProgress>(p =>
        Console.WriteLine($"  [{p.Stage}] {p.Message}"));

    Console.WriteLine($"Converting '{options.PdfPath}'...");

    ConversionResult result = await services.Workflow.RunAsync(
        options.PdfPath, options.Demographics, conversionOptions, progress);

    PrintValidationIssues(result.ValidationIssues);

    if (!result.DicomGenerated)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(result.Summary);

        return result.ValidationIssues.Any(i =>
            i.Severity == ValidationSeverity.Error && IsInputField(i.Field))
            ? ExitCodes.InvalidArguments
            : ExitCodes.GenerationFailed;
    }

    Console.WriteLine();
    Console.WriteLine("DICOM object generated:");
    Console.WriteLine($"  Study Instance UID  : {result.StudyInstanceUid}");
    Console.WriteLine($"  Series Instance UID : {result.SeriesInstanceUid}");
    Console.WriteLine($"  SOP Instance UID    : {result.SopInstanceUid}");

    if (result.GeneratedFilePath is not null)
    {
        Console.WriteLine($"  Saved to            : {result.GeneratedFilePath}");
    }

    if (options.ShowTags && result.GeneratedFilePath is not null)
    {
        Console.WriteLine();
        PrintTags(services, result.GeneratedFilePath);
    }

    if (result.Transmissions.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Transmission results:");

        foreach (TransmissionResult transmission in result.Transmissions)
        {
            string marker = transmission.IsSuccess ? "OK  " : "FAIL";
            Console.WriteLine(
                $"  [{marker}] {transmission.DestinationName} " +
                $"({transmission.CalledAeTitle}@{transmission.Host}:{transmission.Port}) " +
                $"status={transmission.DicomStatusCode ?? "n/a"} " +
                $"attempts={transmission.AttemptCount} " +
                $"{(long)transmission.Duration.TotalMilliseconds} ms");
            Console.WriteLine($"         {transmission.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine(result.Summary);

    return result.IsCompleteSuccess ? ExitCodes.Success : ExitCodes.TransmissionFailed;
}

/// <summary>Sends a verification request to each selected destination.</summary>
static async Task<int> RunEchoAsync(ApplicationServices services, CommandLineOptions options)
{
    IReadOnlyList<PacsDestination>? selected = ResolveDestinations(services, options, out string? error);

    if (error is not null)
    {
        Console.Error.WriteLine($"error: {error}");
        return ExitCodes.InvalidArguments;
    }

    IReadOnlyList<PacsDestination> destinations = selected ?? services.ConfigurationService.Current.EnabledDestinations;

    if (destinations.Count == 0)
    {
        Console.Error.WriteLine("error: no enabled destination is configured.");
        return ExitCodes.InvalidArguments;
    }

    bool allSucceeded = true;

    foreach (PacsDestination destination in destinations)
    {
        Console.WriteLine($"Verifying {destination.DisplaySummary}...");

        TransmissionResult result = await services.NetworkService.VerifyAsync(
            destination, services.ConfigurationService.Current.CallingAeTitle);

        Console.WriteLine(
            $"  [{(result.IsSuccess ? "OK  " : "FAIL")}] {result.Message} " +
            $"({(long)result.Duration.TotalMilliseconds} ms)");

        allSucceeded &= result.IsSuccess;
    }

    return allSucceeded ? ExitCodes.Success : ExitCodes.TransmissionFailed;
}

/// <summary>Prints the tags of an existing DICOM file.</summary>
static int RunInspect(ApplicationServices services, CommandLineOptions options)
{
    if (string.IsNullOrWhiteSpace(options.PdfPath))
    {
        Console.Error.WriteLine("error: --pdf must point at the .dcm file to inspect.");
        return ExitCodes.InvalidArguments;
    }

    if (!File.Exists(options.PdfPath))
    {
        Console.Error.WriteLine($"error: '{options.PdfPath}' does not exist.");
        return ExitCodes.InvalidArguments;
    }

    PrintTags(services, options.PdfPath);

    // Re-running the IOD validation over a stored file is a convenient way for a support
    // engineer to confirm a file kept from an earlier failure is conformant.
    DicomFile file = DicomFile.Open(options.PdfPath);
    ValidationResult validation = services.DicomValidator.Validate(file);

    Console.WriteLine();
    Console.WriteLine(validation.IsValid
        ? "IOD validation: the object conforms to the Encapsulated PDF definition."
        : "IOD validation: the object does NOT conform.");

    PrintValidationIssues(validation.Issues);

    return validation.IsValid ? ExitCodes.Success : ExitCodes.GenerationFailed;
}

/// <summary>
/// Recovers the original PDF from a generated object.
/// </summary>
/// <remarks>
/// This exists for troubleshooting. When a document renders unexpectedly in a PACS
/// viewer, the first question is whether the stored PDF is intact or whether the viewer
/// is rendering it badly. Extracting the payload and opening it in an ordinary PDF reader
/// answers that in seconds: if the recovered file looks correct, the conversion is sound
/// and the problem lies in how the archive displays it.
/// </remarks>
static int RunExtract(ApplicationServices services, CommandLineOptions options)
{
    if (string.IsNullOrWhiteSpace(options.PdfPath))
    {
        Console.Error.WriteLine("error: --pdf must point at the .dcm file to extract from.");
        return ExitCodes.InvalidArguments;
    }

    if (!File.Exists(options.PdfPath))
    {
        Console.Error.WriteLine($"error: '{options.PdfPath}' does not exist.");
        return ExitCodes.InvalidArguments;
    }

    DicomFile file;
    try
    {
        file = DicomFile.Open(options.PdfPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: '{options.PdfPath}' could not be read as a DICOM file: {ex.Message}");
        return ExitCodes.GenerationFailed;
    }

    string mimeType = file.Dataset.GetSingleValueOrDefault(DicomTag.MIMETypeOfEncapsulatedDocument, string.Empty);

    if (!file.Dataset.TryGetValues(DicomTag.EncapsulatedDocument, out byte[] payload) || payload.Length == 0)
    {
        Console.Error.WriteLine("error: the file contains no encapsulated document.");
        return ExitCodes.GenerationFailed;
    }

    // OB values are padded to an even length, so a single trailing null byte may have
    // been added at encapsulation. It is removed here so the recovered file is exactly
    // the original, which is what makes a byte for byte comparison meaningful.
    int length = payload.Length;
    if (length > 0 && payload[length - 1] == 0x00)
    {
        length--;
    }

    string output = string.IsNullOrWhiteSpace(options.OutputPath)
        ? Path.ChangeExtension(options.PdfPath, ".extracted.pdf")
        : options.OutputPath;

    try
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(output, payload.AsSpan(0, length).ToArray());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: the recovered document could not be written: {ex.Message}");
        return ExitCodes.GenerationFailed;
    }

    bool looksLikePdf = length >= 5 && payload.AsSpan(0, 5).SequenceEqual("%PDF-"u8);

    Console.WriteLine($"Recovered {length} bytes to '{output}'.");
    Console.WriteLine($"  Declared MIME type : {(string.IsNullOrEmpty(mimeType) ? "(none)" : mimeType)}");
    Console.WriteLine($"  Begins with a PDF header : {(looksLikePdf ? "yes" : "NO")}");
    Console.WriteLine($"  Patient  : {file.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty)}");
    Console.WriteLine($"  Accession: {file.Dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty)}");
    Console.WriteLine();
    Console.WriteLine("Open the recovered file in an ordinary PDF reader. If it looks correct, the");
    Console.WriteLine("stored document is intact and any display problem lies in the viewer.");

    return looksLikePdf ? ExitCodes.Success : ExitCodes.GenerationFailed;
}

/// <summary>Prints the effective configuration and where it is stored.</summary>
static int RunShowConfiguration(ApplicationServices services)
{
    AppConfiguration configuration = services.ConfigurationService.Current;

    Console.WriteLine($"Configuration file : {services.ConfigurationService.ConfigurationFilePath}");
    Console.WriteLine($"Log folder         : {configuration.LogFolder}");
    Console.WriteLine($"Output folder      : {configuration.OutputFolder}");
    Console.WriteLine($"Institution        : {configuration.InstitutionName}");
    Console.WriteLine($"Calling AE Title   : {configuration.CallingAeTitle}");
    Console.WriteLine($"Character set      : {configuration.SpecificCharacterSetValue}");
    Console.WriteLine($"Modality           : {configuration.Modality}");
    Console.WriteLine($"UID strategy       : {configuration.UidStrategy}");
    Console.WriteLine();
    Console.WriteLine($"Destinations ({configuration.Destinations.Count}):");

    foreach (PacsDestination destination in configuration.Destinations)
    {
        Console.WriteLine(
            $"  [{(destination.IsEnabled ? "on " : "off")}] {destination.Name}: " +
            $"{destination.CalledAeTitle}@{destination.Host}:{destination.Port} " +
            $"(verify={destination.VerifyBeforeSend}, timeout={destination.TimeoutSeconds}s, " +
            $"retries={destination.RetryCount})");
    }

    return ExitCodes.Success;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'.");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLineOptions.HelpText);
    return ExitCodes.InvalidArguments;
}

// ---------------------------------------------------------------------- Helpers

/// <summary>
/// Resolves the destinations named on the command line, or null to mean "all enabled".
/// </summary>
static IReadOnlyList<PacsDestination>? ResolveDestinations(
    ApplicationServices services,
    CommandLineOptions options,
    out string? error)
{
    error = null;

    if (options.DestinationNames.Count == 0)
    {
        return null;
    }

    var resolved = new List<PacsDestination>();

    foreach (string name in options.DestinationNames)
    {
        PacsDestination? match = services.ConfigurationService.Current.Destinations
            .FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            error = $"no destination named '{name}' is configured.";
            return null;
        }

        resolved.Add(match);
    }

    return resolved;
}

static void PrintTags(ApplicationServices services, string dicomFilePath)
{
    DicomFile file = DicomFile.Open(dicomFilePath);
    Console.WriteLine(services.TagInspector.ToText(services.TagInspector.Inspect(file)));
}

static void PrintValidationIssues(IReadOnlyList<ValidationIssue> issues)
{
    foreach (ValidationIssue issue in issues.Where(i => i.Severity == ValidationSeverity.Error))
    {
        Console.Error.WriteLine($"  error  : {issue.Message}");
    }

    foreach (ValidationIssue issue in issues.Where(i => i.Severity == ValidationSeverity.Warning))
    {
        Console.WriteLine($"  warning: {issue.Message}");
    }
}

/// <summary>
/// Distinguishes a problem with the operator's input from a problem building the object,
/// so the exit code tells a calling script which of the two occurred.
/// </summary>
static bool IsInputField(string field) =>
    field is "PatientId" or "FamilyName" or "GivenName" or "MiddleName" or "DateOfBirth"
        or "AccessionNumber" or "PdfPath" or "StudyId" or "ExistingStudyInstanceUid"
        or "CallingAeTitle" or "Destinations";

/// <summary>The product version reported in the audit trail.</summary>
static string ProductVersion() =>
    typeof(CommandLineOptions).Assembly.GetName().Version?.ToString(3) ?? "1.5.0";

/// <summary>Exit codes returned to the shell.</summary>
static class ExitCodes
{
    /// <summary>Everything requested succeeded.</summary>
    public const int Success = 0;

    /// <summary>The arguments or the patient information were not valid.</summary>
    public const int InvalidArguments = 1;

    /// <summary>The object could not be generated or failed validation.</summary>
    public const int GenerationFailed = 2;

    /// <summary>The object was generated but a transmission failed.</summary>
    public const int TransmissionFailed = 3;
}
