using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Imaging;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Core.Validation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Covers conversion of ordinary images into Secondary Capture objects.
/// </summary>
/// <remarks>
/// The fixtures are generated rather than committed, so the tests state the pixel values
/// they expect instead of trusting a binary nobody can read in a review. Every assertion
/// about pixel data can therefore be checked against the formula that produced it.
/// </remarks>
public sealed class ImageConversionTests : IDisposable
{
    private readonly string _folder;
    private readonly RecordingLogger _logger = new();
    private readonly ImageInspector _inspector;

    public ImageConversionTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "PdfToDicomImageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _inspector = new ImageInspector(_logger);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A locked file in a temporary folder is not worth failing a test run over.
        }
    }

    // ----------------------------------------------------------------- fixtures

    private string WriteColour(string name, int width, int height, int frames = 1)
    {
        string path = Path.Combine(_folder, name);

        using var image = new Image<Rgb24>(width, height);
        Fill(image, (x, y) => new Rgb24((byte)(x * 3), (byte)(y * 7), 200));

        for (int frame = 1; frame < frames; frame++)
        {
            using var page = new Image<Rgb24>(width, height);
            Fill(page, (x, y) => new Rgb24((byte)(x + frame), (byte)y, 10));
            image.Frames.AddFrame(page.Frames.RootFrame);
        }

        image.Save(path);
        return path;
    }

    private string WriteGreyscale(string name, int width, int height)
    {
        string path = Path.Combine(_folder, name);

        using var image = new Image<L8>(width, height);
        Fill(image, (x, y) => new L8((byte)((x + y) * 5)));

        image.Save(path);
        return path;
    }

    private static void Fill<TPixel>(Image<TPixel> image, Func<int, int, TPixel> pixel)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                image[x, y] = pixel(x, y);
            }
        }
    }

    private SecondaryCaptureImageBuilder CreateBuilder(AppConfiguration? configuration = null)
    {
        configuration ??= TestSupport.CreateConfiguration();

        return new SecondaryCaptureImageBuilder(
            configuration,
            UidGenerator.FromConfiguration(configuration),
            _inspector,
            _logger,
            () => new DateTime(2026, 3, 4, 15, 16, 17));
    }

    // ----------------------------------------------------------------- inspection

    [Theory]
    [InlineData("probe.png")]
    [InlineData("probe.jpg")]
    [InlineData("probe.tiff")]
    [InlineData("probe.bmp")]
    [InlineData("probe.gif")]
    public void EveryCommonFormatIsRecognisedAndInspected(string name)
    {
        string path = WriteColour(name, 24, 16);

        Assert.Equal(SourceDocumentKind.Image, _inspector.DetectKind(path));
        Assert.True(_inspector.Validate(path).IsValid);

        ImageDocumentInfo info = _inspector.Inspect(path);

        Assert.Equal(24, info.Width);
        Assert.Equal(16, info.Height);
        Assert.Equal(1, info.PageCount);
    }

    [Fact]
    public void TheFileKindComesFromItsContentRatherThanItsExtension()
    {
        // A scanner writing a TIFF and calling it .jpg is commonplace, and so is an
        // operator renaming a file. Neither should change what the software does.
        string mislabelled = Path.Combine(_folder, "actually-a-png.jpg");
        File.Copy(WriteColour("truth.png", 8, 8), mislabelled);

        Assert.Equal(SourceDocumentKind.Image, _inspector.DetectKind(mislabelled));
        Assert.Equal("PNG", _inspector.Inspect(mislabelled).FormatName);

        // The same rule the other way round: a PDF is a PDF whatever it is called.
        string pdfCalledImage = Path.Combine(_folder, "report.png");
        File.Copy(TestSupport.SamplePdfPath, pdfCalledImage);

        Assert.Equal(SourceDocumentKind.Pdf, _inspector.DetectKind(pdfCalledImage));
    }

    [Fact]
    public void SomethingThatIsNeitherADocumentNorAnImageIsRejected()
    {
        string path = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(path, "This is not a document the software can convert.");

        Assert.Equal(SourceDocumentKind.Unsupported, _inspector.DetectKind(path));
        Assert.False(_inspector.Validate(path).IsValid);
    }

    [Fact]
    public void AGreyscaleSourceIsRecognisedAsGreyscale()
    {
        // The decoder promotes almost everything to a 32 bit colour buffer, so asking it
        // what format the file used gives the wrong answer. The pixels decide.
        ImageDocumentInfo info = _inspector.Inspect(WriteGreyscale("scan.png", 20, 20));

        Assert.False(info.IsColour);
        Assert.Equal(400, info.UncompressedPixelBytes);
    }

    [Fact]
    public void AColourFileHoldingOnlyGreyPixelsIsStoredAsGreyscale()
    {
        // The commonest case in this workflow: a flatbed scanner emitting an RGB file of a
        // black and white page. Storing it as MONOCHROME2 is two thirds smaller and loses
        // nothing, because there is no colour present to lose.
        string path = Path.Combine(_folder, "rgb-but-grey.png");

        using (var image = new Image<Rgb24>(16, 16))
        {
            Fill(image, (x, y) => new Rgb24((byte)(x * 8), (byte)(x * 8), (byte)(x * 8)));
            image.Save(path);
        }

        Assert.False(_inspector.Inspect(path).IsColour);

        DicomDataset dataset = CreateBuilder().Build(path, TestSupport.CreateDemographics())[0].File.Dataset;

        Assert.Equal("MONOCHROME2", dataset.GetSingleValue<string>(DicomTag.PhotometricInterpretation));
        Assert.Equal(1, dataset.GetSingleValue<ushort>(DicomTag.SamplesPerPixel));
    }

    // ----------------------------------------------------------------- building

    [Fact]
    public void AColourImageBecomesAConformantSecondaryCaptureObject()
    {
        string path = WriteColour("photo.png", 12, 9);

        IReadOnlyList<BuiltDicomObject> built = CreateBuilder().Build(path, TestSupport.CreateDemographics());

        Assert.Single(built);
        DicomDataset dataset = built[0].File.Dataset;

        Assert.Equal(
            DicomConstants.SecondaryCaptureImageStorageSopClassUid,
            dataset.GetSingleValue<string>(DicomTag.SOPClassUID));

        Assert.Equal(3, dataset.GetSingleValue<ushort>(DicomTag.SamplesPerPixel));
        Assert.Equal("RGB", dataset.GetSingleValue<string>(DicomTag.PhotometricInterpretation));
        Assert.Equal(0, dataset.GetSingleValue<ushort>(DicomTag.PlanarConfiguration));
        Assert.Equal(9, dataset.GetSingleValue<ushort>(DicomTag.Rows));
        Assert.Equal(12, dataset.GetSingleValue<ushort>(DicomTag.Columns));
        Assert.Equal(8, dataset.GetSingleValue<ushort>(DicomTag.BitsAllocated));
        Assert.Equal(7, dataset.GetSingleValue<ushort>(DicomTag.HighBit));
        Assert.Equal(0, dataset.GetSingleValue<ushort>(DicomTag.PixelRepresentation));

        // Interleaved, red then green then blue, which is what Planar Configuration 0
        // promises. The first pixel of the fixture is (0, 0, 200).
        byte[] pixels = dataset.GetValues<byte>(DicomTag.PixelData);
        Assert.Equal(12 * 9 * 3, pixels.Length);
        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[1]);
        Assert.Equal(200, pixels[2]);

        // The second pixel is one column across, so red advances by three.
        Assert.Equal(3, pixels[3]);
    }

    [Fact]
    public void AGreyscaleImageWithAnOddPixelCountIsPaddedToAnEvenLength()
    {
        // 33 by 17 is 561 bytes. Every DICOM value must occupy an even number of bytes, so
        // one padding byte is required, and it must sit outside the declared extent.
        string path = WriteGreyscale("odd.png", 33, 17);

        DicomDataset dataset = CreateBuilder().Build(path, TestSupport.CreateDemographics())[0].File.Dataset;

        byte[] pixels = dataset.GetValues<byte>(DicomTag.PixelData);

        Assert.Equal(562, pixels.Length);
        Assert.Equal(0, pixels.Length % 2);

        // The real pixels are untouched by the padding.
        Assert.Equal(0, pixels[0]);
        Assert.Equal(5, pixels[1]);
        Assert.Equal((byte)((32 + 16) * 5), pixels[560]);
    }

    [Fact]
    public void EveryPageOfAMultiPageSourceSharesOneSeriesAndIsNumbered()
    {
        string path = WriteColour("report.tiff", 10, 6, frames: 3);

        IReadOnlyList<BuiltDicomObject> built = CreateBuilder().Build(path, TestSupport.CreateDemographics());

        Assert.Equal(3, built.Count);
        Assert.Single(built.Select(b => b.StudyInstanceUid).Distinct());
        Assert.Single(built.Select(b => b.SeriesInstanceUid).Distinct());
        Assert.Equal(3, built.Select(b => b.SopInstanceUid).Distinct().Count());

        for (int i = 0; i < built.Count; i++)
        {
            Assert.Equal(
                (i + 1).ToString(),
                built[i].File.Dataset.GetSingleValue<string>(DicomTag.InstanceNumber));
        }

        // The file names must differ or the pages would overwrite one another on disk.
        Assert.Equal(3, built.Select(b => b.SuggestedFileName).Distinct().Count());
    }

    [Fact]
    public void ALossilyCompressedSourceSaysSo()
    {
        // A JPEG has already lost information that no later step recovers. PS3.3 C.7.6.1
        // requires the object to record that, and a clinician judging fine detail is
        // entitled to know it.
        DicomDataset fromJpeg = CreateBuilder()
            .Build(WriteColour("photo.jpg", 16, 16), TestSupport.CreateDemographics())[0].File.Dataset;

        Assert.Equal("01", fromJpeg.GetSingleValue<string>(DicomTag.LossyImageCompression));
        Assert.Equal("ISO_10918_1", fromJpeg.GetSingleValue<string>(DicomTag.LossyImageCompressionMethod));

        DicomDataset fromPng = CreateBuilder()
            .Build(WriteColour("photo.png", 16, 16), TestSupport.CreateDemographics())[0].File.Dataset;

        Assert.Equal("00", fromPng.GetSingleValue<string>(DicomTag.LossyImageCompression));
    }

    [Fact]
    public void AnImageUsesTheImageModalityRatherThanTheDocumentOne()
    {
        // A site that sets the document modality to DOC must not have its photographs
        // labelled as encapsulated documents.
        AppConfiguration configuration = TestSupport.CreateConfiguration();
        configuration.Modality = "DOC";
        configuration.ImageModality = "XC";

        DicomDataset dataset = CreateBuilder(configuration)
            .Build(WriteColour("photo.png", 8, 8), TestSupport.CreateDemographics())[0].File.Dataset;

        Assert.Equal("XC", dataset.GetSingleValue<string>(DicomTag.Modality));
    }

    // ----------------------------------------------------------------- validation

    [Fact]
    public void AGeneratedImageObjectPassesItsOwnValidator()
    {
        BuiltDicomObject built = CreateBuilder()
            .Build(WriteColour("photo.png", 20, 20), TestSupport.CreateDemographics())[0];

        ValidationResult result = new SopClassDicomValidator().Validate(built.File);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void PixelDataThatDisagreesWithTheDimensionsIsRejected()
    {
        // The failure this guards against does not announce itself: a viewer handed a
        // buffer of the wrong length renders a torn image rather than refusing it.
        BuiltDicomObject built = CreateBuilder()
            .Build(WriteColour("photo.png", 20, 20), TestSupport.CreateDemographics())[0];

        built.File.Dataset.AddOrUpdate(DicomTag.Rows, (ushort)21);

        ValidationResult result = new SecondaryCaptureImageValidator().Validate(built.File);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("pixel data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AColourObjectMissingPlanarConfigurationIsRejected()
    {
        BuiltDicomObject built = CreateBuilder()
            .Build(WriteColour("photo.png", 8, 8), TestSupport.CreateDemographics())[0];

        built.File.Dataset.Remove(DicomTag.PlanarConfiguration);

        ValidationResult result = new SecondaryCaptureImageValidator().Validate(built.File);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "Planar Configuration");
    }

    [Fact]
    public void TheDispatchingValidatorPicksTheRulesForTheObjectItIsGiven()
    {
        var validator = new SopClassDicomValidator();

        BuiltDicomObject image = CreateBuilder()
            .Build(WriteColour("photo.png", 8, 8), TestSupport.CreateDemographics())[0];

        Assert.True(validator.Validate(image.File).IsValid);
        Assert.True(validator.Validate(TestSupport.BuildSampleObject().File).IsValid);

        // Something this software does not produce must not slip through unchecked.
        image.File.Dataset.AddOrUpdate(DicomTag.SOPClassUID, "1.2.840.10008.5.1.4.1.1.2");

        Assert.False(validator.Validate(image.File).IsValid);
    }

    // ----------------------------------------------------------------- grouping

    [Fact]
    public void SeparateStudyModeGivesEveryFileItsOwnStudy()
    {
        var grouping = new StudyGroupingService();

        Assert.Null(grouping.ResolveStudyInstanceUid(StudyGroupingMode.SeparateStudy, "PID1"));

        grouping.Remember(StudyGroupingMode.SeparateStudy, "PID1", "1.2.3.4");

        // Nothing was remembered, because grouping was not asked for.
        Assert.Null(grouping.CurrentStudyInstanceUid);
    }

    [Fact]
    public void GroupingReusesTheStudyForTheSamePatientAndOnlyForThem()
    {
        var grouping = new StudyGroupingService();
        const StudyGroupingMode mode = StudyGroupingMode.GroupByPatient;

        // The first file for a patient has nothing to join.
        Assert.Null(grouping.ResolveStudyInstanceUid(mode, "PID1"));

        grouping.Remember(mode, "PID1", "1.2.3.4");
        Assert.Equal("1.2.3.4", grouping.ResolveStudyInstanceUid(mode, "PID1"));

        // A different patient starts a new study rather than joining someone else's.
        Assert.Null(grouping.ResolveStudyInstanceUid(mode, "PID2"));

        grouping.Remember(mode, "PID2", "5.6.7.8");
        Assert.Equal("5.6.7.8", grouping.ResolveStudyInstanceUid(mode, "PID2"));

        // And going back to the first patient does not resurrect their old study, because
        // only the most recent association is held.
        Assert.Null(grouping.ResolveStudyInstanceUid(mode, "PID1"));
    }

    [Fact]
    public void PatientIdentifiersAreComparedExactly()
    {
        // Patient identifiers are opaque strings assigned by another system. Treating
        // "ab1" and "AB1" as the same patient is a guess, and a wrong guess here files a
        // document against the wrong person.
        var grouping = new StudyGroupingService();
        grouping.Remember(StudyGroupingMode.GroupByPatient, "ab1", "1.2.3.4");

        Assert.Null(grouping.ResolveStudyInstanceUid(StudyGroupingMode.GroupByPatient, "AB1"));
        Assert.Equal("1.2.3.4", grouping.ResolveStudyInstanceUid(StudyGroupingMode.GroupByPatient, "ab1"));
    }

    [Fact]
    public void ResettingStartsANewStudy()
    {
        var grouping = new StudyGroupingService();
        grouping.Remember(StudyGroupingMode.GroupByPatient, "PID1", "1.2.3.4");
        grouping.Reset();

        Assert.Null(grouping.CurrentStudyInstanceUid);
        Assert.Null(grouping.ResolveStudyInstanceUid(StudyGroupingMode.GroupByPatient, "PID1"));
    }

    [Fact]
    public void AGroupedStudyIsUsedButAnOperatorSuppliedOneWins()
    {
        string path = WriteColour("photo.png", 8, 8);
        PatientDemographics demographics = TestSupport.CreateDemographics();

        // Grouping supplies the study when the operator has not.
        IReadOnlyList<BuiltDicomObject> grouped =
            CreateBuilder().Build(path, demographics, "1.2.826.0.1.3680043.9.6008.1.111");

        Assert.Equal("1.2.826.0.1.3680043.9.6008.1.111", grouped[0].StudyInstanceUid);

        // A Study Instance UID typed on the form outranks it, because the operator is
        // filing the image into a study they can see and grouping is only a convenience.
        demographics.ExistingStudyInstanceUid = "1.2.826.0.1.3680043.9.6008.1.222";

        IReadOnlyList<BuiltDicomObject> overridden =
            CreateBuilder().Build(path, demographics, "1.2.826.0.1.3680043.9.6008.1.111");

        Assert.Equal("1.2.826.0.1.3680043.9.6008.1.222", overridden[0].StudyInstanceUid);
    }
}
