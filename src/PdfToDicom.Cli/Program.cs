using FellowOakDicom;
using PdfToDicom.Cli;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Security;
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

// Handled before anything else is set up. It needs no configuration, no log folder and
// no DICOM stack, and it must work on a machine where none of those are in place yet.
if (string.Equals(options.Command, "worklist-password", StringComparison.OrdinalIgnoreCase))
{
    return RunWorklistPassword();
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
        "worklist" => await RunWorklistAsync(services, options),
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

/// <summary>
/// Turns a password into the verifier that guards the worklist settings.
/// </summary>
/// <remarks>
/// <para>
/// Exists so the password never has to be told to anyone. It is read from the keyboard
/// without being echoed, converted here on the author's own machine, and only the verifier
/// is ever written down. Nothing that could be turned back into the password leaves this
/// process.
/// </para>
/// <para>
/// The output goes to standard output as a single line, intended to be pasted into
/// WorklistSettingsLock.Verifier and compiled in.
/// </para>
/// </remarks>
static int RunWorklistPassword()
{
    Console.WriteLine();
    Console.WriteLine("  Worklist settings password");
    Console.WriteLine("  ==========================");
    Console.WriteLine();
    Console.WriteLine("  This produces the line to paste into WorklistSettingsLock.Verifier.");
    Console.WriteLine("  The password itself is never stored, printed or sent anywhere.");
    Console.WriteLine();

    string? first = ReadHidden("  Password: ");
    if (string.IsNullOrWhiteSpace(first))
    {
        Console.Error.WriteLine("  No password was entered. Nothing was generated.");
        return ExitCodes.InvalidArguments;
    }

    string? second = ReadHidden("  Repeat   : ");
    if (!string.Equals(first, second, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("  The two entries did not match. Nothing was generated.");
        return ExitCodes.InvalidArguments;
    }

    // Said rather than enforced. A short password is the author's choice to make, and
    // refusing it outright would be presumptuous about a lock whose realistic job is to
    // deter a colleague rather than to resist an attacker.
    if (first.Length < 10)
    {
        Console.WriteLine();
        Console.WriteLine($"  NOTE: that password is {first.Length} characters. A longer passphrase is");
        Console.WriteLine("  meaningfully harder to guess, and this one is typed rarely.");
    }

    string verifier = WorklistSettingsLock.CreateVerifier(first);

    Console.WriteLine();
    Console.WriteLine("  Paste this into src/PdfToDicom.Core/Security/WorklistSettingsLock.cs,");
    Console.WriteLine("  replacing the empty Verifier constant, then rebuild:");
    Console.WriteLine();
    Console.WriteLine($"    private const string Verifier = \"{verifier}\";");
    Console.WriteLine();
    Console.WriteLine("  Keep a record of the password somewhere safe. It cannot be recovered");
    Console.WriteLine("  from the line above, which is the point of storing it this way.");
    Console.WriteLine();

    return ExitCodes.Success;
}

/// <summary>
/// Reads a line without echoing it, falling back to a visible read where the console
/// cannot be controlled, for instance when input is piped.
/// </summary>
static string? ReadHidden(string prompt)
{
    Console.Write(prompt);

    if (Console.IsInputRedirected)
    {
        // Nothing is being typed at a terminal, so there is nothing to hide. Saying so
        // beats pretending the input was masked when it was not.
        Console.WriteLine("(input is not a terminal, so it will not be hidden)");
        return Console.ReadLine();
    }

    var builder = new System.Text.StringBuilder();

    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return builder.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (builder.Length > 0)
            {
                builder.Length--;
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            builder.Append(key.KeyChar);
        }
    }
}


/// <summary>Converts a PDF or an image and, unless suppressed, transmits the result.</summary>
static async Task<int> RunConversionAsync(
    ApplicationServices services,
    CommandLineOptions options,
    bool transmit)
{
    if (options.SourcePaths.Count == 0)
    {
        Console.Error.WriteLine("error: --file is required (--pdf and --image mean the same thing).");
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

    // Converted in one run so that study grouping, which is held in memory and never
    // written to disk, can put several files for one patient into the same study.
    int worstExitCode = ExitCodes.Success;

    foreach (string sourcePath in options.SourcePaths)
    {
        if (options.SourcePaths.Count > 1)
        {
            Console.WriteLine();
        }

        int exitCode = await ConvertOneAsync(services, options, sourcePath, conversionOptions, progress);

        // The first real failure is what the caller is told about, but every file is
        // still attempted: a script converting a folder should not stop dead because one
        // page of one document was malformed.
        if (exitCode != ExitCodes.Success && worstExitCode == ExitCodes.Success)
        {
            worstExitCode = exitCode;
        }
    }

    return worstExitCode;
}

/// <summary>Converts a single file and reports what happened to it.</summary>
static async Task<int> ConvertOneAsync(
    ApplicationServices services,
    CommandLineOptions options,
    string sourcePath,
    ConversionOptions conversionOptions,
    IProgress<TransmissionProgress> progress)
{
    string kindDescription = services.Workflow.DetectKind(sourcePath) switch
    {
        SourceDocumentKind.Pdf => "PDF document",
        SourceDocumentKind.Image => "image",
        _ => "file of an unrecognised type"
    };

    Console.WriteLine($"Converting '{sourcePath}' ({kindDescription})...");

    ConversionResult result = await services.Workflow.RunAsync(
        sourcePath, options.Demographics, conversionOptions, progress);

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
    Console.WriteLine(result.InstanceCount > 1
        ? $"{result.InstanceCount} DICOM objects generated, one per page, all in one series:"
        : "DICOM object generated:");

    Console.WriteLine($"  Study Instance UID  : {result.StudyInstanceUid}");
    Console.WriteLine($"  Series Instance UID : {result.SeriesInstanceUid}");

    if (result.InstanceCount > 1)
    {
        for (int i = 0; i < result.SopInstanceUids.Count; i++)
        {
            Console.WriteLine($"  SOP Instance {i + 1,-6} : {result.SopInstanceUids[i]}");
        }
    }
    else
    {
        Console.WriteLine($"  SOP Instance UID    : {result.SopInstanceUid}");
    }

    if (result.GeneratedFilePaths.Count > 1)
    {
        Console.WriteLine("  Saved to            :");
        foreach (string path in result.GeneratedFilePaths)
        {
            Console.WriteLine($"      {path}");
        }
    }
    else if (result.GeneratedFilePath is not null)
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

/// <summary>
/// Queries the configured Modality Worklist and prints what came back.
/// </summary>
/// <remarks>
/// <para>
/// Written for the person commissioning a site rather than for daily use: the operator
/// picks a patient in the window, not here. Its value is that it answers "is the worklist
/// reachable, and does it return what we expect?" over a real association, from a machine
/// with nothing but this one file on it, and without touching the clinical interface.
/// </para>
/// <para>
/// It prints the name twice whenever the two differ: once as the worklist sent it, and
/// once as it would be written to the object. A Person Name may carry sixty four
/// characters, so a long name is shortened, and seeing both here is how an administrator
/// learns that before a clinician asks about it.
/// </para>
/// </remarks>
static async Task<int> RunWorklistAsync(ApplicationServices services, CommandLineOptions options)
{
    AppConfiguration configuration = services.ConfigurationService.Current;
    WorklistConfiguration worklist = configuration.Worklist;

    if (string.IsNullOrWhiteSpace(worklist.Host))
    {
        Console.Error.WriteLine("error: no worklist server is configured. See the Worklist section of the configuration file.");
        return ExitCodes.InvalidArguments;
    }

    // Said rather than refused. Querying a server that the application will not offer to
    // the operator is exactly what commissioning looks like, and failing here would make
    // the command useless for the one job it has.
    if (!worklist.IsEnabled)
    {
        Console.WriteLine("note: the worklist is configured but not enabled, so the application will not offer it yet.");
        Console.WriteLine();
    }

    Console.WriteLine($"Querying {worklist.CalledAeTitle}@{worklist.Host}:{worklist.Port} as {CallingAeTitleFor(worklist, configuration)}...");

    WorklistQueryResult result = await services.Worklist.QueryAsync(
        worklist,
        configuration.CallingAeTitle,
        options.Demographics.PatientId,
        options.Demographics.FamilyName);

    Console.WriteLine(result.Message);

    if (!result.IsSuccess)
    {
        return ExitCodes.TransmissionFailed;
    }

    foreach (WorklistEntry entry in result.Entries)
    {
        ShortenedPersonName fitted = PersonNameShortener.Fit(entry.FamilyName, entry.GivenName);

        Console.WriteLine();
        Console.WriteLine($"  {entry.PatientId}  {entry.DisplayName}");
        Console.WriteLine($"    accession  {entry.AccessionNumber}");
        Console.WriteLine($"    born       {entry.DisplayDateOfBirth}   sex {entry.Sex.ToDicomValue()}");
        Console.WriteLine($"    scheduled  {entry.DisplayScheduled}   modality {entry.Modality}");
        Console.WriteLine($"    procedure  {entry.DisplayProcedure}");
        Console.WriteLine($"    study UID  {entry.StudyInstanceUid}");

        if (fitted.WasShortened)
        {
            // No trailing caret when nothing is left of the given name. It is a valid
            // Person Name either way, but printing one invites the reader to wonder
            // whether something was lost in this output rather than in the name.
            string sent = string.IsNullOrEmpty(fitted.GivenName)
                ? fitted.FamilyName
                : $"{fitted.FamilyName}^{fitted.GivenName}";

            Console.WriteLine(
                $"    SHORTENED  the name is {fitted.OriginalLength} characters and would be sent as '{sent}'");
        }
    }

    if (result.WasTruncated)
    {
        Console.WriteLine();
        Console.WriteLine($"note: the server had more to return than the configured maximum of {worklist.MaximumResults}.");
    }

    return ExitCodes.Success;
}

/// <summary>The AE Title the worklist query will present, for the line printed before it runs.</summary>
static string CallingAeTitleFor(WorklistConfiguration worklist, AppConfiguration configuration) =>
    string.IsNullOrWhiteSpace(worklist.CallingAeTitleOverride)
        ? configuration.CallingAeTitle
        : worklist.CallingAeTitleOverride;

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
    typeof(CommandLineOptions).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

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
