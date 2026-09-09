using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Validation;

/// <summary>
/// Enforces the business rules that govern the patient information form.
/// </summary>
/// <remarks>
/// Mandatory fields are Patient ID, Patient Name, Date of Birth and Accession Number.
/// Patient Sex is optional. Anything that would produce a non conformant DICOM value is
/// reported as an error so the operator can correct it before an object is created.
/// </remarks>
public sealed class PatientDemographicsValidator
{
    /// <summary>
    /// Earliest date of birth considered plausible. A date before this almost certainly
    /// indicates a data entry mistake such as a mistyped year.
    /// </summary>
    private static readonly DateTime EarliestPlausibleBirthDate = new(1875, 1, 1);

    private readonly Func<DateTime> _nowProvider;

    /// <summary>
    /// Creates a validator.
    /// </summary>
    /// <param name="nowProvider">
    /// Supplies the current local date. Injected so that the automated tests can
    /// validate the future dated birth date rule deterministically.
    /// </param>
    public PatientDemographicsValidator(Func<DateTime>? nowProvider = null)
    {
        _nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    /// <summary>
    /// Validates the supplied demographics.
    /// </summary>
    /// <param name="demographics">Operator supplied values.</param>
    /// <param name="characterSetSupportsUnicode">
    /// True when the configured Specific Character Set is UTF-8. When false, a name
    /// containing characters outside Latin 1 is reported so it is not silently corrupted.
    /// </param>
    public ValidationResult Validate(PatientDemographics demographics, bool characterSetSupportsUnicode = true)
    {
        ArgumentNullException.ThrowIfNull(demographics);
        var result = new ValidationResult();

        ValidatePatientId(demographics, result);
        ValidatePatientName(demographics, result, characterSetSupportsUnicode);
        ValidateBirthDate(demographics, result);
        ValidateAccessionNumber(demographics, result);
        ValidateOptionalFields(demographics, result);

        return result;
    }

    private static void ValidatePatientId(PatientDemographics demographics, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(demographics.PatientId))
        {
            result.AddError(nameof(demographics.PatientId), "Patient ID is mandatory and must not be empty.");
            return;
        }

        string id = demographics.PatientId.Trim();

        if (id.Length > DicomValueRules.MaxLongStringLength)
        {
            result.AddError(
                nameof(demographics.PatientId),
                $"Patient ID must not exceed {DicomValueRules.MaxLongStringLength} characters (currently {id.Length}).");
        }

        if (id.Contains('\\'))
        {
            result.AddError(nameof(demographics.PatientId), "Patient ID must not contain a backslash.");
        }

        if (id.Any(char.IsControl))
        {
            result.AddError(
                nameof(demographics.PatientId),
                "Patient ID contains a control character. If a barcode scanner was used, rescan the label.");
        }
    }

    private static void ValidatePatientName(
        PatientDemographics demographics,
        ValidationResult result,
        bool characterSetSupportsUnicode)
    {
        bool hasFamily = !string.IsNullOrWhiteSpace(demographics.FamilyName);
        bool hasGiven = !string.IsNullOrWhiteSpace(demographics.GivenName);

        if (!hasFamily && !hasGiven)
        {
            result.AddError(nameof(demographics.FamilyName), "Patient Name is mandatory. Enter at least a surname.");
            return;
        }

        if (!hasFamily)
        {
            result.AddWarning(
                nameof(demographics.FamilyName),
                "No surname was entered. Most PACS index patients by surname, so matching may be unreliable.");
        }

        // PN component groups are limited to 64 characters each. The five components are
        // joined with the caret separator, which also counts towards the limit.
        string combined = string.Join(
            '^',
            demographics.FamilyName.Trim(),
            demographics.GivenName.Trim(),
            demographics.MiddleName.Trim(),
            demographics.NamePrefix.Trim(),
            demographics.NameSuffix.Trim()).TrimEnd('^');

        if (combined.Length > DicomValueRules.MaxPersonNameGroupLength)
        {
            result.AddError(
                nameof(demographics.FamilyName),
                $"The combined patient name must not exceed {DicomValueRules.MaxPersonNameGroupLength} characters " +
                $"(currently {combined.Length}). Shorten one of the name components.");
        }

        foreach (var (field, value) in new[]
                 {
                     (nameof(demographics.FamilyName), demographics.FamilyName),
                     (nameof(demographics.GivenName), demographics.GivenName),
                     (nameof(demographics.MiddleName), demographics.MiddleName),
                     (nameof(demographics.NamePrefix), demographics.NamePrefix),
                     (nameof(demographics.NameSuffix), demographics.NameSuffix)
                 })
        {
            if (value.Contains('^'))
            {
                result.AddError(field, "Name components must not contain the caret character, which DICOM reserves as a separator.");
            }

            if (value.Contains('\\'))
            {
                result.AddError(field, "Name components must not contain a backslash.");
            }

            if (!characterSetSupportsUnicode && !DicomValueRules.IsRepresentableInLatin1(value))
            {
                result.AddError(
                    field,
                    "The name contains characters that the configured character set cannot represent. " +
                    "Switch the character set to UTF-8 (ISO_IR 192) in Settings, or transliterate the name.");
            }
        }
    }

