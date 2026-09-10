using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Builds an Encapsulated PDF Storage SOP Instance from a PDF file and the patient
/// information entered by the operator.
/// </summary>
/// <remarks>
/// <para>
/// The object produced satisfies the Encapsulated PDF Information Object Definition of
/// DICOM PS3.3 Annex A.45.1, which is composed of the Patient, General Study,
/// Encapsulated Document Series, General Equipment, SC Equipment, Encapsulated Document
/// and SOP Common modules.
/// </para>
/// <para>
/// Encoding is Explicit VR Little Endian. That syntax is uncompressed and unambiguous,
/// every conformant archive accepts it, and it avoids any dependency on native codec
/// libraries, which keeps the deployed application a single self contained executable.
/// </para>
/// </remarks>
public sealed class EncapsulatedPdfBuilder : IDicomObjectBuilder
{
    private readonly AppConfiguration _configuration;
    private readonly IUidGenerator _uidGenerator;
    private readonly IPdfInspector _pdfInspector;
    private readonly IAppLogger _logger;
    private readonly Func<DateTime> _nowProvider;

    /// <summary>
    /// Creates the builder.
    /// </summary>
    /// <param name="configuration">Site configuration supplying institution and encoding settings.</param>
    /// <param name="uidGenerator">Source of new identifiers.</param>
    /// <param name="pdfInspector">Used to determine the page count when it was not already read.</param>
    /// <param name="logger">Diagnostic log.</param>
    /// <param name="nowProvider">
    /// Supplies the current local time. Injected so that the tests can assert on the
    /// date and time attributes deterministically.
    /// </param>
    public EncapsulatedPdfBuilder(
        AppConfiguration configuration,
        IUidGenerator uidGenerator,
        IPdfInspector pdfInspector,
        IAppLogger logger,
        Func<DateTime>? nowProvider = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _uidGenerator = uidGenerator ?? throw new ArgumentNullException(nameof(uidGenerator));
        _pdfInspector = pdfInspector ?? throw new ArgumentNullException(nameof(pdfInspector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nowProvider = nowProvider ?? (() => DateTime.Now);

        // Guarantees that the implementation identity is applied before any object is
        // saved, regardless of how the host wired the application together.
        DicomRuntime.EnsureInitialized();
    }

    /// <inheritdoc />
    public BuiltDicomObject Build(
        string pdfPath,
        PatientDemographics demographics,
        PdfDocumentInfo? pdfInfo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentNullException.ThrowIfNull(demographics);

        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException("The PDF to encapsulate was not found.", pdfPath);
        }

        pdfInfo ??= _pdfInspector.Inspect(pdfPath);

        byte[] pdfBytes = File.ReadAllBytes(pdfPath);
        if (pdfBytes.Length == 0)
        {
            throw new InvalidOperationException("The PDF to encapsulate is empty.");
        }

        DateTime now = _nowProvider();

        // A Study Instance UID supplied by the operator files the document into an
        // existing study. Otherwise a new study is created for it.
        bool reusingStudy = !string.IsNullOrWhiteSpace(demographics.ExistingStudyInstanceUid);
        string studyUid = reusingStudy
            ? demographics.ExistingStudyInstanceUid.Trim()
            : _uidGenerator.NewStudyInstanceUid();

        string seriesUid = _uidGenerator.NewSeriesInstanceUid();
        string sopUid = _uidGenerator.NewSopInstanceUid();

        _logger.Information(
            $"Generating an Encapsulated PDF object. Study {studyUid} ({(reusingStudy ? "supplied by the operator" : "newly generated")}), " +
            $"series {seriesUid}, instance {sopUid}.");

        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);

        // Specific Character Set must be written before any text value so that the
        // toolkit encodes subsequent strings with the intended character repertoire.
        AddSpecificCharacterSet(dataset);

        AddPatientModule(dataset, demographics);
        AddGeneralStudyModule(dataset, demographics, studyUid, now);
        AddEncapsulatedDocumentSeriesModule(dataset, seriesUid);
        AddGeneralEquipmentModule(dataset);
        AddScEquipmentModule(dataset);
        AddEncapsulatedDocumentModule(dataset, demographics, pdfInfo, pdfBytes, now);
        AddSopCommonModule(dataset, sopUid, now);

        DicomFile file = CreateFileWithMetaInformation(dataset, sopUid);

        string suggestedFileName = BuildFileName(demographics, sopUid);

        return new BuiltDicomObject(file, studyUid, seriesUid, sopUid, suggestedFileName);
    }

