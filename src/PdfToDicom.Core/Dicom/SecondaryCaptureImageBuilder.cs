using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Builds Secondary Capture Image Storage SOP Instances from an ordinary image file.
/// </summary>
/// <remarks>
/// <para>
/// The object produced satisfies the Secondary Capture Image Information Object
/// Definition of DICOM PS3.3 Annex A.8.1: the Patient, General Study, General Series,
/// General Equipment, SC Equipment, General Image, Image Pixel and SOP Common modules.
/// </para>
/// <para>
/// Pixel data is stored uncompressed in Explicit VR Little Endian, the same syntax the
/// Encapsulated PDF objects use. Every conformant archive accepts it and it needs no
/// native codec library, which is what keeps the application a single self contained
/// executable. The cost is size: a twelve megapixel photograph becomes an object of
/// roughly thirty six megabytes, and the operator is told so before converting.
/// </para>
/// <para>
/// A multi page source, typically a scanned TIFF, produces one instance per page. All
/// pages share one Series Instance UID and are numbered sequentially, so an archive
/// presents them as a single document that a viewer can page through.
/// </para>
/// </remarks>
public sealed class SecondaryCaptureImageBuilder : IImageDicomObjectBuilder
{
    private readonly AppConfiguration _configuration;
    private readonly IUidGenerator _uidGenerator;
    private readonly IImageInspector _imageInspector;
    private readonly IAppLogger _logger;
    private readonly Func<DateTime> _nowProvider;
    private readonly DicomModuleWriter _modules;

    /// <summary>Creates the builder.</summary>
    /// <param name="configuration">Site configuration supplying institution and encoding settings.</param>
    /// <param name="uidGenerator">Source of new identifiers.</param>
    /// <param name="imageInspector">Used to read the image when it was not already inspected.</param>
    /// <param name="logger">Diagnostic log.</param>
    /// <param name="nowProvider">
    /// Supplies the current local time. Injected so that the tests can assert on the date
    /// and time attributes deterministically.
    /// </param>
    public SecondaryCaptureImageBuilder(
        AppConfiguration configuration,
        IUidGenerator uidGenerator,
        IImageInspector imageInspector,
        IAppLogger logger,
        Func<DateTime>? nowProvider = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _uidGenerator = uidGenerator ?? throw new ArgumentNullException(nameof(uidGenerator));
        _imageInspector = imageInspector ?? throw new ArgumentNullException(nameof(imageInspector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        _modules = new DicomModuleWriter(_configuration, _uidGenerator);

        DicomRuntime.EnsureInitialized();
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltDicomObject> Build(
        string imagePath,
        PatientDemographics demographics,
        string? studyInstanceUid = null,
        ImageDocumentInfo? imageInfo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(demographics);

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("The image to convert was not found.", imagePath);
        }

        imageInfo ??= _imageInspector.Inspect(imagePath);

        DateTime now = _nowProvider();

        // Precedence, from strongest to weakest: a Study Instance UID typed by the
        // operator, then one supplied by the caller because grouping is switched on, then
        // a new study.
        string studyUid = FirstUid(
            demographics.ExistingStudyInstanceUid,
            studyInstanceUid)
            ?? _uidGenerator.NewStudyInstanceUid();

        // One series for the whole file, however many pages it has. Separate files get
        // separate series, which is what keeps them distinguishable inside a study.
        string seriesUid = _uidGenerator.NewSeriesInstanceUid();

        using Image source = Image.Load(imagePath);
        IImageFormat format = Image.DetectFormat(imagePath);

        int pageCount = source.Frames.Count;
        bool isColour = imageInfo.IsColour;

        _logger.Information(
            $"Generating {pageCount} Secondary Capture object(s) from '{imageInfo.FileName}' " +
            $"({format.Name}, {imageInfo.PixelSummary}). Study {studyUid}, series {seriesUid}.");

        var built = new List<BuiltDicomObject>(pageCount);

        for (int page = 0; page < pageCount; page++)
        {
            string sopUid = _uidGenerator.NewSopInstanceUid();

            using Image frame = source.Frames.CloneFrame(page);
            byte[] pixels = ExtractPixels(frame, isColour);

            var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);

            // Specific Character Set must be written before any text value so that the
            // toolkit encodes subsequent strings with the intended repertoire.
            _modules.AddSpecificCharacterSet(dataset);

            DicomModuleWriter.AddPatientModule(dataset, demographics);
            _modules.AddGeneralStudyModule(dataset, demographics, studyUid, now);
            _modules.AddSeriesModule(dataset, seriesUid, _configuration.ImageModality);
            _modules.AddGeneralEquipmentModule(dataset);
            _modules.AddScEquipmentModule(dataset);
            AddGeneralImageModule(dataset, demographics, page + 1, now);
            AddImagePixelModule(dataset, frame.Width, frame.Height, isColour, pixels);
            AddLossyCompressionHistory(dataset, format);
            _modules.AddSopCommonModule(
                dataset, DicomConstants.SecondaryCaptureImageStorageSopClassUid, sopUid, now);

            DicomFile file = _modules.CreateFileWithMetaInformation(
                dataset, DicomConstants.SecondaryCaptureImageStorageSopClassUid, sopUid);

            // Only a multi page source gets a page suffix, so a single image keeps a file
            // name of exactly the shape a PDF conversion produces.
            string pageSuffix = pageCount > 1
                ? $"_p{(page + 1).ToString("000", CultureInfo.InvariantCulture)}"
                : string.Empty;

            built.Add(new BuiltDicomObject(
                file,
                studyUid,
                seriesUid,
                sopUid,
                DicomModuleWriter.BuildFileName(demographics, sopUid, pageSuffix)));
        }

        return built;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltDicomObject> BuildFromRenderedPages(
        IReadOnlyList<RenderedPdfPage> pages,
        PatientDemographics demographics,
        string studyInstanceUid,
        string sourceFileName)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(demographics);
        ArgumentException.ThrowIfNullOrWhiteSpace(studyInstanceUid);

        if (pages.Count == 0)
        {
            return Array.Empty<BuiltDicomObject>();
        }

        DateTime now = _nowProvider();

        // A new series inside the study the document was filed under. Its own series is
        // what keeps the images distinguishable from the document while a viewer still
        // presents them together.
        string seriesUid = _uidGenerator.NewSeriesInstanceUid();

        // One above the document's series, so a viewer that orders by series number shows
        // the readable pages rather than opening on an object it may not render.
        int seriesNumber = _configuration.SeriesNumber + 1;

        _logger.Information(
            $"Generating {pages.Count} rendered page image(s) from '{sourceFileName}'. " +
            $"Study {studyInstanceUid}, series {seriesUid}.");

        var built = new List<BuiltDicomObject>(pages.Count);

        foreach (RenderedPdfPage page in pages)
        {
            string sopUid = _uidGenerator.NewSopInstanceUid();

            var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);

            _modules.AddSpecificCharacterSet(dataset);

            DicomModuleWriter.AddPatientModule(dataset, demographics);
            _modules.AddGeneralStudyModule(dataset, demographics, studyInstanceUid, now);
            _modules.AddSeriesModule(dataset, seriesUid, _configuration.ImageModality, seriesNumber);
            _modules.AddGeneralEquipmentModule(dataset);
            _modules.AddScEquipmentModule(dataset);
            AddGeneralImageModule(dataset, demographics, page.PageNumber, now);
            AddImagePixelModule(dataset, page.Width, page.Height, page.IsColour, page.Pixels);
            AddRenderedPageDerivation(dataset, sourceFileName, page.PageNumber, pages.Count);
            _modules.AddSopCommonModule(
                dataset, DicomConstants.SecondaryCaptureImageStorageSopClassUid, sopUid, now);

            DicomFile file = _modules.CreateFileWithMetaInformation(
                dataset, DicomConstants.SecondaryCaptureImageStorageSopClassUid, sopUid);

            string pageSuffix = $"_page{page.PageNumber.ToString("000", CultureInfo.InvariantCulture)}";

            built.Add(new BuiltDicomObject(
                file,
                studyInstanceUid,
                seriesUid,
                sopUid,
                DicomModuleWriter.BuildFileName(demographics, sopUid, pageSuffix)));
        }

        return built;
    }