    private void ValidateBirthDate(PatientDemographics demographics, ValidationResult result)
    {
        if (demographics.DateOfBirth is null)
        {
            result.AddError(nameof(demographics.DateOfBirth), "Date of Birth is mandatory. Enter a valid date.");
            return;
        }

        DateTime dob = demographics.DateOfBirth.Value.Date;
        DateTime today = _nowProvider().Date;

        if (dob > today)
        {
            result.AddError(nameof(demographics.DateOfBirth), "Date of Birth cannot be in the future.");
        }
        else if (dob < EarliestPlausibleBirthDate)
        {
            result.AddError(
                nameof(demographics.DateOfBirth),
                $"Date of Birth is earlier than {EarliestPlausibleBirthDate:yyyy}. Check the year that was entered.");
        }
    }

    private static void ValidateAccessionNumber(PatientDemographics demographics, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(demographics.AccessionNumber))
        {
            result.AddError(
                nameof(demographics.AccessionNumber),
                "Accession Number is mandatory. Without it the PACS cannot reconcile the document with the order.");
            return;
        }

        string accession = demographics.AccessionNumber.Trim();

        // Accession Number (0008,0050) has VR SH, limited to 16 characters.
        if (accession.Length > DicomValueRules.MaxShortStringLength)
        {
            result.AddError(
                nameof(demographics.AccessionNumber),
                $"Accession Number must not exceed {DicomValueRules.MaxShortStringLength} characters (currently {accession.Length}).");
        }

        if (accession.Contains('\\'))
        {
            result.AddError(nameof(demographics.AccessionNumber), "Accession Number must not contain a backslash.");
        }

        if (accession.Any(char.IsControl))
        {
            result.AddError(
                nameof(demographics.AccessionNumber),
                "Accession Number contains a control character. If a barcode scanner was used, rescan the label.");
        }
    }

    private static void ValidateOptionalFields(PatientDemographics demographics, ValidationResult result)
    {
        if (!string.IsNullOrWhiteSpace(demographics.ExistingStudyInstanceUid) &&
            !DicomValueRules.IsValidUid(demographics.ExistingStudyInstanceUid.Trim(), out string uidError))
        {
            result.AddError(nameof(demographics.ExistingStudyInstanceUid), $"Study Instance UID is not valid. {uidError}");
        }

        if (!string.IsNullOrWhiteSpace(demographics.StudyId) &&
            demographics.StudyId.Trim().Length > DicomValueRules.MaxShortStringLength)
        {
            result.AddError(
                nameof(demographics.StudyId),
                $"Study ID must not exceed {DicomValueRules.MaxShortStringLength} characters.");
        }

        foreach (var (field, value, limit) in new[]
                 {
                     (nameof(demographics.StudyDescription), demographics.StudyDescription, DicomValueRules.MaxLongStringLength),
                     (nameof(demographics.SeriesDescription), demographics.SeriesDescription, DicomValueRules.MaxLongStringLength),
                     (nameof(demographics.DocumentTitle), demographics.DocumentTitle, DicomValueRules.MaxShortTextLength)
                 })
        {
            if (!string.IsNullOrEmpty(value) && value.Trim().Length > limit)
            {
                result.AddWarning(field, $"The value exceeds {limit} characters and will be truncated.");
            }
        }
    }
}
