using System.Globalization;
using System.Text;
using FellowOakDicom;
using Microsoft.Extensions.Logging;
using FellowOakDicom.Network;

namespace PdfToDicom.TestWorklistServer;

/// <summary>
/// Answers Modality Worklist C-FIND requests with the sample scheduled procedure steps.
/// </summary>
/// <remarks>
/// Matching is deliberately simple but honest about the two keys that matter in practice,
/// the scheduled date and the modality, so that configuring those in the application has a
/// visible effect rather than appearing to work because everything always comes back.
/// </remarks>
internal sealed class WorklistProvider : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCEchoProvider
{
    public WorklistProvider(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger logger,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, logger, dependencies)
    {
    }

    /// <inheritdoc />
    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        Console.WriteLine(
            $"  [{Timestamp()}] association from {association.CallingAE} " +
            $"at {association.RemoteHost}:{association.RemotePort}");

        foreach (DicomPresentationContext context in association.PresentationContexts)
        {
            bool supported =
                context.AbstractSyntax == DicomUID.Verification ||
                context.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind;

            context.SetResult(
                supported ? DicomPresentationContextResult.Accept : DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
        }

        return SendAssociationAcceptAsync(association);
    }

    /// <inheritdoc />
    public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();

    /// <inheritdoc />
    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) =>
        Console.WriteLine($"  [{Timestamp()}] association aborted: {source} {reason}");

    /// <inheritdoc />
    public void OnConnectionClosed(Exception? exception)
    {
        if (exception is not null)
        {
            Console.WriteLine($"  [{Timestamp()}] connection closed: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        Console.WriteLine($"  [{Timestamp()}] C-ECHO");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        if (request.SOPClassUID != DicomUID.ModalityWorklistInformationModelFind)
        {
            Console.WriteLine($"  [{Timestamp()}] C-FIND for an unsupported information model");
            yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveIdentifierDoesNotMatchSOPClass);
            yield break;
        }

        DicomDataset? step = request.Dataset.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out DicomSequence? sequence)
            && sequence.Items.Count > 0
                ? sequence.Items[0]
                : null;

        string requestedModality = step?.GetSingleValueOrDefault(DicomTag.Modality, string.Empty) ?? string.Empty;
        string requestedDates = step?.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartDate, string.Empty) ?? string.Empty;
        string requestedPatientId = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty) ?? string.Empty;
        string requestedName = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty) ?? string.Empty;

        Console.WriteLine(
            $"  [{Timestamp()}] C-FIND  modality='{requestedModality}'  dates='{requestedDates}'" +
            (string.IsNullOrEmpty(requestedPatientId) ? string.Empty : $"  id='{requestedPatientId}'") +
            (string.IsNullOrEmpty(requestedName) ? string.Empty : $"  name='{requestedName}'"));

        DateTime today = DateTime.Today;
        int matched = 0;

        foreach (SampleWorklist.Item item in SampleWorklist.Items)
        {
            if (!ModalityMatches(requestedModality, item.Modality)) { continue; }
            if (!DateMatches(requestedDates, today)) { continue; }
            if (!TextMatches(requestedPatientId, item.PatientId)) { continue; }
            if (!TextMatches(requestedName, item.FamilyName)) { continue; }

            matched++;
            yield return new DicomCFindResponse(request, DicomStatus.Pending)
            {
                Dataset = SampleWorklist.ToDataset(item, today)
            };
        }

        Console.WriteLine($"  [{Timestamp()}] returned {matched} entr{(matched == 1 ? "y" : "ies")}");

        yield return new DicomCFindResponse(request, DicomStatus.Success);
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Matching

    /// <summary>An empty key matches everything. That is DICOM universal matching.</summary>
    private static bool ModalityMatches(string requested, string actual) =>
        string.IsNullOrWhiteSpace(requested)
        || string.Equals(requested.Trim(), actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Handles both forms a date key arrives in: a single day, and a range written as
    /// "from-to". Everything here is scheduled today, so the question is only whether
    /// today falls inside what was asked for.
    /// </summary>
    private static bool DateMatches(string requested, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        string value = requested.Trim();
        string todayText = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        if (!value.Contains('-', StringComparison.Ordinal))
        {
            return value == todayText;
        }

        string[] ends = value.Split('-', 2);
        string from = ends[0].Trim();
        string to = ends[1].Trim();

        // An open ended range omits one side, which is legal.
        bool afterStart = string.IsNullOrEmpty(from) || string.CompareOrdinal(todayText, from) >= 0;
        bool beforeEnd = string.IsNullOrEmpty(to) || string.CompareOrdinal(todayText, to) <= 0;

        return afterStart && beforeEnd;
    }

    /// <summary>
    /// Matches a text key, honouring the "*" wildcard, which is what an operator searching
    /// by part of a surname will send.
    /// </summary>
    private static bool TextMatches(string requested, string actual)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        string pattern = requested.Trim().TrimEnd('^').Replace("*", string.Empty, StringComparison.Ordinal);

        return string.IsNullOrEmpty(pattern)
            || actual.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