    /// <summary>
    /// Records that these pixels were rendered from the document in the same study, rather
    /// than acquired.
    /// </summary>
    /// <remarks>
    /// Worth stating in the object itself. Anyone auditing the archive later, or wondering
    /// why one study holds a report twice, can read the answer from the instance instead of
    /// inferring it.
    /// </remarks>
    private static void AddRenderedPageDerivation(
        DicomDataset dataset, string sourceFileName, int pageNumber, int pageCount)
    {
        dataset.AddOrUpdate(
            DicomTag.DerivationDescription,
            DicomValueRules.Sanitise(
                $"Page {pageNumber} of {pageCount} rendered from the encapsulated PDF document " +
                $"'{sourceFileName}' in this study.",
                DicomValueRules.MaxShortTextLength));
    }

    // ------------------------------------------------------------------ Modules

    /// <summary>Writes the General Image module, PS3.3 C.7.6.1.</summary>
    private void AddGeneralImageModule(
        DicomDataset dataset,
        PatientDemographics demographics,
        int instanceNumber,
        DateTime now)
    {
        // Instance Number is Type 2. Numbering the pages is what lets a viewer present a
        // scanned document in the order it was scanned rather than an arbitrary one.
        dataset.AddOrUpdate(
            DicomTag.InstanceNumber,
            instanceNumber.ToString(CultureInfo.InvariantCulture));

        // Patient Orientation is Type 2C and has no meaning for a scanned document or a
        // photograph, so it is present and empty rather than invented.
        dataset.AddOrUpdate(DicomTag.PatientOrientation, string.Empty);

        dataset.AddOrUpdate(DicomTag.ContentDate, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        dataset.AddOrUpdate(DicomTag.ContentTime, now.ToString("HHmmss", CultureInfo.InvariantCulture));

        // DERIVED because the pixels came from something that was not a DICOM acquisition,
        // SECONDARY because this is not the original acquisition of the patient.
        dataset.AddOrUpdate(DicomTag.ImageType, "DERIVED", "SECONDARY");

        // Burned In Annotation is required by the SC Image module and is genuinely unknown
        // here: a scanned report almost certainly carries the patient's name in its pixels.
        // Saying YES is the safe answer, because it warns a receiving system that the
        // pixels cannot be treated as anonymous.
        dataset.AddOrUpdate(DicomTag.BurnedInAnnotation, "YES");

        string seriesDescription = DicomModuleWriter.FirstNonEmpty(
            demographics.SeriesDescription,
            demographics.DocumentTitle,
            _configuration.DefaultImageSeriesDescription);

        DicomModuleWriter.AddIfNotEmpty(
            dataset, DicomTag.SeriesDescription, seriesDescription, DicomValueRules.MaxLongStringLength);
    }

    /// <summary>Writes the Image Pixel module, PS3.3 C.7.6.3.</summary>
    private static void AddImagePixelModule(
        DicomDataset dataset,
        int width,
        int height,
        bool isColour,
        byte[] pixels)
    {
        dataset.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)(isColour ? 3 : 1));
        dataset.AddOrUpdate(DicomTag.PhotometricInterpretation, isColour ? "RGB" : "MONOCHROME2");

