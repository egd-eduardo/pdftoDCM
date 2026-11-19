using System.Globalization;
using System.Text;

namespace WorklistExplorer.Core.Query;

public sealed class QueryOptions
{
    public DateSelection Dates { get; init; } = DateSelection.Any;

    /// <summary>
    /// Rows kept per server before further rows are counted but not held. Only a guard
    /// against exhausting memory; reaching it is always reported with the number.
    /// </summary>
    public int Ceiling { get; init; } = DefaultCeiling;

    /// <summary>Folder for one text file per received dataset, or null when raw mode is off.</summary>
    public string? RawFolder { get; init; }

    public const int DefaultCeiling = 1_000_000;
}

public sealed record Discard(int ResponseNumber, string Reason);

/// <summary>
/// Everything that is known about one server's answer. Contains no patient data, so that
/// it can be logged and pasted into a report safely.
/// </summary>
public sealed class QueryReport
{
    public required string ServerName { get; init; }
    public required string Association { get; init; }
    public required string Question { get; init; }
    public string SentDataset { get; set; } = "";
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public TimeSpan Elapsed { get; set; }

    public int PendingResponses { get; set; }
    public int RowsKept { get; set; }
    public int RowsBeyondCeiling { get; set; }
    public int Ceiling { get; init; }
    public List<Discard> Discards { get; } = new();

    public int RowsWithoutStudyInstanceUid { get; set; }
    public int ResponsesWithoutSpsSequence { get; set; }
    public int ResponsesWithSeveralSpsItems { get; set; }

    /// <summary>Distinct Specific Character Set values the server declared, with counts. Empty key means none declared.</summary>
    public Dictionary<string, int> CharacterSets { get; } = new(StringComparer.Ordinal);

    public string FinalStatus { get; set; } = "no final status received";
    public bool FinalStatusIsSuccess { get; set; }
    public string? Failure { get; set; }
    public string? RawFolder { get; set; }
    public int RawFilesWritten { get; set; }

    public bool CeilingReached => RowsBeyondCeiling > 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine($"=== {ServerName} ===");
        sb.AppendLine($"Started:      {StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Association:  {Association}");
        sb.AppendLine($"Question:     {Question}");
        sb.AppendLine("Query dataset sent (an empty value is universal matching: match anything and report it):");
        sb.Append(Indent(SentDataset));
        sb.AppendLine($"Pending responses received: {PendingResponses.ToString("N0", ci)}");
        sb.AppendLine($"Rows kept:                  {RowsKept.ToString("N0", ci)}");
        if (CeilingReached)
        {
            sb.AppendLine($"CEILING REACHED: the limit of {Ceiling.ToString("N0", ci)} rows was reached; " +
                          $"{RowsBeyondCeiling.ToString("N0", ci)} further row(s) were received and counted but not kept. " +
                          "This answer is INCOMPLETE on screen and in the CSV.");
        }
        sb.AppendLine($"Discarded:                  {Discards.Count.ToString("N0", ci)}");
        foreach (var group in Discards.GroupBy(d => d.Reason))
        {
            var numbers = string.Join(", ", group.Take(20).Select(d => d.ResponseNumber));
            var more = group.Count() > 20 ? $" and {group.Count() - 20} more" : "";
            sb.AppendLine($"  {group.Count()} x {group.Key} (response numbers {numbers}{more})");
        }
        if (RowsWithoutStudyInstanceUid > 0)
            sb.AppendLine($"Rows with an empty or absent Study Instance UID: {RowsWithoutStudyInstanceUid.ToString("N0", ci)}");
        if (ResponsesWithoutSpsSequence > 0)
            sb.AppendLine($"Responses without a Scheduled Procedure Step Sequence: {ResponsesWithoutSpsSequence} (kept as rows with empty step fields)");
        if (ResponsesWithSeveralSpsItems > 0)
            sb.AppendLine($"Responses with more than one step item: {ResponsesWithSeveralSpsItems} (one row per item)");
        if (CharacterSets.Count > 0)
        {
            var sets = string.Join("; ", CharacterSets.Select(kv =>
                $"{(kv.Key.Length == 0 ? "none declared" : "'" + kv.Key + "'")} x {kv.Value}"));
            sb.AppendLine($"Specific Character Set declared by the server: {sets}");
            if (CharacterSets.ContainsKey(""))
                sb.AppendLine("  Note: responses without a declared character set are decoded as the DICOM default repertoire; accented names in them may be wrong.");
        }
        sb.AppendLine($"Final C-FIND status:        {FinalStatus}");
        if (Failure is not null) sb.AppendLine($"Failure:                    {Failure}");
        if (RawFolder is not null) sb.AppendLine($"Raw datasets written:       {RawFilesWritten} file(s) in {RawFolder}");
        sb.AppendLine($"Elapsed:                    {Elapsed.TotalSeconds.ToString("0.000", ci)} s");
        if (RowsKept == 0 && RowsBeyondCeiling == 0)
            sb.AppendLine($"RESULT: no scheduled procedures were returned; {Question}.");
        else
            sb.AppendLine($"RESULT: {(RowsKept + RowsBeyondCeiling).ToString("N0", ci)} scheduled procedure step(s) returned; {Question}.");
        return sb.ToString();
    }

    private static string Indent(string text) =>
        string.Concat(text.Split('\n').Where(l => l.Length > 0).Select(l => "    " + l.TrimEnd('\r') + Environment.NewLine));
}

public sealed class QueryResult
{
    public required QueryReport Report { get; init; }
    public required List<WorklistRow> Rows { get; init; }
}
