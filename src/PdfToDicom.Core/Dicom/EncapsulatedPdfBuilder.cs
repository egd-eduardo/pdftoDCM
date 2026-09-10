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
    private readonly DicomModuleWriter _modules;

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
        _modules = new DicomModuleWriter(_configuration, _uidGenerator);

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
        _modules.AddSpecificCharacterSet(dataset);

        DicomModuleWriter.AddPatientModule(dataset, demographics);
        _modules.AddGeneralStudyModule(dataset, demographics, studyUid, now);
        _modules.AddSeriesModule(dataset, seriesUid, _configuration.Modality);
        _modules.AddGeneralEquipmentModule(dataset);
        _modules.AddScEquipmentModule(dataset);
        AddEncapsulatedDocumentModule(dataset, demographics, pdfInfo, pdfBytes, now);
        _modules.AddSopCommonModule(dataset, DicomConstants.EncapsulatedPdfStorageSopClassUid, sopUid, now);

        DicomFile file = _modules.CreateFileWithMetaInformation(
            dataset, DicomConstants.EncapsulatedPdfStorageSopClassUid, sopUid);

        string suggestedFileName = DicomModuleWriter.BuildFileName(demographics, sopUid);

        return new BuiltDicomObject(file, studyUid, seriesUid, sopUid, suggestedFileName);
    }

    // ------------------------------------------------------------------ Modules







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

        string documentTitle = DicomModuleWriter.FirstNonEmpty(
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

        string seriesDescription = DicomModuleWriter.FirstNonEmpty(
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


    // ------------------------------------------------------------------ File meta


    // ------------------------------------------------------------------ Helpers







}
