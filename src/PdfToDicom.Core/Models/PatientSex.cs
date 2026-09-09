namespace PdfToDicom.Core.Models;

/// <summary>
/// Patient sex as defined by DICOM PS3.3 attribute Patient's Sex (0010,0040).
/// The attribute is Type 2, therefore <see cref="Unspecified"/> results in a
/// present but zero length element rather than an omitted element.
/// </summary>
public enum PatientSex
{
    /// <summary>No value supplied. Written as a zero length element.</summary>
    Unspecified = 0,

    /// <summary>Male, encoded as "M".</summary>
    Male = 1,

    /// <summary>Female, encoded as "F".</summary>
    Female = 2,

    /// <summary>Other, encoded as "O".</summary>
    Other = 3
}

/// <summary>
/// Conversion helpers between <see cref="PatientSex"/> and its DICOM representation.
/// </summary>
public static class PatientSexExtensions
{
    /// <summary>
    /// Returns the DICOM defined term for the supplied value. An empty string is
    /// returned for <see cref="PatientSex.Unspecified"/> because Patient's Sex is a
    /// Type 2 attribute and must be present even when unknown.
    /// </summary>
    public static string ToDicomValue(this PatientSex sex) => sex switch
    {
        PatientSex.Male => "M",
        PatientSex.Female => "F",
        PatientSex.Other => "O",
        _ => string.Empty
    };

    /// <summary>
    /// Parses a DICOM defined term back into a <see cref="PatientSex"/>.
    /// Unrecognised input is treated as unspecified rather than raising an
    /// exception, because this method is used when restoring saved templates.
    /// </summary>
    public static PatientSex FromDicomValue(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "M" => PatientSex.Male,
        "F" => PatientSex.Female,
        "O" => PatientSex.Other,
        _ => PatientSex.Unspecified
    };
}