    // ------------------------------------------------------------------ Modules

    /// <summary>
    /// Writes Specific Character Set (0008,0005). The attribute is Type 1C and is
    /// required whenever any value uses an extended repertoire; it is always written
    /// here so the receiving system never has to guess the encoding.
    /// </summary>
    private void AddSpecificCharacterSet(DicomDataset dataset)
    {
        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, _configuration.SpecificCharacterSetValue);
    }

    /// <summary>Writes the Patient module, PS3.3 C.7.1.1.</summary>
    private void AddPatientModule(DicomDataset dataset, PatientDemographics demographics)
    {
        string patientName = PersonNameFormatter.FromPatient(demographics);

        dataset.AddOrUpdate(DicomTag.PatientName, patientName);
        dataset.AddOrUpdate(
            DicomTag.PatientID,
            DicomValueRules.Sanitise(demographics.PatientId, DicomValueRules.MaxLongStringLength));

        // Patient's Birth Date is Type 2, so it is written even when unknown. The
        // validator prevents that in practice, but the encoder must not depend on it.
        dataset.AddOrUpdate(
            DicomTag.PatientBirthDate,
            demographics.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty);

        dataset.AddOrUpdate(DicomTag.PatientSex, demographics.Sex.ToDicomValue());
    }

    /// <summary>Writes the General Study module, PS3.3 C.7.2.1.</summary>
    private void AddGeneralStudyModule(
        DicomDataset dataset,
        PatientDemographics demographics,
        string studyUid,
        DateTime now)
    {
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
        dataset.AddOrUpdate(DicomTag.StudyDate, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        dataset.AddOrUpdate(DicomTag.StudyTime, now.ToString("HHmmss", CultureInfo.InvariantCulture));

        dataset.AddOrUpdate(
            DicomTag.AccessionNumber,
            DicomValueRules.Sanitise(demographics.AccessionNumber, DicomValueRules.MaxShortStringLength));

        // Referring Physician's Name is Type 2: present, possibly empty.
        dataset.AddOrUpdate(
            DicomTag.ReferringPhysicianName,
            PersonNameFormatter.FromFreeText(demographics.ReferringPhysicianName));

        // Study ID is Type 2. When the operator does not supply one, a value derived from
        // the accession number keeps the study identifiable to the department.
        string studyId = !string.IsNullOrWhiteSpace(demographics.StudyId)
            ? demographics.StudyId
            : demographics.AccessionNumber;

        dataset.AddOrUpdate(
            DicomTag.StudyID,
            DicomValueRules.Sanitise(studyId, DicomValueRules.MaxShortStringLength));

        string studyDescription = FirstNonEmpty(
            demographics.StudyDescription,
            _configuration.DefaultStudyDescription);

        if (!string.IsNullOrEmpty(studyDescription))
        {
            dataset.AddOrUpdate(
                DicomTag.StudyDescription,
                DicomValueRules.Sanitise(studyDescription, DicomValueRules.MaxLongStringLength));
        }
    }

    /// <summary>Writes the Encapsulated Document Series module, PS3.3 C.24.1.</summary>
    private void AddEncapsulatedDocumentSeriesModule(DicomDataset dataset, string seriesUid)
    {
        // Modality is Type 1 in this module. "OT" is the default because it is universally
        // accepted; sites whose archive expects the newer "DOC" value can configure it.
        dataset.AddOrUpdate(
            DicomTag.Modality,
            DicomValueRules.Sanitise(_configuration.Modality, DicomValueRules.MaxShortStringLength));

        dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
        dataset.AddOrUpdate(
            DicomTag.SeriesNumber,
            _configuration.SeriesNumber.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes the General Equipment module, PS3.3 C.7.5.1.</summary>
    private void AddGeneralEquipmentModule(DicomDataset dataset)
    {
        // Manufacturer is Type 2 and must always be present.
        dataset.AddOrUpdate(
            DicomTag.Manufacturer,
            DicomValueRules.Sanitise(_configuration.Manufacturer, DicomValueRules.MaxLongStringLength));

        AddIfNotEmpty(dataset, DicomTag.InstitutionName, _configuration.InstitutionName, DicomValueRules.MaxLongStringLength);
        AddIfNotEmpty(dataset, DicomTag.InstitutionalDepartmentName, _configuration.DepartmentName, DicomValueRules.MaxLongStringLength);
        AddIfNotEmpty(dataset, DicomTag.StationName, _configuration.StationName, DicomValueRules.MaxShortStringLength);

        dataset.AddOrUpdate(DicomTag.ManufacturerModelName, "PDF to DICOM Converter");
        dataset.AddOrUpdate(DicomTag.SoftwareVersions, ProductVersion);
    }

    /// <summary>
    /// Writes the SC Equipment module, PS3.3 C.8.6.1. Conversion Type (0008,0064) is
    /// Type 1 and describes how the object came into being.
    /// </summary>
    private void AddScEquipmentModule(DicomDataset dataset)
    {
        dataset.AddOrUpdate(
            DicomTag.ConversionType,
            DicomValueRules.Sanitise(_configuration.ConversionType, DicomValueRules.MaxShortStringLength));
    }

    /// <summary>Writes the Encapsulated Document module, PS3.3 C.24.2.</summary>
    private void AddEncapsulatedDocumentModule(
        DicomDataset dataset,
        PatientDemographics demographics,
        PdfDocumentInfo pdfInfo,
        byte[] pdfBytes,
        DateTime now)
    {
        dataset.AddOrUpdate(
            DicomTag.InstanceNumber,
            _configuration.InstanceNumber.ToString(CultureInfo.InvariantCulture));

        dataset.AddOrUpdate(DicomTag.ContentDate, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        dataset.AddOrUpdate(DicomTag.ContentTime, now.ToString("HHmmss", CultureInfo.InvariantCulture));

        // Acquisition DateTime is Type 2 in this module. The moment of conversion is the
        // most defensible value available for a document produced by another application.
        dataset.AddOrUpdate(
            DicomTag.AcquisitionDateTime,
            now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

        // Burned In Annotation is Type 1. A clinical report normally prints the patient
        // identity on the page, so "YES" is the truthful default for this workflow.
        dataset.AddOrUpdate(
            DicomTag.BurnedInAnnotation,
            _configuration.BurnedInAnnotation ? "YES" : "NO");

        string documentTitle = FirstNonEmpty(
            demographics.DocumentTitle,
            pdfInfo.Title,
            _configuration.DefaultDocumentTitle,
            "Clinical Document");

        dataset.AddOrUpdate(
            DicomTag.DocumentTitle,
            DicomValueRules.Sanitise(documentTitle, DicomValueRules.MaxShortTextLength));

        // Concept Name Code Sequence (0040,A043) is Type 2. Populating it with the DICOM
        // controlled term for a document title improves indexing on archives that use it
        // and is inert on those that do not.
        if (_configuration.IncludeConceptNameCodeSequence)
        {
            dataset.AddOrUpdate(new DicomSequence(
                DicomTag.ConceptNameCodeSequence,
                new DicomDataset
                {
                    { DicomTag.CodeValue, DicomConstants.DocumentTitleCodeValue },
                    { DicomTag.CodingSchemeDesignator, DicomConstants.DcmCodingScheme },
                    { DicomTag.CodeMeaning, DicomConstants.DocumentTitleCodeMeaning }
                }));
        }
        else
        {
            dataset.AddOrUpdate(new DicomSequence(DicomTag.ConceptNameCodeSequence));
        }

        string seriesDescription = FirstNonEmpty(
            demographics.SeriesDescription,
            _configuration.DefaultSeriesDescription);

        if (!string.IsNullOrEmpty(seriesDescription))
        {
            dataset.AddOrUpdate(
                DicomTag.SeriesDescription,
                DicomValueRules.Sanitise(seriesDescription, DicomValueRules.MaxLongStringLength));
        }

        dataset.AddOrUpdate(DicomTag.MIMETypeOfEncapsulatedDocument, DicomConstants.PdfMimeType);

        // The Encapsulated Document element has VR OB, whose value must be of even
        // length. A single null byte is appended when necessary, which PDF readers
        // ignore because the document is delimited by its own trailer.
        byte[] payload = pdfBytes;
        if (payload.Length % 2 != 0)
        {
            payload = new byte[pdfBytes.Length + 1];
            Buffer.BlockCopy(pdfBytes, 0, payload, 0, pdfBytes.Length);
            payload[^1] = 0x00;
        }

        dataset.AddOrUpdate(new DicomOtherByte(DicomTag.EncapsulatedDocument, new MemoryByteBuffer(payload)));

        AddDocumentClassCodeSequence(dataset);
        AddPageCountPrivateAttribute(dataset, pdfInfo);
    }

    /// <summary>
    /// Writes Document Class Code Sequence (0040,E008), which classifies the kind of
    /// document being encapsulated. It is optional in the standard but several archives
    /// use it when presenting document lists to clinicians.
    /// </summary>
    private void AddDocumentClassCodeSequence(DicomDataset dataset)
    {
        if (!_configuration.IncludeConceptNameCodeSequence)
        {
            return;
        }

        dataset.AddOrUpdate(new DicomSequence(
            DicomTag.DocumentClassCodeSequence,
            new DicomDataset
            {
                { DicomTag.CodeValue, DicomConstants.DocumentTitleCodeValue },
                { DicomTag.CodingSchemeDesignator, DicomConstants.DcmCodingScheme },
                { DicomTag.CodeMeaning, DicomConstants.DocumentTitleCodeMeaning }
            }));
    }

    /// <summary>
    /// Records the PDF page count.
    /// </summary>
    /// <remarks>
    /// The standard defines no attribute for the page count of an encapsulated document,
    /// so the value is written into a correctly reserved private block rather than being
    /// forced into an unrelated standard attribute. Private data elements are permitted by
    /// PS3.5 section 7.8 and are ignored by systems that do not recognise them, but the
    /// behaviour can be switched off for archives that reject unknown private data.
    /// </remarks>
    private void AddPageCountPrivateAttribute(DicomDataset dataset, PdfDocumentInfo pdfInfo)
    {
        if (!_configuration.RecordPageCountInPrivateTag || pdfInfo.PageCount is not > 0)
        {
            return;
        }

        try
        {
            var creator = DicomDictionary.Default.GetPrivateCreator(DicomConstants.PrivateCreator);
            DicomTag pageCountTag = dataset.GetPrivateTag(
                new DicomTag(DicomConstants.PrivateGroup, 0x01, creator));

            dataset.AddOrUpdate(new DicomIntegerString(
                pageCountTag,
                pdfInfo.PageCount.Value.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception ex)
        {
            // The page count is informational. Failing to record it must never prevent a
            // clinical document from reaching the archive.
            _logger.Warning($"The PDF page count could not be recorded in a private attribute: {ex.Message}");
        }
    }

    /// <summary>Writes the SOP Common module, PS3.3 C.12.1.</summary>
    private void AddSopCommonModule(DicomDataset dataset, string sopUid, DateTime now)
    {
        dataset.AddOrUpdate(DicomTag.SOPClassUID, DicomConstants.EncapsulatedPdfStorageSopClassUid);
        dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);

        dataset.AddOrUpdate(DicomTag.InstanceCreationDate, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        dataset.AddOrUpdate(DicomTag.InstanceCreationTime, now.ToString("HHmmss", CultureInfo.InvariantCulture));
        dataset.AddOrUpdate(DicomTag.InstanceCreatorUID, _uidGenerator.ImplementationClassUid);

        // Recording the offset from UTC removes any ambiguity about the local timestamps
        // above, which matters when an archive serves sites in different time zones.
        dataset.AddOrUpdate(DicomTag.TimezoneOffsetFromUTC, FormatUtcOffset(now));
    }

    // ------------------------------------------------------------------ File meta

    /// <summary>
    /// Wraps the data set in a DICOM file and populates the File Meta Information group,
    /// which is what makes the result a valid Part 10 file rather than a bare data set.
    /// </summary>
    private DicomFile CreateFileWithMetaInformation(DicomDataset dataset, string sopUid)
    {
        var file = new DicomFile(dataset);

        file.FileMetaInfo.AddOrUpdate(
            DicomTag.FileMetaInformationVersion,
            new byte[] { 0x00, 0x01 });

        file.FileMetaInfo.AddOrUpdate(
            DicomTag.MediaStorageSOPClassUID,
            DicomConstants.EncapsulatedPdfStorageSopClassUid);

        file.FileMetaInfo.AddOrUpdate(DicomTag.MediaStorageSOPInstanceUID, sopUid);
        file.FileMetaInfo.AddOrUpdate(DicomTag.TransferSyntaxUID, DicomConstants.ExplicitVrLittleEndianUid);
        file.FileMetaInfo.AddOrUpdate(DicomTag.ImplementationClassUID, _uidGenerator.ImplementationClassUid);
        file.FileMetaInfo.AddOrUpdate(DicomTag.ImplementationVersionName, _uidGenerator.ImplementationVersionName);

        // Source Application Entity Title records which system produced the object, which
        // is the first thing an archive administrator looks at when tracing an instance.
        file.FileMetaInfo.AddOrUpdate(
            DicomTag.SourceApplicationEntityTitle,
            DicomValueRules.Sanitise(_configuration.CallingAeTitle, DicomValueRules.MaxAeTitleLength));

        return file;
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>Product version reported in Software Versions (0018,1020).</summary>
    private static string ProductVersion =>
        typeof(EncapsulatedPdfBuilder).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";

    /// <summary>
    /// Formats the local offset from UTC as the signed four digit value required by the
    /// Timezone Offset From UTC attribute, for example "+0100".
    /// </summary>
    private static string FormatUtcOffset(DateTime localTime)
    {
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(localTime);
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan absolute = offset.Duration();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{absolute.Hours:00}{absolute.Minutes:00}");
    }

    /// <summary>Returns the first supplied value that is not null, empty or whitespace.</summary>
    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? string.Empty;

    /// <summary>Adds a value only when it carries content, leaving Type 3 attributes absent otherwise.</summary>
    private static void AddIfNotEmpty(DicomDataset dataset, DicomTag tag, string? value, int maxLength)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            dataset.AddOrUpdate(tag, DicomValueRules.Sanitise(value, maxLength));
        }
    }

    /// <summary>
    /// Builds a file name that a support engineer can recognise at a glance while
    /// remaining safe on a Windows file system and unique within the output folder.
    /// </summary>
    private static string BuildFileName(PatientDemographics demographics, string sopUid)
    {
        string accession = SanitiseForFileName(demographics.AccessionNumber);
        string patientId = SanitiseForFileName(demographics.PatientId);

        // The final segment of the SOP Instance UID is enough to make the name unique
        // while keeping the path comfortably within the Windows length limit.
        string uidSuffix = sopUid.Split('.').LastOrDefault() ?? sopUid;
        if (uidSuffix.Length > 16)
        {
            uidSuffix = uidSuffix[^16..];
        }

        return $"{patientId}_{accession}_{uidSuffix}.dcm";
    }

    /// <summary>Replaces every character that is not safe in a file name with an underscore.</summary>
    private static string SanitiseForFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (char c in value.Trim())
        {
            builder.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        }

        string result = builder.ToString();
        return result.Length > 32 ? result[..32] : result;
    }
}
