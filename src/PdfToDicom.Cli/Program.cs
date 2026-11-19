using FellowOakDicom;
using PdfToDicom.Cli;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Importing;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Networking;
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
// Both spellings. The command was named for the worklist when that was all the password
// guarded; it now guards the destinations too, but a note written down at a hospital six
// months ago still says the old name and must keep working.
if (string.Equals(options.Command, "settings-password", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(options.Command, "worklist-password", StringComparison.OrdinalIgnoreCase))
{
    return RunSettingsPassword();
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
        "check-names" => RunCheckNames(services, options),
        "inspect" => RunInspect(services, options),
        "extract" => RunExtract(services, options),
        "config" => RunShowConfiguration(services),
        "export-settings" => RunExportSettings(services, options),
        "import-settings" => RunImportSettings(services, options),
        // The transaction log goes beside the application log rather than into a folder
        // of its own. Somebody commissioning an interface is already being told where the
        // logs are, and one place to look is worth more than a tidier layout.
        "hl7-check" => Hl7Commands.Check(options.Hl7ProfilePath, options.Hl7MessagePath),
        "hl7-send" => await Hl7Commands.SendAsync(
            options.Hl7ProfilePath,
            options.Hl7Target,
            options.Hl7Values,
            options.Hl7TestData,
            options.NoSend,
            options.Hl7TimeoutSeconds,
            Path.Combine(services.AppLogger.LogFolder, "hl7")),
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
/// Turns a password into the verifier that guards the worklist and destination settings.
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
/// ProtectedSettingsLock.Verifier and compiled in.
/// </para>
/// </remarks>
static int RunSettingsPassword()
{
    Console.WriteLine();
    Console.WriteLine("  Protected settings password");
    Console.WriteLine("  ===========================");
    Console.WriteLine();
    Console.WriteLine("  This one password guards the worklist servers and the destinations.");
    Console.WriteLine("  This produces the line to paste into ProtectedSettingsLock.Verifier.");
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

    string verifier = ProtectedSettingsLock.CreateVerifier(first);

    Console.WriteLine();
    Console.WriteLine("  Paste this into src/PdfToDicom.Core/Security/ProtectedSettingsLock.cs,");
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

    // --from-filename is the unattended path, so a date of birth and an accession number
    // the file name does not carry are allowed to be absent. Nothing else is relaxed.
    var conversionOptions = new ConversionOptions(transmit, destinations, options.FromFilename);

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

    PatientDemographics demographics = options.Demographics;

    if (options.FromFilename)
    {
        FilenameImportConfiguration importSettings = services.ConfigurationService.Current.FilenameImport;
        var reader = new FilenameMetadataReader(importSettings);

        FilenameReadResult read = reader.Read(sourcePath, StudyDateOf(sourcePath, importSettings));

        foreach (string note in read.Notes)
        {
            Console.WriteLine($"  note: {note}");
        }

        if (!read.IsMatch || read.Demographics is null)
        {
            // Refused rather than falling back to whatever was on the command line.
            // --from-filename says where the identity comes from, and quietly converting
            // with something else is how a document reaches the wrong patient.
            Console.Error.WriteLine(
                $"error: '{Path.GetFileName(sourcePath)}' matches no configured file name pattern. " +
                "Check it with 'pdftodicom check-names'.");

            // Moved out of the way all the same. A name that matches nothing will never
            // match on a later run either, so leaving it where it was means retrying it
            // for ever and nobody ever looking at it. In Failed/ somebody does.
            FiledSourceFile refused = new ImportedFileFiler(services.Logger)
                .File(sourcePath, importSettings, succeeded: false);

            if (refused.Message.Length > 0)
            {
                Console.Error.WriteLine($"  {refused.Message}");
            }

            return ExitCodes.InvalidArguments;
        }

        Console.WriteLine($"  read from the file name using '{read.Pattern}'");

        // The same audit entry the desktop application writes. Where a document's
        // identifiers came from matters more than usual here, because on this path nobody
        // looked at them before the object was created.
        services.AuditLogger.PatientInformationReadFromFilename(
            Path.GetFileName(sourcePath), read.Pattern);

        demographics = read.Demographics;

        // Anything the pattern cannot carry is still accepted from the command line, so a
        // caller can supply a referring physician or a document title alongside.
        if (options.Demographics.ReferringPhysicianName.Length > 0)
        {
            demographics.ReferringPhysicianName = options.Demographics.ReferringPhysicianName;
        }

        if (options.Demographics.DocumentTitle.Length > 0)
        {
            demographics.DocumentTitle = options.Demographics.DocumentTitle;
        }

        if (options.Demographics.SeriesDescription.Length > 0)
        {
            demographics.SeriesDescription = options.Demographics.SeriesDescription;
        }

        if (options.Demographics.StudyDescription.Length > 0)
        {
            demographics.StudyDescription = options.Demographics.StudyDescription;
        }
    }

    ConversionResult result = await services.Workflow.RunAsync(
        sourcePath, demographics, conversionOptions, progress);

    PrintValidationIssues(result.ValidationIssues);

    // Only on the --from-filename path, because that is the unattended one. A file named
    // explicitly by somebody at a prompt is theirs, and moving it under them would be
    // presumptuous.
    if (options.FromFilename)
    {
        FilenameImportConfiguration importSettings = services.ConfigurationService.Current.FilenameImport;
        var filer = new ImportedFileFiler(services.Logger);

        // Success here means the object reached every destination it was meant to, or was
        // generated when nothing was being sent. Anything less leaves the file to be dealt
        // with rather than filed as done.
        bool imported = result.DicomGenerated
            && (!conversionOptions.Transmit || result.IsCompleteSuccess);

        FiledSourceFile filed = filer.File(sourcePath, importSettings, imported);

        if (filed.Message.Length > 0)
        {
            Console.WriteLine($"  {filed.Message}");
        }
    }

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

    if (configuration.Worklists.Count == 0)
    {
        Console.Error.WriteLine(
            "error: no worklist server is configured. Add one on the Worklist page in Settings, " +
            "or in the Worklists section of the configuration file.");
        return ExitCodes.InvalidArguments;
    }

    IReadOnlyList<WorklistConfiguration> chosen = SelectWorklists(configuration, options.WorklistName);

    if (chosen.Count == 0)
    {
        Console.Error.WriteLine($"error: no configured worklist is called '{options.WorklistName}'. Configured:");

        foreach (WorklistConfiguration candidate in configuration.Worklists)
        {
            Console.Error.WriteLine($"  {candidate.DisplayName}");
        }

        return ExitCodes.InvalidArguments;
    }

    // Every chosen server is queried, and the worst outcome decides the exit code. A
    // commissioning run against four servers should say which of them answered rather than
    // stopping at the first that did not.
    int failures = 0;

    foreach (WorklistConfiguration worklist in chosen)
    {
        if (chosen.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {worklist.DisplayName}");
        }

        if (!await QueryOneWorklistAsync(services, configuration, worklist, options).ConfigureAwait(false))
        {
            failures++;
        }
    }

    return failures == 0 ? ExitCodes.Success : ExitCodes.TransmissionFailed;
}

/// <summary>
/// The servers a run should query: the one named, or every configured one.
/// </summary>
/// <remarks>
/// Matched on the name and then on the identifier, both ignoring case, because the name is
/// what a person reads off the Settings page and the identifier is what a script would
/// have. Every server is queried when none is named, since this command exists to answer
/// "which of these work?".
/// </remarks>
static IReadOnlyList<WorklistConfiguration> SelectWorklists(AppConfiguration configuration, string? name)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return configuration.Worklists;
    }

    string wanted = name.Trim();

    return configuration.Worklists
        .Where(w =>
            string.Equals(w.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.DisplayName, wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.Id, wanted, StringComparison.OrdinalIgnoreCase))
        .ToList();
}

