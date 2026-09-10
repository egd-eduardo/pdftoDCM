using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Applies the validator that matches the object's SOP Class.
/// </summary>
/// <remarks>
/// The workflow does not need to know what kind of object it is holding, and should not:
/// deciding that in two places is how the two decisions come to disagree. It asks this
/// validator, which reads the SOP Class the object itself declares and applies the rules
/// for it.
/// </remarks>
public sealed class SopClassDicomValidator : IDicomValidator
{
    private readonly EncapsulatedPdfValidator _pdfValidator;
    private readonly SecondaryCaptureImageValidator _imageValidator;

    /// <summary>Creates the dispatching validator.</summary>
    public SopClassDicomValidator(
        EncapsulatedPdfValidator? pdfValidator = null,
        SecondaryCaptureImageValidator? imageValidator = null)
    {
        _pdfValidator = pdfValidator ?? new EncapsulatedPdfValidator();
        _imageValidator = imageValidator ?? new SecondaryCaptureImageValidator();
    }

    /// <inheritdoc />
    public ValidationResult Validate(DicomFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        string sopClass = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);

        if (sopClass == DicomConstants.SecondaryCaptureImageStorageSopClassUid)
        {
            return _imageValidator.Validate(file);
        }

        if (sopClass == DicomConstants.EncapsulatedPdfStorageSopClassUid)
        {
            return _pdfValidator.Validate(file);
        }

        // An object this software did not produce, or produced wrongly. Either way it must
        // not reach an archive, and saying which SOP Class was found is what makes the
        // cause obvious.
        var result = new ValidationResult();

        return result.AddError(
            "SOP Class UID",
            string.IsNullOrEmpty(sopClass)
                ? "The object does not declare a SOP Class UID, so it cannot be validated."
                : $"The object declares SOP Class '{sopClass}', which this software does not produce.");
    }
}
