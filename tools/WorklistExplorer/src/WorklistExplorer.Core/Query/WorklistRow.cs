namespace WorklistExplorer.Core.Query;

/// <summary>
/// One scheduled procedure step, holding the values exactly as the server sent them.
/// Nothing is trimmed, title cased or normalised: the point is to show the truth.
/// An attribute the server did not send, or sent empty, stays an empty string.
/// </summary>
public sealed class WorklistRow
{
    /// <summary>Name of the configured worklist the row came from.</summary>
    public string Server { get; init; } = "";

    /// <summary>1 based number of the C-FIND response within that server's answer; matches the raw file name.</summary>
    public int ResponseNumber { get; init; }

    public string PatientName { get; init; } = "";
    public string PatientID { get; init; } = "";
    public string PatientBirthDate { get; init; } = "";
    public string PatientSex { get; init; } = "";
    public string AccessionNumber { get; init; } = "";
    public string ReferringPhysicianName { get; init; } = "";
    public string StudyInstanceUID { get; init; } = "";
    public string RequestedProcedureDescription { get; init; } = "";
    public string RequestedProcedureID { get; init; } = "";

    public string Modality { get; init; } = "";
    public string ScheduledStationAETitle { get; init; } = "";
    public string ScheduledProcedureStepStartDate { get; init; } = "";
    public string ScheduledProcedureStepStartTime { get; init; } = "";
    public string ScheduledProcedureStepDescription { get; init; } = "";
    public string ScheduledProcedureStepID { get; init; } = "";
    public string ScheduledPerformingPhysicianName { get; init; } = "";

    /// <summary>Remarks about how this row was formed, for example a missing sequence. Empty when nothing is unusual.</summary>
    public string Remarks { get; init; } = "";

    /// <summary>Column order and headings used by the grid, the CSV and sorting, kept in one place.</summary>
    public static IReadOnlyList<(string Header, string Property, Func<WorklistRow, string> Get)> Columns { get; } =
    [
        ("Server", nameof(Server), r => r.Server),
        ("Patient name", nameof(PatientName), r => r.PatientName),
        ("Patient ID", nameof(PatientID), r => r.PatientID),
        ("Birth date", nameof(PatientBirthDate), r => r.PatientBirthDate),
        ("Sex", nameof(PatientSex), r => r.PatientSex),
        ("Accession number", nameof(AccessionNumber), r => r.AccessionNumber),
        ("SPS start date", nameof(ScheduledProcedureStepStartDate), r => r.ScheduledProcedureStepStartDate),
        ("SPS start time", nameof(ScheduledProcedureStepStartTime), r => r.ScheduledProcedureStepStartTime),
        ("Modality", nameof(Modality), r => r.Modality),
        ("Scheduled station AE", nameof(ScheduledStationAETitle), r => r.ScheduledStationAETitle),
        ("SPS description", nameof(ScheduledProcedureStepDescription), r => r.ScheduledProcedureStepDescription),
        ("SPS ID", nameof(ScheduledProcedureStepID), r => r.ScheduledProcedureStepID),
        ("Requested procedure description", nameof(RequestedProcedureDescription), r => r.RequestedProcedureDescription),
        ("Requested procedure ID", nameof(RequestedProcedureID), r => r.RequestedProcedureID),
        ("Referring physician", nameof(ReferringPhysicianName), r => r.ReferringPhysicianName),
        ("Scheduled performing physician", nameof(ScheduledPerformingPhysicianName), r => r.ScheduledPerformingPhysicianName),
        ("Study Instance UID", nameof(StudyInstanceUID), r => r.StudyInstanceUID),
        ("Response number", nameof(ResponseNumber), r => r.ResponseNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("Remarks", nameof(Remarks), r => r.Remarks),
    ];
}
