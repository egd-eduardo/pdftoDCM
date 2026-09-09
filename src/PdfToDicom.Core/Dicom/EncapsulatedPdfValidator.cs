using System.Text;
using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Checks a generated object against the Encapsulated PDF Information Object Definition
/// before it is written to disk or transmitted.
/// </summary>
/// <remarks>
/// The checks below mirror the attribute types defined in PS3.3: Type 1 attributes must
/// be present and carry a value, Type 2 attributes must be present but may be empty, and
/// identifiers must be syntactically valid. Catching a defect here yields a precise local
/// message instead of an opaque rejection status from the archive.
/// </remarks>
public sealed class EncapsulatedPdfValidator : IDicomValidator
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
        (DicomTag.InstanceNumber, "Instance Number"),
        (DicomTag.ConversionType, "Conversion Type"),
        (DicomTag.BurnedInAnnotation, "Burned In Annotation"),
        (DicomTag.MIMETypeOfEncapsulatedDocument, "MIME Type of Encapsulated Document"),
        (DicomTag.EncapsulatedDocument, "Encapsulated Document")
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
        (DicomTag.ContentDate, "Content Date"),
        (DicomTag.ContentTime, "Content Time"),
        (DicomTag.AcquisitionDateTime, "Acquisition DateTime"),
        (DicomTag.DocumentTitle, "Document Title"),
        (DicomTag.ConceptNameCodeSequence, "Concept Name Code Sequence")
    };

    /// <summary>The five byte signature that begins every PDF document.</summary>
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    /// <inheritdoc />
    public ValidationResult Validate(DicomFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = new ValidationResult();
        DicomDataset dataset = file.Dataset;

        ValidateFileMetaInformation(file, result);
        ValidateRequiredAttributes(dataset, result);
        ValidateIdentifiers(dataset, result);
        ValidateSopClass(dataset, result);
        ValidateEncapsulatedPayload(dataset, result);
        ValidateCharacterSet(dataset, result);
        ValidateDateAndTimeValues(dataset, result);

        return result;
    }

    /// <summary>
    /// Confirms that the Part 10 file meta group is complete and that it agrees with the
    /// data set. A mismatch between the meta group and the data set is one of the most
    /// common reasons an archive rejects an otherwise valid object.
    /// </summary>
    private static void ValidateFileMetaInformation(DicomFile file, ValidationResult result)
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

        string? metaSopClass = meta.GetSingleValueOrDefault<string?>(DicomTag.MediaStorageSOPClassUID, null);
        string? datasetSopClass = file.Dataset.GetSingleValueOrDefault<string?>(DicomTag.SOPClassUID, null);

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

        string? metaSopInstance = meta.GetSingleValueOrDefault<string?>(DicomTag.MediaStorageSOPInstanceUID, null);
        string? datasetSopInstance = file.Dataset.GetSingleValueOrDefault<string?>(DicomTag.SOPInstanceUID, null);

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

    /// <summary>Checks the Type 1 and Type 2 attributes of the composite IOD.</summary>
    private static void ValidateRequiredAttributes(DicomDataset dataset, ValidationResult result)
    {
        foreach ((DicomTag tag, string name) in Type1Attributes)
        {
            if (!dataset.Contains(tag))
            {
                result.AddError(name, $"{name} {tag} is mandatory (Type 1) and is missing.");
                continue;
            }

            if (tag == DicomTag.EncapsulatedDocument)
            {
                // Handled separately because it carries binary rather than string content.
                continue;
            }

            string value = dataset.GetSingleValueOrDefault(tag, string.Empty);
            if (string.IsNullOrWhiteSpace(value))
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

    /// <summary>Checks that every identifier is a syntactically valid UID.</summary>
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

        // Distinct identifiers are required at each level. Equal values would make the
        // instance unaddressable inside the archive.
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

    /// <summary>Confirms the object declares the Encapsulated PDF Storage SOP Class.</summary>
    private static void ValidateSopClass(DicomDataset dataset, ValidationResult result)
    {
        string sopClass = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);

        if (!string.IsNullOrEmpty(sopClass) &&
            sopClass != DicomConstants.EncapsulatedPdfStorageSopClassUid)
        {
            result.AddError(
                "SOP Class UID",
                $"The SOP Class UID is '{sopClass}' but an Encapsulated PDF object must declare " +
                $"'{DicomConstants.EncapsulatedPdfStorageSopClassUid}'.");
        }
    }

    /// <summary>
    /// Confirms the encapsulated payload is present, non empty, of even length and
    /// actually a PDF, and that the declared MIME type matches.
    /// </summary>
    private static void ValidateEncapsulatedPayload(DicomDataset dataset, ValidationResult result)
    {
        string mimeType = dataset.GetSingleValueOrDefault(DicomTag.MIMETypeOfEncapsulatedDocument, string.Empty);

        if (!string.Equals(mimeType, DicomConstants.PdfMimeType, StringComparison.OrdinalIgnoreCase))
        {
            result.AddError(
                "MIME Type of Encapsulated Document",
                $"The MIME type must be '{DicomConstants.PdfMimeType}' but '{mimeType}' was found.");
        }

        if (!dataset.TryGetValues(DicomTag.EncapsulatedDocument, out byte[] payload))
        {
            result.AddError("Encapsulated Document", "The encapsulated document could not be read from the data set.");
            return;
        }

        if (payload.Length == 0)
        {
            result.AddError("Encapsulated Document", "The encapsulated document is empty.");
            return;
        }

        if (payload.Length % 2 != 0)
        {
            result.AddError(
                "Encapsulated Document",
                "The encapsulated document has an odd length. Values with VR OB must be padded to an even length.");
        }

        if (payload.Length < PdfSignature.Length ||
            !payload.AsSpan(0, PdfSignature.Length).SequenceEqual(PdfSignature))
        {
            result.AddError(
                "Encapsulated Document",
                "The encapsulated content does not begin with a PDF header, so it is not a PDF document.");
        }
    }

    /// <summary>
    /// Confirms that a Specific Character Set is declared whenever any text value uses
    /// characters outside the DICOM default repertoire.
    /// </summary>
    private static void ValidateCharacterSet(DicomDataset dataset, ValidationResult result)
    {
        string characterSet = dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, string.Empty);

        DicomTag[] textTags =
        {
            DicomTag.PatientName,
            DicomTag.ReferringPhysicianName,
            DicomTag.StudyDescription,
            DicomTag.SeriesDescription,
            DicomTag.InstitutionName,
            DicomTag.DocumentTitle
        };

        bool needsExtendedRepertoire = textTags.Any(tag =>
            !DicomValueRules.IsPureAscii(dataset.GetSingleValueOrDefault(tag, string.Empty)));

        if (needsExtendedRepertoire && string.IsNullOrWhiteSpace(characterSet))
        {
            result.AddError(
                "Specific Character Set",
                "Text values contain characters outside the default repertoire but Specific Character Set (0008,0005) is not declared.");
        }

        if (!string.IsNullOrWhiteSpace(characterSet) &&
            characterSet is not ("ISO_IR 192" or "ISO_IR 100" or "ISO_IR 6" or "ISO 2022 IR 6"))
        {
            result.AddWarning(
                "Specific Character Set",
                $"The declared character set '{characterSet}' is unusual for this workflow. " +
                "Confirm the receiving archive supports it.");
        }
    }

    /// <summary>Confirms the date and time attributes are correctly formatted.</summary>
    private static void ValidateDateAndTimeValues(DicomDataset dataset, ValidationResult result)
    {
        foreach ((DicomTag tag, string name) in new[]
                 {
                     (DicomTag.StudyDate, "Study Date"),
                     (DicomTag.ContentDate, "Content Date"),
                     (DicomTag.InstanceCreationDate, "Instance Creation Date"),
                     (DicomTag.PatientBirthDate, "Patient's Birth Date")
                 })
        {
            string value = dataset.GetSingleValueOrDefault(tag, string.Empty);

            // These attributes are permitted to be empty; only a populated value is checked.
            if (value.Length > 0 &&
                (value.Length != 8 || !value.All(char.IsAsciiDigit)))
            {
                result.AddError(name, $"{name} must be formatted as YYYYMMDD but '{value}' was found.");
            }
        }

        foreach ((DicomTag tag, string name) in new[]
                 {
                     (DicomTag.StudyTime, "Study Time"),
                     (DicomTag.ContentTime, "Content Time"),
                     (DicomTag.InstanceCreationTime, "Instance Creation Time")
                 })
        {
            string value = dataset.GetSingleValueOrDefault(tag, string.Empty);

            if (value.Length > 0 && !IsValidDicomTime(value))
            {
                result.AddError(name, $"{name} must be formatted as HHMMSS but '{value}' was found.");
            }
        }
    }

    /// <summary>
    /// Checks a DICOM TM value, which is HHMMSS optionally followed by a fractional
    /// second component introduced by a full stop.
    /// </summary>
    private static bool IsValidDicomTime(string value)
    {
        string wholeSeconds = value.Split('.')[0];

        return wholeSeconds.Length is 2 or 4 or 6 && wholeSeconds.All(char.IsAsciiDigit);
    }
}
