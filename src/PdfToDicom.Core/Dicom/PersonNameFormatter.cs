using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Builds DICOM Person Name (PN) values from separate name components.
/// The name deliberately avoids the toolkit's own PN type name to prevent ambiguity.
/// </summary>
/// <remarks>
/// A PN value is made of five components separated by a caret, in the order family name,
/// given name, middle name, prefix and suffix. Trailing empty components are omitted,
/// which is what conformant systems expect and what keeps the value inside the length
/// limit for the VR.
/// </remarks>
public static class PersonNameFormatter
{
    /// <summary>Builds a PN value from the patient name components of the supplied form values.</summary>
    public static string FromPatient(PatientDemographics demographics)
    {
        ArgumentNullException.ThrowIfNull(demographics);

        return Compose(
            demographics.FamilyName,
            demographics.GivenName,
            demographics.MiddleName,
            demographics.NamePrefix,
            demographics.NameSuffix);
    }

    /// <summary>
    /// Builds a PN value from free text entered as a single field, for example the
    /// referring physician. Text already containing a caret is assumed to be in DICOM
    /// form and is passed through; anything else is treated as "Given Family" and
    /// reordered, because that is how a clinician writes a colleague's name.
    /// </summary>
    public static string FromFreeText(string? text)
    {
        string value = (text ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.Contains('^'))
        {
            return DicomValueRules.Sanitise(value, DicomValueRules.MaxPersonNameGroupLength);
        }

        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            return Compose(parts[0], string.Empty, string.Empty, string.Empty, string.Empty);
        }

        string family = parts[^1];
        string given = parts[0];
        string middle = parts.Length > 2
            ? string.Join(' ', parts[1..^1])
            : string.Empty;

        return Compose(family, given, middle, string.Empty, string.Empty);
    }

    /// <summary>
    /// Joins the five components, removing characters that are illegal in a PN value,
    /// dropping trailing empty components and enforcing the length limit.
    /// </summary>
    public static string Compose(
        string? familyName,
        string? givenName,
        string? middleName,
        string? namePrefix,
        string? nameSuffix)
    {
        string[] components =
        {
            CleanComponent(familyName),
            CleanComponent(givenName),
            CleanComponent(middleName),
            CleanComponent(namePrefix),
            CleanComponent(nameSuffix)
        };

        // Trailing empty components carry no information and are conventionally omitted.
        int lastPopulated = Array.FindLastIndex(components, c => c.Length > 0);
        if (lastPopulated < 0)
        {
            return string.Empty;
        }

        string composed = string.Join('^', components[..(lastPopulated + 1)]);

        return composed.Length <= DicomValueRules.MaxPersonNameGroupLength
            ? composed
            : composed[..DicomValueRules.MaxPersonNameGroupLength];
    }

    /// <summary>
    /// Removes the caret and backslash separators and any control character from a single
    /// name component.
    /// </summary>
    private static string CleanComponent(string? component)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return string.Empty;
        }

        string cleaned = new(component
            .Where(c => c != '^' && c != '\\' && c != '=' && !char.IsControl(c))
            .ToArray());

        return cleaned.Trim();
    }
}
