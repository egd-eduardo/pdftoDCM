using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Checks a generated Secondary Capture image against the parts of the standard that a
/// receiving archive will hold it to.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="EncapsulatedPdfValidator"/> for image objects. It exists
/// for the same reason: an object that is quietly non conformant is far worse than one
/// that fails here, because it reaches the patient's record before anybody notices.
/// </remarks>
public sealed class SecondaryCaptureImageValidator : IDicomValidator
{
    /// <summary>Attributes that must be present and must carry a value.</summary>
    private static readonly (DicomTag Tag, string Name)[] Type1Attributes =
    {
        (DicomTag.SOPClassUID, "SOP Class UID"),
        (DicomTag.SOPInstanceUID, "SOP Instance UID"),
        (DicomTag.StudyInstanceUID, "Study Instance UID"),
        (DicomTag.SeriesInstanceUID, "Series Instance UID"),
        (DicomTag.Modality, "Modality"),
        (DicomTag.SeriesNumber, "Series Number"),
        (DicomTag.ConversionType, "Conversion Type"),
        (DicomTag.ImageType, "Image Type"),
        (DicomTag.BurnedInAnnotation, "Burned In Annotation"),
        (DicomTag.SamplesPerPixel, "Samples per Pixel"),
        (DicomTag.PhotometricInterpretation, "Photometric Interpretation"),
        (DicomTag.Rows, "Rows"),
        (DicomTag.Columns, "Columns"),
        (DicomTag.BitsAllocated, "Bits Allocated"),
        (DicomTag.BitsStored, "Bits Stored"),
        (DicomTag.HighBit, "High Bit"),
        (DicomTag.PixelRepresentation, "Pixel Representation")
    };

    /// <summary>Attributes that must be present but that are permitted to be empty.</summary>
    private static readonly (DicomTag Tag, string Name)[] Type2Attributes =
    {
        (DicomTag.PatientName, "Patient's Name"),
        (DicomTag.PatientID, "Patient ID"),
        (DicomTag.PatientBirthDate, "Patient's Birth Date"),
        (DicomTag.PatientSex, "Patient's Sex"),
        (DicomTag.StudyDate, "Study Date"),
        (DicomTag.StudyTime, "Study Time"),
        (DicomTag.ReferringPhysicianName, "Referring Physician's Name"),
        (DicomTag.StudyID, "Study ID"),
        (DicomTag.AccessionNumber, "Accession Number"),
        (DicomTag.Manufacturer, "Manufacturer"),
        (DicomTag.InstanceNumber, "Instance Number"),
        (DicomTag.PatientOrientation, "Patient Orientation"),
        (DicomTag.ContentDate, "Content Date"),
        (DicomTag.ContentTime, "Content Time")
    };

    /// <inheritdoc />
    public ValidationResult Validate(DicomFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = new ValidationResult();
        DicomDataset dataset = file.Dataset;

        ValidateFileMeta(file, result);
        ValidateRequiredAttributes(dataset, result);
        ValidateIdentifiers(dataset, result);
        ValidateSopClass(dataset, result);
        ValidatePixelDescription(dataset, result);
        ValidatePixelData(dataset, result);

        return result;
    }

    private static void ValidateFileMeta(DicomFile file, ValidationResult result)
    {
        DicomFileMetaInformation meta = file.FileMetaInfo;

        if (!meta.Contains(DicomTag.TransferSyntaxUID))
        {
            result.AddError("FileMeta", "The file meta information does not declare a Transfer Syntax UID.");
        }

        if (!meta.Contains(DicomTag.ImplementationClassUID))
        {
            result.AddError("FileMeta", "The file meta information does not declare an Implementation Class UID.");
        }

        string metaSopClass = meta.GetSingleValueOrDefault(DicomTag.MediaStorageSOPClassUID, string.Empty);
        string datasetSopClass = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);

        if (string.IsNullOrEmpty(metaSopClass))
        {
            result.AddError("FileMeta", "Media Storage SOP Class UID is missing from the file meta information.");
        }
        else if (!string.IsNullOrEmpty(datasetSopClass) && metaSopClass != datasetSopClass)
        {
            result.AddError(
                "FileMeta",
                "Media Storage SOP Class UID in the file meta information does not match SOP Class UID in the data set.");
        }