        if (isColour)
        {
            // 0 means the three samples of a pixel are stored together, R then G then B,
            // which is the layout the decoder produced. Required whenever Samples per
            // Pixel is greater than one.
            dataset.AddOrUpdate(DicomTag.PlanarConfiguration, (ushort)0);
        }

        dataset.AddOrUpdate(DicomTag.Rows, (ushort)height);
        dataset.AddOrUpdate(DicomTag.Columns, (ushort)width);
        dataset.AddOrUpdate(DicomTag.BitsAllocated, (ushort)8);
        dataset.AddOrUpdate(DicomTag.BitsStored, (ushort)8);
        dataset.AddOrUpdate(DicomTag.HighBit, (ushort)7);

        // 0 is unsigned. Eight bit image data is never signed.
        dataset.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);

        // Every DICOM value must occupy an even number of bytes. An odd length buffer,
        // which happens whenever a greyscale image has an odd number of pixels, is padded
        // with a single null byte. The padding is outside the declared Rows by Columns
        // extent, so no viewer ever sees it.
        byte[] value = pixels;
        if ((value.Length & 1) == 1)
        {
            value = new byte[pixels.Length + 1];
            pixels.CopyTo(value, 0);
        }

        dataset.AddOrUpdate(new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer(value)));
    }

    /// <summary>
    /// Records that the pixels passed through lossy compression before reaching this
    /// software, when the source format says they did.
    /// </summary>
    /// <remarks>
    /// PS3.3 C.7.6.1 requires Lossy Image Compression to say so whenever the image has
    /// ever been lossily compressed, not merely when this object is compressed. A JPEG
    /// scanned report has lost information that no later step recovers, and a clinician
    /// judging fine detail is entitled to know that from the object itself.
    /// </remarks>
    private static void AddLossyCompressionHistory(DicomDataset dataset, IImageFormat format)
    {
        string name = format.Name ?? string.Empty;

        bool lossy = name.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WEBP", StringComparison.OrdinalIgnoreCase);

        // Type 3, and "00" states positively that the pixels were never lossily
        // compressed, which is worth saying for a PNG or TIFF source.
        dataset.AddOrUpdate(DicomTag.LossyImageCompression, lossy ? "01" : "00");

        if (lossy && name.Equals("JPEG", StringComparison.OrdinalIgnoreCase))
        {
            // The defined term for baseline JPEG, PS3.3 C.7.6.1.1.5.
            dataset.AddOrUpdate(DicomTag.LossyImageCompressionMethod, "ISO_10918_1");
        }
    }

    // ------------------------------------------------------------------ Pixels

    /// <summary>
    /// Copies one frame out as the contiguous byte buffer DICOM expects: interleaved RGB
    /// for colour, one byte per pixel for greyscale.
    /// </summary>
    private static byte[] ExtractPixels(Image frame, bool isColour)
    {
        if (isColour)
        {
            using Image<Rgb24> rgb = frame.CloneAs<Rgb24>();
            var buffer = new byte[(long)rgb.Width * rgb.Height * 3];
            rgb.CopyPixelDataTo(buffer);
            return buffer;
        }

        using Image<L8> grey = frame.CloneAs<L8>();
        var monochrome = new byte[(long)grey.Width * grey.Height];
        grey.CopyPixelDataTo(monochrome);
        return monochrome;
    }

    /// <summary>Returns the first supplied identifier that has content, or null.</summary>
    private static string? FirstUid(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
