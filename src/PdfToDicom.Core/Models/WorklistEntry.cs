using System.Globalization;

namespace PdfToDicom.Core.Models;

/// <summary>
/// One scheduled procedure step returned by a Modality Worklist query.
/// </summary>
/// <remarks>
/// Holds the worklist's values exactly as they arrived, before any DICOM length limit is
/// applied. Shortening happens when an entry is turned into
/// <see cref="PatientDemographics"/>, so the operator can always be shown what the
/// worklist actually said.
/// </remarks>
public sealed record WorklistEntry
{
    /// <summary>Patient ID (0010,0020).</summary>
    public string PatientId { get; init; } = string.Empty;

    /// <summary>
    /// Family name, the first component of Patient's Name (0010,0010).
    /// </summary>
    public string FamilyName { get; init; } = string.Empty;

    /// <summary>
    /// Given name, the second component of Patient's Name.
    /// </summary>
    /// <remarks>
    /// The middle name component is deliberately not carried. The department does not use
    /// it, and dropping it here rather than later means it cannot quietly consume part of
    /// the sixty four characters a Person Name is allowed.
    /// </remarks>
    public string GivenName { get; init; } = string.Empty;

    /// <summary>Accession Number (0008,0050).</summary>
    public string AccessionNumber { get; init; } = string.Empty;

    /// <summary>Patient's Birth Date (0010,0030), when the worklist supplied one.</summary>
    public DateTime? DateOfBirth { get; init; }

    /// <summary>Patient's Sex (0010,0040).</summary>
    public PatientSex Sex { get; init; } = PatientSex.Unspecified;

    /// <summary>
    /// Study Instance UID (0020,000D) from the Requested Procedure.
    /// </summary>
    /// <remarks>
    /// The most valuable field in the whole response. Filing the document under the study
    /// the order already created is what makes it appear beside the images rather than in
    /// a study of its own.
    /// </remarks>
    public string StudyInstanceUid { get; init; } = string.Empty;

    /// <summary>Referring Physician's Name (0008,0090).</summary>
    public string ReferringPhysicianName { get; init; } = string.Empty;

    /// <summary>Requested Procedure Description (0032,1060).</summary>
    public string RequestedProcedureDescription { get; init; } = string.Empty;

    /// <summary>Modality of the scheduled step (0008,0060).</summary>
    public string Modality { get; init; } = string.Empty;

    /// <summary>Scheduled start of the procedure step, when the worklist supplied one.</summary>
    public DateTime? ScheduledStart { get; init; }

    /// <summary>Scheduled Procedure Step Description (0040,0007).</summary>
    public string ScheduledProcedureStepDescription { get; init; } = string.Empty;

    /// <summary>The patient's name as the worklist sent it, for display.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(GivenName)
            ? FamilyName
            : $"{FamilyName}, {GivenName}";

    /// <summary>Scheduled time for display, or an empty string when none was supplied.</summary>
    public string DisplayScheduled =>
        ScheduledStart?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Date of birth for display, or an empty string when none was supplied.</summary>
    public string DisplayDateOfBirth =>
        DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>What the procedure is, preferring the requested description.</summary>
    public string DisplayProcedure =>
        !string.IsNullOrWhiteSpace(RequestedProcedureDescription)
            ? RequestedProcedureDescription
            : ScheduledProcedureStepDescription;
}
