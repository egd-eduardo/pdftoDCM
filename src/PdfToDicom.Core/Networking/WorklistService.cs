using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Networking;

/// <summary>
/// Queries a Modality Worklist SCP using C-FIND.
/// </summary>
/// <remarks>
/// <para>
/// The query is assembled attribute by attribute rather than through the toolkit's
/// convenience helper. That helper sends a Scheduled Procedure Step Start Time of
/// "000000-000000", which asks for steps scheduled at exactly midnight and returns nothing
/// from a real department's worklist.
/// </para>
/// <para>
/// Every attribute this software wants back is sent as an empty value. That is DICOM
/// universal matching: an empty key means "match anything, and tell me what it was". A key
/// that is not sent at all is not returned at all, which is the usual reason a worklist
/// query appears to work but comes back missing the Study Instance UID.
/// </para>
/// </remarks>
public sealed class WorklistService : IWorklistService
{
    private readonly IAppLogger _logger;

    /// <summary>Creates the service.</summary>
    public WorklistService(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DicomRuntime.EnsureInitialized();
    }

    /// <inheritdoc />
    public async Task<(bool IsSuccess, string Message)> TestConnectionAsync(
        WorklistConfiguration configuration,
        string callingAeTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        try
        {
            IDicomClient client = CreateClient(configuration, ResolveCallingAe(configuration, callingAeTitle));

            DicomStatus? status = null;
            var request = new DicomCEchoRequest();
            request.OnResponseReceived += (_, response) => status = response.Status;

            await client.AddRequestAsync(request).ConfigureAwait(false);
            await client.SendAsync(cancellationToken).ConfigureAwait(false);

            if (status == DicomStatus.Success)
            {
                return (true, $"The worklist server answered as {configuration.CalledAeTitle.Trim()}.");
            }

            return (false, $"The worklist server refused the verification: {status?.ToString() ?? "no response"}.");
        }
        catch (Exception ex)
        {
            _logger.Warning($"The worklist connection test failed: {ex.Message}");
            return (false, Explain(ex, configuration));
        }
    }