/// <summary>Queries one server and prints what came back. True when it answered.</summary>
static async Task<bool> QueryOneWorklistAsync(
    ApplicationServices services,
    AppConfiguration configuration,
    WorklistConfiguration worklist,
    CommandLineOptions options)
{
    if (string.IsNullOrWhiteSpace(worklist.Host))
    {
        Console.Error.WriteLine($"error: '{worklist.DisplayName}' has no host configured.");
        return false;
    }

    // Said rather than refused. Querying a server that the application will not offer to
    // the operator is exactly what commissioning looks like, and failing here would make
    // the command useless for the one job it has.
    if (!worklist.IsEnabled)
    {
        Console.WriteLine($"note: '{worklist.DisplayName}' is configured but not enabled, so the application will not offer it yet.");
        Console.WriteLine();
    }

    Console.WriteLine($"Querying {worklist.CalledAeTitle}@{worklist.Host}:{worklist.Port} as {CallingAeTitleFor(worklist, configuration)}...");

    WorklistQueryResult result = await services.Worklist.QueryAsync(
        worklist,
        configuration.CallingAeTitle,
        options.Demographics.PatientId,
        options.Demographics.FamilyName,
        options.DateRange is null ? null : new WorklistDateWindow(options.DateRange.Value))
        .ConfigureAwait(false);

    Console.WriteLine(result.Message);

    if (!result.IsSuccess)
    {
        return false;
    }

    // Everything was fetched; this decides only how much is printed. A department's whole
    // schedule is tens of thousands of lines, and a console that scrolls past them has not
    // shown them to anybody.
    int shown = options.AllResults
        ? result.Entries.Count
        : Math.Min(result.Entries.Count, Math.Max(1, worklist.MaximumResults));

    foreach (WorklistEntry entry in result.Entries.Take(shown))
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

    if (shown < result.Entries.Count)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"note: {result.Entries.Count} entries were found and {shown} printed. " +
            "Add --all to print the rest, or narrow the dates or the filters.");
    }

    if (result.WasTruncated)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"note: the query stopped at {WorklistService.FetchCeiling} entries, which is " +
            "this software's ceiling and not the server's answer, so there are scheduled " +
            "steps it has not seen.");
    }

    return true;
}

