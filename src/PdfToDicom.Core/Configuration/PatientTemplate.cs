using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Configuration;

/// <summary>
/// A reusable set of patient form values, saved by the operator to avoid retyping
/// values that repeat across documents, for example a referring physician or a
/// standard study description.
/// </summary>
/// <remarks>
/// Templates deliberately never store the Patient ID, patient name, date of birth or
/// accession number. Those identify a specific patient and reusing them is the single
/// most likely cause of a document being filed against the wrong person.
/// </remarks>
public sealed class PatientTemplate
{
    /// <summary>Friendly name shown in the template list.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Referring physician name applied by the template.</summary>
    public string ReferringPhysicianName { get; set; } = string.Empty;

    /// <summary>Study description applied by the template.</summary>
    public string StudyDescription { get; set; } = string.Empty;

    /// <summary>Series description applied by the template.</summary>
    public string SeriesDescription { get; set; } = string.Empty;

    /// <summary>Document title applied by the template.</summary>
    public string DocumentTitle { get; set; } = string.Empty;

    /// <summary>
    /// Applies this template to the supplied demographics, leaving the identifying
    /// fields untouched.
    /// </summary>
    public void ApplyTo(PatientDemographics demographics)
    {
        ArgumentNullException.ThrowIfNull(demographics);
        demographics.ReferringPhysicianName = ReferringPhysicianName;
        demographics.StudyDescription = StudyDescription;
        demographics.SeriesDescription = SeriesDescription;
        demographics.DocumentTitle = DocumentTitle;
    }

    /// <summary>Creates a template from the non identifying parts of the supplied form values.</summary>
    public static PatientTemplate CaptureFrom(string name, PatientDemographics demographics)
    {
        ArgumentNullException.ThrowIfNull(demographics);
        return new PatientTemplate
        {
            Name = name,
            ReferringPhysicianName = demographics.ReferringPhysicianName,
            StudyDescription = demographics.StudyDescription,
            SeriesDescription = demographics.SeriesDescription,
            DocumentTitle = demographics.DocumentTitle
        };
    }
}