    /// <inheritdoc />
    public async Task<WorklistQueryResult> QueryAsync(
        WorklistConfiguration configuration,
        string callingAeTitle,
        string? patientIdFilter = null,
        string? patientNameFilter = null,
        WorklistDateWindow? dateRangeOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var entries = new List<WorklistEntry>();
        bool truncated = false;

        try
        {
            IDicomClient client = CreateClient(configuration, ResolveCallingAe(configuration, callingAeTitle));

            DicomCFindRequest request = BuildQuery(
                configuration, patientIdFilter, patientNameFilter, dateRangeOverride);
            DicomStatus? finalStatus = null;

            request.OnResponseReceived += (_, response) =>
            {
                if (response.Status == DicomStatus.Pending && response.HasDataset)
                {
                    // Collected first and cut later, after the sort.
                    //
                    // This used to stop at the configured maximum as responses arrived, so a
                    // wide query kept whichever entries the server happened to send first and
                    // then sorted those. The list was therefore cut in the server's order and
                    // presented in date order, which looks like a complete answer and is not:
                    // the examination somebody was looking for could be absent while the list
                    // appeared full. The ceiling below still exists, so a server with a
                    // runaway schedule cannot exhaust this process.
                    if (entries.Count >= CollectionCeiling(configuration))
                    {
                        truncated = true;
                        return;
                    }

                    WorklistEntry? entry = TryReadEntry(response.Dataset);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                else if (response.Status != DicomStatus.Pending)
                {
                    finalStatus = response.Status;
                }
            };

            await client.AddRequestAsync(request).ConfigureAwait(false);
            await client.SendAsync(cancellationToken).ConfigureAwait(false);

            if (finalStatus is not null && finalStatus != DicomStatus.Success)
            {
                string message = $"The worklist server returned {finalStatus}.";
                _logger.Warning(message);
                return new WorklistQueryResult(false, Array.Empty<WorklistEntry>(), message);
            }

            List<WorklistEntry> ordered = entries
                .OrderBy(e => e.ScheduledStart ?? DateTime.MaxValue)
                .ThenBy(e => e.FamilyName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            int found = ordered.Count;

            if (ordered.Count > configuration.MaximumResults)
            {
                ordered = ordered.Take(configuration.MaximumResults).ToList();
                truncated = true;
            }

            string criteria = DescribeCriteria(
                configuration, patientIdFilter, patientNameFilter, dateRangeOverride);

            // The criteria are written down, not just the count. Without them a log saying
            // "0 scheduled step(s)" cannot distinguish a server that had nothing from a
            // query that asked about one day, which is the question anybody diagnosing this
            // actually has.
            _logger.Information(
                $"The worklist returned {found} scheduled step(s) from " +
                $"{configuration.CalledAeTitle.Trim()}@{configuration.Host.Trim()}:{configuration.Port}. " +
                $"Asked for {criteria}.");

            return new WorklistQueryResult(
                true,
                ordered,
                ordered.Count == 0
                    ? WorklistMessages.Nothing(criteria)
                    : WorklistMessages.Found(ordered.Count, criteria),
                truncated);
        }
        catch (Exception ex)
        {
            _logger.Error($"The worklist query failed: {ex.Message}", ex);
            return new WorklistQueryResult(false, Array.Empty<WorklistEntry>(), Explain(ex, configuration));
        }
    }

    // ------------------------------------------------------------------ Query

    /// <summary>
    /// How many entries to hold before the sort, whatever the display maximum is.
    /// </summary>
    /// <remarks>
    /// Ten times the configured maximum, and never fewer than a thousand. The display
    /// maximum exists to keep the window usable; this exists only to stop a server with a
    /// runaway schedule exhausting memory, so it can be far larger. An entry is a handful of
    /// short strings, so even the ceiling is a few megabytes.
    /// </remarks>
    private static int CollectionCeiling(WorklistConfiguration configuration) =>
        Math.Max(1000, configuration.MaximumResults * 10);

    /// <summary>
    /// Describes what was asked for, in words, for the log and for the operator.
    /// </summary>
    private static string DescribeCriteria(
        WorklistConfiguration configuration,
        string? patientIdFilter,
        string? patientNameFilter,
        WorklistDateWindow? dateRangeOverride)
    {
        var parts = new List<string>();

        if (WorklistMatch.IsPresent(patientIdFilter))
        {
            parts.Add($"patient ID '{patientIdFilter!.Trim()}'");
        }

        if (WorklistMatch.IsPresent(patientNameFilter))
        {
            parts.Add($"name '{patientNameFilter!.Trim()}'");
        }

        parts.Add(DescribeDates(configuration, patientIdFilter, dateRangeOverride));

        if (!string.IsNullOrWhiteSpace(configuration.Modality))
        {
            // Named because a modality left over from an earlier configuration silently
            // excludes everything else, and nothing on screen says it is set.
            parts.Add($"modality '{configuration.Modality.Trim()}'");
        }

        if (!string.IsNullOrWhiteSpace(configuration.ScheduledStationAeTitle))
        {
            parts.Add($"scheduled station '{configuration.ScheduledStationAeTitle.Trim()}'");
        }

        return string.Join(", ", parts);
    }

    private static string DescribeDates(
        WorklistConfiguration configuration,
        string? patientIdFilter,
        WorklistDateWindow? dateRangeOverride)
    {
        if (WorklistMatch.IsPresent(patientIdFilter))
        {
            return "any date, because a patient ID was given";
        }

        string range = FormatDateRange(configuration, dateRangeOverride);

        return range.Length == 0 ? "any date" : $"dates {range}";
    }

    /// <summary>
    /// Builds the C-FIND request.
    /// </summary>
    private DicomCFindRequest BuildQuery(
        WorklistConfiguration configuration,
        string? patientIdFilter,
        string? patientNameFilter,
        WorklistDateWindow? dateRangeOverride)
    {
        var request = new DicomCFindRequest(
            DicomUID.ModalityWorklistInformationModelFind,
            DicomQueryRetrieveLevel.NotApplicable,
            DicomPriority.Medium);

        DicomDataset query = request.Dataset;

        // Declared so the server knows this client can accept accented and non Latin
        // names. Without it a server is entitled to transliterate or refuse, and a
        // Portuguese worklist is full of characters that would not survive that.
        query.AddOrUpdate(DicomTag.SpecificCharacterSet, DicomConstants.Utf8CharacterSet);

        // Return keys: sent empty so the server matches anything and reports the value.
        query.AddOrUpdate(DicomTag.PatientName, Match(patientNameFilter));
        query.AddOrUpdate(DicomTag.PatientID, Match(patientIdFilter));
        query.AddOrUpdate(DicomTag.PatientBirthDate, string.Empty);
        query.AddOrUpdate(DicomTag.PatientSex, string.Empty);
        query.AddOrUpdate(DicomTag.AccessionNumber, string.Empty);
        query.AddOrUpdate(DicomTag.ReferringPhysicianName, string.Empty);
        query.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);
        query.AddOrUpdate(DicomTag.RequestedProcedureDescription, string.Empty);
        query.AddOrUpdate(DicomTag.RequestedProcedureID, string.Empty);

        var step = new DicomDataset();
        step.AddOrUpdate(DicomTag.Modality, (configuration.Modality ?? string.Empty).Trim());
        step.AddOrUpdate(DicomTag.ScheduledStationAETitle, (configuration.ScheduledStationAeTitle ?? string.Empty).Trim());
        // A patient identifier overrides the date entirely.
        //
        // Asking "where is this patient's examination" and "what is scheduled today" at the
        // same time answers neither. An operator looking up one identifier has a document in
        // their hand for that patient, and the examination it belongs to is as likely to be
        // last month's as today's; a date restriction turns that search into a question
        // nobody meant to ask, and the result is an empty list that reads as "no such
        // patient". The scheduled date is a column in the list, so an operator who gets
        // several can still see which is which, and what was asked for is said on screen and
        // written to the log rather than being inferred.
        step.AddOrUpdate(
            DicomTag.ScheduledProcedureStepStartDate,
            WorklistMatch.IsPresent(patientIdFilter)
                ? string.Empty
                : FormatDateRange(configuration, dateRangeOverride));
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepStartTime, string.Empty);
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepDescription, string.Empty);
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepID, string.Empty);
        step.AddOrUpdate(DicomTag.ScheduledPerformingPhysicianName, string.Empty);

        query.AddOrUpdate(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, step));

        return request;
    }

    /// <summary>Normalises an optional filter into a matching key.</summary>
    private static string Match(string? filter) => (filter ?? string.Empty).Trim();

    /// <summary>
    /// Formats the configured date range the way DICOM range matching expects, or an empty
    /// value when every date should match.
    /// </summary>
    private static string FormatDateRange(
        WorklistConfiguration configuration, WorklistDateWindow? dateRangeOverride)
    {
        (DateTime From, DateTime To)? range = dateRangeOverride is null
            ? configuration.ResolveDates(DateTime.Today)
            : configuration.ResolveDates(DateTime.Today, dateRangeOverride);

        if (range is null)
        {
            return string.Empty;
        }

        string from = range.Value.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string to = range.Value.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        // A single day is sent as a plain date rather than a range of one. Some servers
        // handle the degenerate range poorly, and the plain form is unambiguous.
        return from == to ? from : $"{from}-{to}";
    }

    // ------------------------------------------------------------------ Response

    /// <summary>
    /// Turns one C-FIND response into an entry, or null when it carries nothing usable.
    /// </summary>
    /// <remarks>
    /// Deliberately forgiving. Worklist servers vary in which optional attributes they
    /// populate, and discarding an otherwise good entry because one field is absent would
    /// send the operator back to typing by hand, which is the thing this feature exists to
    /// prevent.
    /// </remarks>
    private WorklistEntry? TryReadEntry(DicomDataset dataset)
    {
        try
        {
            DicomDataset step = dataset.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out DicomSequence? sequence)
                && sequence.Items.Count > 0
                    ? sequence.Items[0]
                    : new DicomDataset();

            (string family, string given) = ReadName(dataset);

            var entry = new WorklistEntry
            {
                PatientId = Text(dataset, DicomTag.PatientID),
                FamilyName = family,
                GivenName = given,
                AccessionNumber = Text(dataset, DicomTag.AccessionNumber),
                DateOfBirth = Date(dataset, DicomTag.PatientBirthDate),
                Sex = PatientSexExtensions.FromDicomValue(Text(dataset, DicomTag.PatientSex)),
                StudyInstanceUid = Text(dataset, DicomTag.StudyInstanceUID),
                ReferringPhysicianName = ReadPhysicianName(dataset),
                RequestedProcedureDescription = Text(dataset, DicomTag.RequestedProcedureDescription),
                Modality = Text(step, DicomTag.Modality),
                ScheduledStart = DateAndTime(step),
                ScheduledProcedureStepDescription = Text(step, DicomTag.ScheduledProcedureStepDescription)
            };

            // An entry with neither an identifier nor a name cannot be acted on and would
            // only clutter the list.
            if (string.IsNullOrWhiteSpace(entry.PatientId) && string.IsNullOrWhiteSpace(entry.FamilyName))
            {
                return null;
            }

            return entry;
        }
        catch (Exception ex)
        {
            _logger.Warning($"A worklist entry could not be read and was skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Splits Patient's Name into its family and given components.
    /// </summary>
    /// <remarks>
    /// The middle name, prefix and suffix are read and discarded. The department uses only
    /// the two, and dropping the rest here means they cannot silently consume part of the
    /// sixty four characters the Person Name is allowed.
    /// </remarks>
    private static (string Family, string Given) ReadName(DicomDataset dataset)
    {
        string raw = Text(dataset, DicomTag.PatientName);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, string.Empty);
        }

        // Only the alphabetic component group is used. A group after "=" is the same name
        // in another script, not additional name text.
        string alphabetic = raw.Split('=')[0];
        string[] components = alphabetic.Split('^');

        string family = components.Length > 0 ? components[0].Trim() : string.Empty;
        string given = components.Length > 1 ? components[1].Trim() : string.Empty;

        return (family, given);
    }

    /// <summary>
    /// Reads Referring Physician's Name into the order a person writes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Person Name arrives as <c>Family^Given^Middle</c>. Replacing the carets with
    /// spaces, which is what this did at first, produces "COSTA MANUEL" and reads
    /// plausibly, and it is wrong: the form hands that text back to
    /// <c>PersonNameFormatter.FromFreeText</c>, which treats the last word as the family
    /// name. "COSTA MANUEL" therefore became <c>MANUEL^COSTA</c> and the physician's
    /// surname and given name were silently swapped on the way into the object. Spotted by
    /// dumping a generated object rather than by reading the code.
    /// </para>
    /// <para>
    /// Emitting "Given Middle Family" instead both reads naturally in the form and is
    /// reconstructed by that same formatter with the family name in the family position,
    /// which is what an archive indexes and what was being corrupted.
    /// </para>
    /// <para>
    /// It is not lossless, and the tests say so: one text field holds no record of where
    /// the given name ended, so a given component of several words comes back split across
    /// the given and middle ones. That is a redistribution within the forenames rather
    /// than a wrong name, and carrying it faithfully would mean giving the form a
    /// structured physician field, which is a great deal of interface for a Type 2
    /// attribute nobody searches on.
    /// </para>
    /// </remarks>
    private static string ReadPhysicianName(DicomDataset dataset)
    {
        string raw = Text(dataset, DicomTag.ReferringPhysicianName);

        if (raw.Length == 0)
        {
            return string.Empty;
        }

        // The alphabetic group only, as for the patient's name: a group after "=" is the
        // same name in another script.
        string[] components = raw.Split('=')[0].Split('^');

        string family = components.Length > 0 ? components[0].Trim() : string.Empty;
        string given = components.Length > 1 ? components[1].Trim() : string.Empty;
        string middle = components.Length > 2 ? components[2].Trim() : string.Empty;

        // Prefix and suffix are dropped: the single text field they would be folded into
        // cannot represent them, and inventing a position for them would be guesswork.
        return string.Join(
            ' ',
            new[] { given, middle, family }.Where(part => part.Length > 0));
    }

    private static string Text(DicomDataset dataset, DicomTag tag) =>
        dataset.GetSingleValueOrDefault(tag, string.Empty)?.Trim() ?? string.Empty;

    private static DateTime? Date(DicomDataset dataset, DicomTag tag)
    {
        string value = Text(dataset, tag);

        return DateTime.TryParseExact(
            value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
                ? parsed
                : null;
    }

    /// <summary>Combines the scheduled start date and time, tolerating a missing time.</summary>
    private static DateTime? DateAndTime(DicomDataset step)
    {
        DateTime? date = Date(step, DicomTag.ScheduledProcedureStepStartDate);

        if (date is null)
        {
            return null;
        }

        // Times arrive as HHmmss, sometimes with a fractional part, and sometimes as HHmm.
        string time = Text(step, DicomTag.ScheduledProcedureStepStartTime).Split('.')[0];

        if (time.Length >= 4
            && int.TryParse(time[..2], out int hours)
            && int.TryParse(time.Substring(2, 2), out int minutes))
        {
            int seconds = time.Length >= 6 && int.TryParse(time.Substring(4, 2), out int parsedSeconds)
                ? parsedSeconds
                : 0;

            if (hours is >= 0 and < 24 && minutes is >= 0 and < 60 && seconds is >= 0 and < 60)
            {
                return date.Value.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds);
            }
        }

        return date;
    }

    // ------------------------------------------------------------------ Plumbing

    private static string ResolveCallingAe(WorklistConfiguration configuration, string callingAeTitle) =>
        string.IsNullOrWhiteSpace(configuration.CallingAeTitleOverride)
            ? (callingAeTitle ?? string.Empty).Trim()
            : configuration.CallingAeTitleOverride.Trim();

    private static IDicomClient CreateClient(WorklistConfiguration configuration, string callingAeTitle)
    {
        IDicomClient client = DicomClientFactory.Create(
            configuration.Host.Trim(),
            configuration.Port,
            configuration.UseTls,
            callingAeTitle,
            configuration.CalledAeTitle.Trim());

        var timeout = TimeSpan.FromSeconds(Math.Max(1, configuration.TimeoutSeconds));

        client.ServiceOptions.RequestTimeout = timeout;
        client.ClientOptions.AssociationRequestTimeoutInMs = (int)timeout.TotalMilliseconds;
        client.ClientOptions.AssociationReleaseTimeoutInMs = (int)timeout.TotalMilliseconds;
        client.ClientOptions.MaximumNumberOfConsecutiveTimedOutAssociationRequests = 1;

        return client;
    }

    /// <summary>
    /// Turns an exception into something a person at a reception desk can act on.
    /// </summary>
    private static string Explain(Exception exception, WorklistConfiguration configuration)
    {
        string where = $"{configuration.CalledAeTitle.Trim()}@{configuration.Host.Trim()}:{configuration.Port}";

        return exception switch
        {
            DicomAssociationRejectedException =>
                $"{where} rejected the connection. Check that this workstation's AE Title is registered on the worklist server.",

            DicomNetworkException or System.Net.Sockets.SocketException =>
                $"{where} could not be reached. Check the address, the port and the network.",

            OperationCanceledException =>
                "The worklist query was cancelled.",

            _ => $"The worklist query failed: {exception.Message}"
        };
    }
}