/// <summary>The AE Title the worklist query will present, for the line printed before it runs.</summary>
static string CallingAeTitleFor(WorklistConfiguration worklist, AppConfiguration configuration) =>
    string.IsNullOrWhiteSpace(worklist.CallingAeTitleOverride)
        ? configuration.CallingAeTitle
        : worklist.CallingAeTitleOverride;

/// <summary>
/// Tries the configured file name patterns against names, without converting anything.
/// </summary>
/// <remarks>
/// <para>
/// This is how a format gets established. Getting a pattern right is a few minutes of
/// trial and error against real file names, and doing that through the desktop
/// application, a monitored folder and a PACS would be a slow way to discover a missing
/// underscore.
/// </para>
/// <para>
/// It touches nothing: no conversion, no network, no files written. Names may be given as
/// arguments, or piped in one per line, which is what makes it easy to point at a whole
/// folder listing at once.
/// </para>
/// </remarks>
static int RunCheckNames(ApplicationServices services, CommandLineOptions options)
{
    FilenameImportConfiguration configuration = services.ConfigurationService.Current.FilenameImport;

    // An explicit pattern on the command line overrides the configured ones, so a format
    // can be tried before anybody commits it to a configuration file.
    if (options.Patterns.Count > 0 || options.SplitNames || options.PadPatientIdTo is not null)
    {
        configuration = configuration.Clone();

        if (options.Patterns.Count > 0)
        {
            configuration.Patterns = new List<string>(options.Patterns);
        }

        if (options.SplitNames)
        {
            configuration.SplitRunTogetherNames = true;
        }

        if (options.PadPatientIdTo is int width)
        {
            configuration.PadPatientIdTo = width;
        }
    }

    var reader = new FilenameMetadataReader(configuration);

    Console.WriteLine();
    Console.WriteLine($"  Patterns ({reader.UsablePatternCount} usable):");

    foreach (string pattern in configuration.Patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
    {
        Console.WriteLine($"    {pattern}");
    }

    foreach (string error in reader.PatternErrors)
    {
        Console.Error.WriteLine($"    error: {error}");
    }

    if (reader.UsablePatternCount == 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("  No usable pattern, so nothing can be read. Supply one with --pattern,");
        Console.Error.WriteLine("  or fill in the FilenameImport section of the configuration file.");
        return ExitCodes.InvalidArguments;
    }

    Console.WriteLine($"  Date formats: {string.Join(", ", configuration.EffectiveDateFormats)}");
    Console.WriteLine(configuration.PadPatientIdTo > 0
        ? $"  Patient numbers are padded with zeros to {configuration.PadPatientIdTo} digits."
        : "  Patient numbers are used exactly as the file name has them.");
    Console.WriteLine();

    IReadOnlyList<string> names = CollectNamesToCheck(options);

    if (names.Count == 0)
    {
        Console.Error.WriteLine("  No names to check. Pass them as --name, or pipe a list in, one per line.");
        return ExitCodes.InvalidArguments;
    }

    int matched = 0;

    foreach (string name in names)
    {
        FilenameReadResult result = reader.Read(name, StudyDateOf(name, configuration));

        if (result.IsMatch && result.Demographics is not null)
        {
            matched++;
            PatientDemographics d = result.Demographics;

            Console.WriteLine($"  MATCH    {name}");
            Console.WriteLine($"           pattern     {result.Pattern}");
            Console.WriteLine($"           patient id  {Shown(d.PatientId)}");
            Console.WriteLine($"           name        {Shown(ComposeName(d))}");
            Console.WriteLine($"           accession   {Shown(d.AccessionNumber)}");
            Console.WriteLine($"           born        {d.DateOfBirth?.ToString("yyyy-MM-dd") ?? "(blank)"}");
            Console.WriteLine($"           sex         {Shown(d.Sex.ToDicomValue())}");

            if (d.StudyDescription.Length > 0)
            {
                Console.WriteLine($"           study       {d.StudyDescription}");
            }

            if (d.StudyDateTime is not null)
            {
                Console.WriteLine($"           study date  {d.StudyDateTime:yyyy-MM-dd HH:mm} (file creation time)");
            }

            // The same rules the form applies, run here so a pattern that reads correctly
            // but produces something unsendable is caught now rather than at the PACS.
            //
            // Only for a name that is a real file. Checking a list of names, which is the
            // usual way a format gets worked out, would otherwise report that no document
            // has been selected against every line, which is true and useless.
            if (File.Exists(name))
            {
                ValidationResult validation = services.Workflow.ValidateInputs(
                    name, d, requireDestination: false);

                if (!validation.IsValid)
                {
                    Console.WriteLine("           NOT YET SENDABLE:");
                    PrintValidationIssues(validation.Issues);
                }
            }
        }
        else
        {
            Console.WriteLine($"  no match {name}");
        }

        foreach (string note in result.Notes.Where(n => !reader.PatternErrors.Contains(n)))
        {
            Console.WriteLine($"           note        {note}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  {matched} of {names.Count} name(s) matched.");

    return matched == names.Count ? ExitCodes.Success : ExitCodes.InvalidArguments;
}

/// <summary>The names to try: those given as options, plus anything piped in.</summary>
static IReadOnlyList<string> CollectNamesToCheck(CommandLineOptions options)
{
    var names = new List<string>(options.NamesToCheck);

    // Only when nothing was named AND input is redirected.
    //
    // Reading stdin whenever it is redirected hangs: under a script, a scheduled task or
    // any tool that gives the process a pipe nobody writes to, ReadLine blocks for ever,
    // and it does so after the names from --name have already been collected and could
    // have been answered. Found by it hanging here rather than by reading the code.
    if (names.Count == 0 && Console.IsInputRedirected)
    {
        string? line;

        while ((line = Console.ReadLine()) is not null)
        {
            if (line.Trim().Length > 0)
            {
                names.Add(line.Trim());
            }
        }
    }

    return names;
}

/// <summary>
/// The file's creation time, when it is a real file and the setting asks for it.
/// </summary>
/// <remarks>
/// A name being checked need not exist, which is the usual case when a format is being
/// worked out from a list. Nothing is reported then; the study date simply stays as the
/// moment of conversion, exactly as it does today.
/// </remarks>
static DateTime? StudyDateOf(string path, FilenameImportConfiguration configuration) =>
    configuration.UseFileDateAsStudyDate && File.Exists(path)
        ? File.GetCreationTime(path)
        : null;

static string ComposeName(PatientDemographics demographics) =>
    demographics.GivenName.Length > 0
        ? $"{demographics.FamilyName}, {demographics.GivenName}"
        : demographics.FamilyName;

static string Shown(string value) => value.Length > 0 ? value : "(blank)";

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
/// <summary>
/// Writes the archives and worklist servers to a file.
/// </summary>
/// <remarks>
/// Here as well as in the settings dialog because setting up a ward full of workstations
/// is a job for a script. The dialog is for the person who configured the first machine;
/// this is for the twenty after it.
/// </remarks>
static int RunExportSettings(ApplicationServices services, CommandLineOptions options)
{
    if (string.IsNullOrWhiteSpace(options.OutputPath))
    {
        Console.Error.WriteLine("error: export-settings needs --out <path>.");
        return ExitCodes.InvalidArguments;
    }

    SettingsTransferParts parts = ChosenParts(options);

    AppConfiguration configuration = services.ConfigurationService.Current;
    string station = string.IsNullOrWhiteSpace(configuration.StationName)
        ? Environment.MachineName
        : configuration.StationName;

    SettingsBundle bundle = SettingsTransfer.CreateBundle(
        configuration, parts, station, ProductVersion());

    try
    {
        SettingsTransfer.Write(bundle, options.OutputPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: the file could not be written: {ex.Message}");
        services.Logger.Error($"The settings could not be exported to '{options.OutputPath}'.", ex);
        return ExitCodes.GenerationFailed;
    }

    Console.WriteLine($"Exported to {Path.GetFullPath(options.OutputPath)}");
    Console.WriteLine($"  archives        : {bundle.Destinations.Count}");
    Console.WriteLine($"  worklist servers: {bundle.Worklists.Count}");
    Console.WriteLine();
    Console.WriteLine("The file carries connection settings only. This workstation's own calling");
    Console.WriteLine("AE Title, folders and language stay where they are.");

    services.AuditLogger.ConfigurationChanged(
        $"Settings exported to '{options.OutputPath}': {bundle.Destinations.Count} destination(s), " +
        $"{bundle.Worklists.Count} worklist server(s).");

    return ExitCodes.Success;
}

/// <summary>
/// Reads a settings file and brings its entries into this machine's configuration.
/// </summary>
/// <remarks>
/// Unlike the dialog, this writes the configuration immediately: there is nobody here to
/// press Save. What it did is printed, and an unattended run can be read back from the
/// log and the audit trail.
/// </remarks>
static int RunImportSettings(ApplicationServices services, CommandLineOptions options)
{
    string? path = options.PdfPath;

    if (string.IsNullOrWhiteSpace(path))
    {
        Console.Error.WriteLine("error: import-settings needs --file <path>.");
        return ExitCodes.InvalidArguments;
    }

    SettingsBundleRead read = SettingsTransfer.Read(path);

    if (!read.Succeeded)
    {
        Console.Error.WriteLine($"error: {read.Problem}");
        return ExitCodes.InvalidArguments;
    }

    SettingsBundle bundle = read.Bundle!;
    SettingsTransferParts parts = ChosenParts(options) & bundle.Parts;

    if (parts == SettingsTransferParts.None)
    {
        Console.Error.WriteLine(
            "error: the file does not hold the kind of settings that were asked for.");
        return ExitCodes.InvalidArguments;
    }

    SettingsImportMode mode = options.ReplaceOnImport
        ? SettingsImportMode.ReplaceExisting
        : SettingsImportMode.AddToExisting;

    AppConfiguration configuration = services.ConfigurationService.Current;
    SettingsImportReport report = SettingsTransfer.Apply(bundle, configuration, parts, mode);

    services.ConfigurationService.Save(configuration);

    Console.WriteLine($"Imported from {Path.GetFullPath(path)}");
    Console.WriteLine($"  exported {bundle.ExportedUtc:yyyy-MM-dd HH:mm} UTC" +
                      (bundle.ExportedFrom.Length > 0 ? $" from {bundle.ExportedFrom}" : string.Empty));
    Console.WriteLine($"  archives        : {report.DestinationsAdded} added, " +
                      $"{report.DestinationsAlreadyPresent} already present, " +
                      $"{report.DestinationsRemoved} discarded");
    Console.WriteLine($"  worklist servers: {report.WorklistsAdded} added, " +
                      $"{report.WorklistsAlreadyPresent} already present, " +
                      $"{report.WorklistsRemoved} discarded");
    Console.WriteLine();
    Console.WriteLine(report.ChangedNothing
        ? "Nothing changed: everything in the file was already configured here."
        : $"Written to {services.ConfigurationService.ConfigurationFilePath}");

    services.AuditLogger.ConfigurationChanged(
        $"Settings imported from '{path}' ({mode}): " +
        $"{report.DestinationsAdded} destination(s) and {report.WorklistsAdded} worklist server(s) added, " +
        $"{report.DestinationsAlreadyPresent + report.WorklistsAlreadyPresent} already present, " +
        $"{report.DestinationsRemoved + report.WorklistsRemoved} discarded.");

    return ExitCodes.Success;
}

/// <summary>
/// Turns the two limiting options into the kinds to carry. Neither one means both.
/// </summary>
static SettingsTransferParts ChosenParts(CommandLineOptions options)
{
    if (options.DestinationsOnly && !options.WorklistsOnly)
    {
        return SettingsTransferParts.Destinations;
    }

    if (options.WorklistsOnly && !options.DestinationsOnly)
    {
        return SettingsTransferParts.Worklists;
    }

    return SettingsTransferParts.Both;
}

static int RunShowConfiguration(ApplicationServices services)
{
    AppConfiguration configuration = services.ConfigurationService.Current;

    Console.WriteLine($"Configuration file : {services.ConfigurationService.ConfigurationFilePath}");

    if (services.ConfigurationService.LoadProblem.Length > 0)
    {
        // First, and to stderr, because everything printed after it is a default rather
        // than what the site configured.
        Console.Error.WriteLine();
        Console.Error.WriteLine($"WARNING: {services.ConfigurationService.LoadProblem}");
        Console.Error.WriteLine();
    }

    if (services.ConfigurationService.LoadNotice.Length > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"NOTE: {services.ConfigurationService.LoadNotice}");
        Console.WriteLine();
    }

    Console.WriteLine($"Settings version   : {configuration.ConfigurationVersion}" +
                      (configuration.ConfigurationVersion == AppConfiguration.CurrentVersion
                          ? " (current)"
                          : $" (this build writes {AppConfiguration.CurrentVersion})"));
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

    // Printed because "the worklist option does not appear" is answered here and nowhere
    // else. The button is shown only when a server is enabled, so a site that configured
    // one and did not tick Enabled sees nothing and has nothing to read.
    Console.WriteLine();
    Console.WriteLine($"Worklist servers ({configuration.Worklists.Count}):");

    if (configuration.Worklists.Count == 0)
    {
        Console.WriteLine("  none configured, so the Worklist button is not shown.");
    }

    foreach (WorklistConfiguration worklist in configuration.Worklists)
    {
        Console.WriteLine(
            $"  [{(worklist.IsEnabled ? "on " : "off")}] {worklist.DisplayName}: " +
            $"{worklist.CalledAeTitle}@{worklist.Host}:{worklist.Port} " +
            $"(modality={Shown(worklist.Modality)}, dates={worklist.DateRange}, " +
            $"timeout={worklist.TimeoutSeconds}s, max={worklist.MaximumResults})");
    }

    Console.WriteLine(configuration.IsWorklistAvailable
        ? "  The Worklist button IS shown on the main window."
        : "  The Worklist button is NOT shown: no server above is enabled.");

    // Printed because a pattern that was rejected would otherwise fail silently: the form
    // would simply never fill in, and nothing on screen would say why.
    FilenameImportConfiguration import = configuration.FilenameImport;

    Console.WriteLine();
    Console.WriteLine($"Filename import    : {(import.IsEnabled ? "enabled" : "disabled")}");

    if (import.IsEnabled || import.Patterns.Count > 0)
    {
        var reader = new FilenameMetadataReader(import);

        foreach (string pattern in import.Patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            Console.WriteLine($"  pattern  {pattern}");
        }

        foreach (string patternError in reader.PatternErrors)
        {
            Console.Error.WriteLine($"  error    {patternError}");
        }

        Console.WriteLine($"  study date from the file: {import.UseFileDateAsStudyDate}");
        Console.WriteLine(import.PadPatientIdTo > 0
            ? $"  patient number padded to: {import.PadPatientIdTo} digits"
            : "  patient number padded to: not padded");
        Console.WriteLine($"  after import            : {import.AfterImport}");
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
