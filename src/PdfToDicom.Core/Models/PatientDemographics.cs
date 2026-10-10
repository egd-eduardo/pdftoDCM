namespace PdfToDicom.Core.Models;

/// <summary>
/// The patient and order level information entered by the operator.
/// </summary>
/// <remarks>
/// This type is intentionally a plain data carrier. All rule enforcement lives in
/// <see cref="Validation.PatientDemographicsValidator"/> so that the same rules are
/// applied identically by the desktop application, the command line tool and the
/// automated tests.
/// </remarks>
public sealed class PatientDemographics
{
    /// <summary>Primary patient identifier, mapped to Patient ID (0010,0020). Mandatory.</summary>
    public string PatientId { get; set; } = string.Empty;

    /// <summary>Family (surname) component of Patient's Name (0010,0010). Mandatory.</summary>
    public string FamilyName { get; set; } = string.Empty;

    /// <summary>Given name component of Patient's Name (0010,0010).</summary>
    public string GivenName { get; set; } = string.Empty;

    /// <summary>Middle name component of Patient's Name (0010,0010).</summary>
    public string MiddleName { get; set; } = string.Empty;

    /// <summary>Name prefix component, for example "Dr".</summary>
    public string NamePrefix { get; set; } = string.Empty;

    /// <summary>Name suffix component, for example "Jr".</summary>
    public string NameSuffix { get; set; } = string.Empty;

    /// <summary>Date of birth, mapped to Patient's Birth Date (0010,0030). Mandatory.</summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Patient sex, mapped to Patient's Sex (0010,0040). Optional.</summary>
    public PatientSex Sex { get; set; } = PatientSex.Unspecified;

    /// <summary>Accession number, mapped to Accession Number (0008,0050). Mandatory.</summary>
    public string AccessionNumber { get; set; } = string.Empty;

    /// <summary>Referring physician name, mapped to (0008,0090). Optional, Type 2.</summary>
    public string ReferringPhysicianName { get; set; } = string.Empty;

    /// <summary>Study description, mapped to (0008,1030). Optional.</summary>
    public string StudyDescription { get; set; } = string.Empty;

    /// <summary>Series description, mapped to (0008,103E). Optional.</summary>
    public string SeriesDescription { get; set; } = string.Empty;

    /// <summary>Document title, mapped to (0042,0010). Optional.</summary>
    public string DocumentTitle { get; set; } = string.Empty;

    /// <summary>
    /// Optional Study Instance UID. When supplied the generated object is filed into an
    /// existing study rather than creating a new one. When left empty a new, globally
    /// unique Study Instance UID is generated.
    /// </summary>
    public string ExistingStudyInstanceUid { get; set; } = string.Empty;

    /// <summary>
    /// Optional Study ID (0020,0010). When empty a short value is derived from the
    /// accession number, because Study ID is a Type 2 attribute.
    /// </summary>
    public string StudyId { get; set; } = string.Empty;

    /// <summary>
    /// Optional Study Date and Time. When supplied, it is written to Study Date (0008,0020)
    /// and Study Time (0008,0030) in place of the moment of conversion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set when a document is imported from a file whose creation time is known. The study
    /// happened when the document was produced, not when this application got round to
    /// converting it, and a folder worked through in a batch would otherwise stamp every
    /// study with the same afternoon and sort uselessly in the archive.
    /// </para>
    /// <para>
    /// It moves the study timestamp only. Content Date and Time still record when this
    /// object was built, which is the truthful answer for them and is what an auditor
    /// reconciling the two would expect.
    /// </para>
    /// </remarks>
    public DateTime? StudyDateTime { get; set; }

    /// <summary>
    /// Creates an independent copy. Used when storing and restoring operator templates
    /// so that later edits to the form do not mutate the stored template.
    /// </summary>
    public PatientDemographics Clone() => new()
    {
        PatientId = PatientId,
        FamilyName = FamilyName,
        GivenName = GivenName,
        MiddleName = MiddleName,
        NamePrefix = NamePrefix,
        NameSuffix = NameSuffix,
        DateOfBirth = DateOfBirth,
        Sex = Sex,
        AccessionNumber = AccessionNumber,
        ReferringPhysicianName = ReferringPhysicianName,
        StudyDescription = StudyDescription,
        SeriesDescription = SeriesDescription,
        DocumentTitle = DocumentTitle,
        ExistingStudyInstanceUid = ExistingStudyInstanceUid,
        StudyId = StudyId,
        StudyDateTime = StudyDateTime
    };
}
