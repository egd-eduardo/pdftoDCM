using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Writes the audit trail required in a hospital environment.
/// </summary>
/// <remarks>
/// The audit log is deliberately separate from the diagnostic log. It records who did
/// what, to which patient record, and with what result, in a stable one line per event
/// format that can be reviewed or ingested by an information governance team.
/// </remarks>
public interface IAuditLogger
{
    /// <summary>Records that the application was started.</summary>
    void ApplicationStarted(string version);

    /// <summary>Records that the application was closed.</summary>
    void ApplicationClosed();

    /// <summary>Records that the operator selected a PDF document.</summary>
    void PdfSelected(PdfDocumentInfo document);

    /// <summary>Records the patient information submitted for conversion.</summary>
    void PatientInformationEntered(PatientDemographics demographics);

    /// <summary>
    /// Records that the patient identifiers were read from the name of the source file
    /// rather than typed by an operator.
    /// </summary>
    /// <remarks>
    /// Where a document's identifiers came from is an audit question in its own right. If a
    /// report is later found against the wrong patient, the first thing an information
    /// governance team needs to know is whether a person read the identifiers off a wristband
    /// or whether a file name supplied them, and which pattern interpreted that name.
    /// </remarks>
    void PatientInformationReadFromFilename(string fileName, string pattern);

    /// <summary>Records the identifiers of a generated object and where it was written.</summary>
    void DicomGenerated(string studyUid, string seriesUid, string sopUid, string? filePath);

    /// <summary>Records the outcome of a transmission attempt.</summary>
    void TransmissionAttempted(TransmissionResult result, string sopInstanceUid);

    /// <summary>Records that a generated file was deleted after successful transmission.</summary>
    void GeneratedFileDeleted(string filePath);

    /// <summary>Records a configuration change made by the operator.</summary>
    void ConfigurationChanged(string summary);

    /// <summary>Folder containing the audit log files.</summary>
    string AuditFolder { get; }
}