        string metaSopInstance = meta.GetSingleValueOrDefault(DicomTag.MediaStorageSOPInstanceUID, string.Empty);
        string datasetSopInstance = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);

        if (string.IsNullOrEmpty(metaSopInstance))
        {
            result.AddError("FileMeta", "Media Storage SOP Instance UID is missing from the file meta information.");
        }
        else if (!string.IsNullOrEmpty(datasetSopInstance) && metaSopInstance != datasetSopInstance)
        {
            result.AddError(
                "FileMeta",
                "Media Storage SOP Instance UID in the file meta information does not match SOP Instance UID in the data set.");
        }
    }

    private static void ValidateRequiredAttributes(DicomDataset dataset, ValidationResult result)
    {
        foreach ((DicomTag tag, string name) in Type1Attributes)
        {
            if (!dataset.Contains(tag))
            {
                result.AddError(name, $"{name} {tag} is mandatory (Type 1) and is missing.");
                continue;
            }

            if (!HasValue(dataset, tag))
            {
                result.AddError(name, $"{name} {tag} is mandatory (Type 1) and must not be empty.");
            }
        }

        foreach ((DicomTag tag, string name) in Type2Attributes)
        {
            if (!dataset.Contains(tag))
            {
                result.AddError(name, $"{name} {tag} is required (Type 2) and must be present, even when empty.");
            }
        }
    }

    /// <summary>
    /// True when the attribute carries at least one value that is not blank.
    /// </summary>
    /// <remarks>
    /// Reading it as a single value would be wrong, and was: Image Type is
    /// "DERIVED\SECONDARY", two values, and asking for the single value of a two valued
    /// attribute yields nothing. That made a correctly formed object look as though it
    /// had an empty mandatory attribute. Numeric attributes such as Rows cannot be read
    /// as strings at all, so the value count is what is checked, and blankness only for
    /// the string representations where it means anything.
    /// </remarks>
    private static bool HasValue(DicomDataset dataset, DicomTag tag)
    {
        if (dataset.GetDicomItem<DicomItem>(tag) is not DicomElement element)
        {
            return dataset.Contains(tag);
        }

        if (element.Count == 0)
        {
            return false;
        }

        if (!element.ValueRepresentation.IsString)
        {
            return true;
        }

        return dataset.GetValues<string>(tag).Any(v => !string.IsNullOrWhiteSpace(v));
    }

    private static void ValidateIdentifiers(DicomDataset dataset, ValidationResult result)
    {
        foreach ((DicomTag tag, string name) in new[]
                 {
                     (DicomTag.SOPClassUID, "SOP Class UID"),
                     (DicomTag.SOPInstanceUID, "SOP Instance UID"),
                     (DicomTag.StudyInstanceUID, "Study Instance UID"),
                     (DicomTag.SeriesInstanceUID, "Series Instance UID")
                 })
        {
            string value = dataset.GetSingleValueOrDefault(tag, string.Empty);

            if (!string.IsNullOrEmpty(value) && !DicomValueRules.IsValidUid(value, out string error))
            {
                result.AddError(name, $"{name} is not a valid UID. {error}");
            }
        }

        string studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        string seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
        string sopUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);

        if (!string.IsNullOrEmpty(studyUid) && studyUid == seriesUid)
        {
            result.AddError("Study Instance UID", "The Study Instance UID and the Series Instance UID must not be identical.");
        }

        if (!string.IsNullOrEmpty(seriesUid) && seriesUid == sopUid)
        {
            result.AddError("Series Instance UID", "The Series Instance UID and the SOP Instance UID must not be identical.");
        }
    }

    private static void ValidateSopClass(DicomDataset dataset, ValidationResult result)
    {
        string sopClass = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);

        if (!string.IsNullOrEmpty(sopClass) &&
            sopClass != DicomConstants.SecondaryCaptureImageStorageSopClassUid)
        {
            result.AddError(
                "SOP Class UID",
                $"The object declares SOP Class '{sopClass}' but a Secondary Capture image must declare " +
                $"'{DicomConstants.SecondaryCaptureImageStorageSopClassUid}'.");
        }
    }

    /// <summary>
    /// Checks that the attributes describing the pixels agree with one another.
    /// </summary>
    /// <remarks>
    /// These combinations are constrained by PS3.3 C.7.6.3.1. A viewer that is handed an
    /// inconsistent set does not usually refuse it; it renders something wrong, which is
    /// the worst possible outcome for a clinical image.
    /// </remarks>
    private static void ValidatePixelDescription(DicomDataset dataset, ValidationResult result)
    {
        ushort samples = dataset.GetSingleValueOrDefault<ushort>(DicomTag.SamplesPerPixel, 0);
        string photometric = dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, string.Empty);
        ushort allocated = dataset.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 0);
        ushort stored = dataset.GetSingleValueOrDefault<ushort>(DicomTag.BitsStored, 0);
        ushort highBit = dataset.GetSingleValueOrDefault<ushort>(DicomTag.HighBit, 0);

        if (samples is not (1 or 3))
        {
            result.AddError(
                "Samples per Pixel",
                $"Samples per Pixel must be 1 for a greyscale image or 3 for a colour image, but {samples} was found.");
        }

        if (samples == 1 && photometric is not ("MONOCHROME1" or "MONOCHROME2" or "PALETTE COLOR"))
        {
            result.AddError(
                "Photometric Interpretation",
                $"A single sample image must be MONOCHROME1, MONOCHROME2 or PALETTE COLOR, but '{photometric}' was found.");
        }

        if (samples == 3)
        {
            if (photometric != "RGB" && !photometric.StartsWith("YBR", StringComparison.Ordinal))
            {
                result.AddError(
                    "Photometric Interpretation",
                    $"A three sample image must be RGB or one of the YBR forms, but '{photometric}' was found.");
            }

            // Required whenever Samples per Pixel is greater than one. Without it a viewer
            // cannot tell interleaved data from three separate colour planes, and the two
            // render completely differently.
            if (!dataset.Contains(DicomTag.PlanarConfiguration))
            {
                result.AddError(
                    "Planar Configuration",
                    "Planar Configuration (0028,0006) is mandatory when Samples per Pixel is greater than one.");
            }
        }

        if (stored > allocated)
        {
            result.AddError(
                "Bits Stored",
                $"Bits Stored ({stored}) cannot exceed Bits Allocated ({allocated}).");
        }

        if (stored > 0 && highBit != stored - 1)
        {
            result.AddError(
                "High Bit",
                $"High Bit must be Bits Stored minus one ({stored - 1}), but {highBit} was found.");
        }
    }

    /// <summary>
    /// Checks that the pixel data is exactly the length the other attributes promise.
    /// </summary>
    /// <remarks>
    /// A single padding byte is allowed, because every DICOM value must occupy an even
    /// number of bytes and a greyscale image with an odd number of pixels cannot do that
    /// on its own. Anything else means the buffer and the dimensions disagree, and a
    /// viewer would tear the image or read past its end.
    /// </remarks>
    private static void ValidatePixelData(DicomDataset dataset, ValidationResult result)
    {
        if (!dataset.Contains(DicomTag.PixelData))
        {
            result.AddError("Pixel Data", "Pixel Data (7FE0,0010) is mandatory and is missing.");
            return;
        }

        ushort rows = dataset.GetSingleValueOrDefault<ushort>(DicomTag.Rows, 0);
        ushort columns = dataset.GetSingleValueOrDefault<ushort>(DicomTag.Columns, 0);
        ushort samples = dataset.GetSingleValueOrDefault<ushort>(DicomTag.SamplesPerPixel, 0);
        ushort allocated = dataset.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 0);

        if (rows == 0 || columns == 0)
        {
            result.AddError("Pixel Data", "Rows and Columns must both be greater than zero.");
            return;
        }

        long expected = (long)rows * columns * samples * (allocated / 8);

        byte[] pixels;
        try
        {
            pixels = dataset.GetValues<byte>(DicomTag.PixelData);
        }
        catch (Exception ex)
        {
            result.AddError("Pixel Data", $"The pixel data could not be read from the data set: {ex.Message}");
            return;
        }

        if (pixels.Length != expected && pixels.Length != expected + 1)
        {
            result.AddError(
                "Pixel Data",
                $"The pixel data is {pixels.Length} bytes but {rows} rows by {columns} columns at " +
                $"{samples} sample(s) of {allocated} bits requires {expected}.");
        }

        if ((pixels.Length & 1) == 1)
        {
            result.AddError(
                "Pixel Data",
                $"The pixel data is {pixels.Length} bytes, which is odd. Every DICOM value must occupy an even number of bytes.");
        }
    }
}
