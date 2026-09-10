using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;

namespace PdfToDicom.Core.Services;

/// <summary>
/// Decides which study a conversion belongs to when several files are imported for one
/// patient.
/// </summary>
/// <remarks>
/// <para>
/// A DICOM study is one examination. Whether several imported files belong to one study
/// or to several is a clinical judgement that the software cannot make on its own, so it
/// is offered as a choice: keep each file in its own study, or gather everything imported
/// for the same patient into one.
/// </para>
/// <para>
/// Each file always gets its own Series Instance UID either way. That is what keeps the
/// files distinguishable inside a study, and it is why grouping does not lose anything: a
/// viewer still shows each document or image separately, it simply shows them together.
/// </para>
/// <para>
/// <b>The association is held in memory and is deliberately not persisted.</b> A patient
/// returning in three months is having a new examination, and their new report must not
/// silently join the study created for the old one. Restarting the application, or moving
/// to a different patient, starts a new study.
/// </para>
/// </remarks>
public sealed class StudyGroupingService
{
    private readonly object _gate = new();
    private string? _patientId;
    private string? _studyInstanceUid;

    /// <summary>The patient the current study is associated with, or null when none is.</summary>
    public string? CurrentPatientId
    {
        get { lock (_gate) { return _patientId; } }
    }

    /// <summary>
    /// The study identifiers currently being reused, or null when nothing is grouped yet.
    /// Shown to the operator so that grouping is visible rather than something that
    /// happens quietly.
    /// </summary>
    public string? CurrentStudyInstanceUid
    {
        get { lock (_gate) { return _studyInstanceUid; } }
    }

    /// <summary>
    /// Returns the Study Instance UID that a conversion for this patient should use, or
    /// null when a new study should be created.
    /// </summary>
    /// <param name="mode">The grouping the operator has chosen.</param>
    /// <param name="patientId">Patient ID for the conversion about to run.</param>
    public string? ResolveStudyInstanceUid(StudyGroupingMode mode, string? patientId)
    {
        if (mode != StudyGroupingMode.GroupByPatient || string.IsNullOrWhiteSpace(patientId))
        {
            return null;
        }

        lock (_gate)
        {
            // Comparison is ordinal and case sensitive. Patient identifiers are opaque
            // strings assigned by another system; treating "ab1" and "AB1" as the same
            // patient is a guess, and a wrong guess here files a document against the
            // wrong person.
            return string.Equals(_patientId, patientId.Trim(), StringComparison.Ordinal)
                ? _studyInstanceUid
                : null;
        }
    }

    /// <summary>
    /// Records the study a conversion actually used, so the next file for the same patient
    /// can join it.
    /// </summary>
    /// <remarks>
    /// Called with the identifier the builder used rather than one chosen in advance,
    /// because the operator may have typed a Study Instance UID of their own, which takes
    /// precedence over everything else.
    /// </remarks>
    public void Remember(StudyGroupingMode mode, string? patientId, string? studyInstanceUid)
    {
        if (mode != StudyGroupingMode.GroupByPatient
            || string.IsNullOrWhiteSpace(patientId)
            || string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            return;
        }

        lock (_gate)
        {
            _patientId = patientId.Trim();
            _studyInstanceUid = studyInstanceUid.Trim();
        }
    }

    /// <summary>
    /// Forgets the current association, so the next conversion starts a new study.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _patientId = null;
            _studyInstanceUid = null;
        }
    }
}
