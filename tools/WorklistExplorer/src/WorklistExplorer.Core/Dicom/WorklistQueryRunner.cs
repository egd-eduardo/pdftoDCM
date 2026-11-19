using System.Diagnostics;
using System.Globalization;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using WorklistExplorer.Core.Query;

namespace WorklistExplorer.Core.Dicom;

public static class WorklistQueryRunner
{
    static WorklistQueryRunner()
    {
        // Single byte character sets such as ISO_IR 100 and ISO_IR 148 need the code page
        // provider on .NET 8. Registering it here costs nothing and avoids mojibake in
        // names from servers that do not answer in UTF-8.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>C-ECHO only. Returns a one line description of the outcome.</summary>
    public static async Task<string> EchoAsync(QueryTarget target, CancellationToken ct)
    {
        var problems = target.Problems();
        if (problems.Count > 0) return $"{target.Name}: not attempted; {string.Join("; ", problems)}.";

        var sw = Stopwatch.StartNew();
        DicomStatus? status = null;
        try
        {
            var client = DicomConnection.Create(target);
            var echo = new DicomCEchoRequest { OnResponseReceived = (_, response) => status = response.Status };
            await client.AddRequestAsync(echo);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, target.TimeoutSeconds)));
            await client.SendAsync(timeout.Token, DicomClientCancellationMode.ImmediatelyReleaseAssociation);
        }
        catch (Exception ex)
        {
            return $"{target.Name}: C-ECHO to {target.Endpoint} failed after {sw.Elapsed.TotalSeconds:0.000} s; {ex.GetType().Name}: {ex.Message}";
        }
        return status is null
            ? $"{target.Name}: C-ECHO to {target.Endpoint} received no response within {target.TimeoutSeconds} s."
            : $"{target.Name}: C-ECHO to {target.Endpoint} answered {DescribeStatus(status)} in {sw.Elapsed.TotalSeconds:0.000} s.";
    }

    public static async Task<QueryResult> RunAsync(QueryTarget target, QueryOptions options, IProgress<string>? log, CancellationToken ct)
    {
        var today = DateTime.Today;
        var report = new QueryReport
        {
            ServerName = target.Name,
            Association = DicomConnection.DescribeAssociation(target),
            Question = WorklistQueryBuilder.DescribeQuestion(target, options.Dates, today),
            Ceiling = options.Ceiling,
            RawFolder = options.RawFolder
        };
        var rows = new List<WorklistRow>();
        var result = new QueryResult { Report = report, Rows = rows };
        var sw = Stopwatch.StartNew();

        var problems = target.Problems();
        if (problems.Count > 0)
        {
            report.Failure = $"not attempted; {string.Join("; ", problems)}";
            report.Elapsed = sw.Elapsed;
            return result;
        }

        DicomDataset identifier;
        try
        {
            identifier = WorklistQueryBuilder.Build(target, options.Dates, today);
        }
        catch (InvalidOperationException ex)
        {
            report.Failure = $"not attempted; {ex.Message}";
            report.Elapsed = sw.Elapsed;
            return result;
        }

        // The query root is the Modality Worklist Information Model with query retrieve
        // level not applicable, so no Query/Retrieve Level attribute is added.
        var request = new DicomCFindRequest(DicomUID.ModalityWorklistInformationModelFind, DicomQueryRetrieveLevel.NotApplicable)
        {
            Dataset = identifier
        };
        // Formatted from the request object itself, after assignment, so what is shown is
        // what fo-dicom will put on the wire rather than what this code intended.
        report.SentDataset = DatasetFormatter.Format(request.Dataset, "universal matching, match anything and return it");

        log?.Report($"{target.Name}: association {report.Association}");
        log?.Report($"{target.Name}: {report.Question}");

        string? rawFolder = null;
        if (options.RawFolder is not null)
        {
            rawFolder = Path.Combine(options.RawFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{SafeFileName(target.Name)}");
            try
            {
                Directory.CreateDirectory(rawFolder);
                File.WriteAllText(Path.Combine(rawFolder, "000000_query_sent.txt"),
                    report.Association + Environment.NewLine + report.Question + Environment.NewLine + Environment.NewLine + report.SentDataset,
                    new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Refuse to query rather than run without the raw files the user asked for.
                report.Failure = $"not attempted; the raw folder {rawFolder} could not be written: {ex.Message}";
                report.Elapsed = sw.Elapsed;
                return result;
            }
            report.RawFolder = rawFolder;
        }

        // The inactivity watchdog is reset by every response, so a slow but steady answer
        // of many thousands of rows is never cut short, while a server that goes silent is.
        using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, target.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        var sync = new object();
        var responseNumber = 0;

        request.OnResponseReceived = (_, response) =>
        {
            lock (sync)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, target.TimeoutSeconds)));
                responseNumber++;
                var status = response.Status;

                if (status.State != DicomState.Pending)
                {
                    // The final response. It ends the operation and carries no match.
                    report.FinalStatus = DescribeStatus(status);
                    report.FinalStatusIsSuccess = status.State == DicomState.Success;
                    if (rawFolder is not null && response.HasDataset && response.Dataset is not null)
                        WriteRaw(rawFolder, responseNumber, "final", response.Dataset, report);
                    return;
                }

                report.PendingResponses++;
                if (!response.HasDataset || response.Dataset is null)
                {
                    report.Discards.Add(new Discard(responseNumber, "pending response carried no dataset"));
                    return;
                }

                if (rawFolder is not null)
                    WriteRaw(rawFolder, responseNumber, "pending", response.Dataset, report);

                List<WorklistRow> made;
                try
                {
                    made = ToRows(target.Name, responseNumber, response.Dataset, report);
                }
                catch (Exception ex)
                {
                    report.Discards.Add(new Discard(responseNumber, $"dataset could not be read: {ex.GetType().Name}: {ex.Message}"));
                    return;
                }

                foreach (var row in made)
                {
                    if (string.IsNullOrEmpty(row.StudyInstanceUID)) report.RowsWithoutStudyInstanceUid++;
                    if (rows.Count < options.Ceiling)
                    {
                        rows.Add(row);
                        report.RowsKept++;
                    }
                    else
                    {
                        // Counted, never silently dropped: the report names the ceiling.
                        report.RowsBeyondCeiling++;
                    }
                }

                if (report.PendingResponses % 1000 == 0)
                    log?.Report($"{target.Name}: {report.PendingResponses.ToString("N0", CultureInfo.InvariantCulture)} responses so far");
            }
        };

        try
        {
            var client = DicomConnection.Create(target);
            await client.AddRequestAsync(request);
            await client.SendAsync(linked.Token, DicomClientCancellationMode.ImmediatelyReleaseAssociation);
        }
        catch (OperationCanceledException)
        {
            // Reported below from the token states.
        }
        catch (Exception ex)
        {
            // Rejections, refused connections and TLS failures all arrive here; the type
            // name is kept because it often says more than the message.
            report.Failure = $"{ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException is not null) report.Failure += $" (inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message})";
        }

        lock (sync)
        {
            if (ct.IsCancellationRequested)
                report.Failure ??= "cancelled by the user; the rows received so far are kept and the answer is incomplete";
            else if (idle.IsCancellationRequested)
                report.Failure ??= $"no response for {target.TimeoutSeconds} s; the association was released and the answer may be incomplete";
            report.Elapsed = sw.Elapsed;
        }

        return result;
    }

    /// <summary>
    /// Turns one response into rows. Normally one; a response with several step items gives
    /// one row per item; a response without the sequence still gives a row, with empty
    /// step fields and a remark, because dropping it would hide that the patient exists.
    /// </summary>
    private static List<WorklistRow> ToRows(string server, int number, DicomDataset ds, QueryReport report)
    {
        ds.TryGetString(DicomTag.SpecificCharacterSet, out var charset);
        charset ??= "";
        report.CharacterSets[charset] = report.CharacterSets.GetValueOrDefault(charset) + 1;

        var items = ds.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var seq) ? seq.Items.ToList() : new List<DicomDataset>();
        var remarks = "";
        if (items.Count == 0)
        {
            report.ResponsesWithoutSpsSequence++;
            remarks = "no Scheduled Procedure Step item in this response";
            items.Add(new DicomDataset());
        }
        else if (items.Count > 1)
        {
            report.ResponsesWithSeveralSpsItems++;
            remarks = $"response holds {items.Count} step items; one row each";
        }

        return items.Select(sps => new WorklistRow
        {
            Server = server,
            ResponseNumber = number,
            PatientName = Get(ds, DicomTag.PatientName),
            PatientID = Get(ds, DicomTag.PatientID),
            PatientBirthDate = Get(ds, DicomTag.PatientBirthDate),
            PatientSex = Get(ds, DicomTag.PatientSex),
            AccessionNumber = Get(ds, DicomTag.AccessionNumber),
            ReferringPhysicianName = Get(ds, DicomTag.ReferringPhysicianName),
            StudyInstanceUID = Get(ds, DicomTag.StudyInstanceUID),
            RequestedProcedureDescription = Get(ds, DicomTag.RequestedProcedureDescription),
            RequestedProcedureID = Get(ds, DicomTag.RequestedProcedureID),
            Modality = Get(sps, DicomTag.Modality),
            ScheduledStationAETitle = Get(sps, DicomTag.ScheduledStationAETitle),
            ScheduledProcedureStepStartDate = Get(sps, DicomTag.ScheduledProcedureStepStartDate),
            ScheduledProcedureStepStartTime = Get(sps, DicomTag.ScheduledProcedureStepStartTime),
            ScheduledProcedureStepDescription = Get(sps, DicomTag.ScheduledProcedureStepDescription),
            ScheduledProcedureStepID = Get(sps, DicomTag.ScheduledProcedureStepID),
            ScheduledPerformingPhysicianName = Get(sps, DicomTag.ScheduledPerformingPhysicianName),
            Remarks = remarks
        }).ToList();
    }

    /// <summary>
    /// Returns the value as decoded by fo-dicom with the response's own character set, with
    /// nothing trimmed or changed. Absent or unreadable is an empty string, never a guess.
    /// </summary>
    private static string Get(DicomDataset ds, DicomTag tag)
    {
        try
        {
            return ds.TryGetString(tag, out var value) ? value ?? "" : "";
        }
        catch (DicomDataException)
        {
            return "";
        }
    }

    private static void WriteRaw(string folder, int number, string kind, DicomDataset ds, QueryReport report)
    {
        // Text rather than .dcm: a C-FIND identifier has no SOP Class or Instance UID, so it
        // cannot form a valid Part 10 file without inventing meta information.
        var path = Path.Combine(folder, $"{number:D6}_{kind}.txt");
        File.WriteAllText(path, DatasetFormatter.Format(ds, "sent by the server without a value"), new UTF8Encoding(true));
        report.RawFilesWritten++;
    }

    public static string DescribeStatus(DicomStatus status) =>
        $"{status.State}, '{status.Description}', status word 0x{status.Code:X4}";

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return string.IsNullOrEmpty(cleaned) ? "server" : cleaned;
    }
}
