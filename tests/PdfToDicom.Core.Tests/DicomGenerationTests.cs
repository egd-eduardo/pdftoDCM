using System.Text;
using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Covers UID construction. A repeated identifier would cause an archive to overwrite or
/// reject an object, so uniqueness is asserted explicitly rather than assumed.
/// </summary>
public sealed class UidGeneratorTests
{
    [Fact]
    public void UuidDerivedUidsUseTheStandardPrefix()
    {
        var generator = new UidGenerator(UidGenerationStrategy.UuidDerived);

        Assert.StartsWith("2.25.", generator.NewStudyInstanceUid(), StringComparison.Ordinal);
        Assert.StartsWith("2.25.", generator.NewSeriesInstanceUid(), StringComparison.Ordinal);
        Assert.StartsWith("2.25.", generator.NewSopInstanceUid(), StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedUidsAreValidAndWithinTheLengthLimit()
    {
        var generator = new UidGenerator(UidGenerationStrategy.UuidDerived);

        for (int i = 0; i < 200; i++)
        {
            string uid = generator.NewSopInstanceUid();

            Assert.True(DicomValueRules.IsValidUid(uid, out string error), error);
            Assert.True(uid.Length <= DicomValueRules.MaxUidLength);
        }
    }

    [Fact]
    public void GeneratedUidsAreNeverRepeated()
    {
        var generator = new UidGenerator(UidGenerationStrategy.UuidDerived);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 20_000; i++)
        {
            Assert.True(seen.Add(generator.NewSopInstanceUid()), "The generator produced a duplicate UID.");
        }
    }

    [Fact]
    public void RegisteredRootUidsAreBuiltBeneathTheConfiguredRoot()
    {
        var generator = new UidGenerator(UidGenerationStrategy.RegisteredRoot, "1.2.826.0.1.3680043.10.999");

        string uid = generator.NewStudyInstanceUid();

        Assert.StartsWith("1.2.826.0.1.3680043.10.999.", uid, StringComparison.Ordinal);
        Assert.True(DicomValueRules.IsValidUid(uid, out _));
        Assert.True(uid.Length <= DicomValueRules.MaxUidLength);
    }

    [Fact]
    public void RegisteredRootUidsRemainUniqueWithinTheSameMillisecond()
    {
        var generator = new UidGenerator(UidGenerationStrategy.RegisteredRoot, "1.2.826.0.1.3680043.10.999");
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Generated in a tight loop so that many share a timestamp component. Uniqueness
        // then rests entirely on the random component, which is what is being verified.
        for (int i = 0; i < 5_000; i++)
        {
            Assert.True(seen.Add(generator.NewSopInstanceUid()), "The generator produced a duplicate UID.");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uid")]
    [InlineData("1.2.03")]
    public void AnUnusableRegisteredRootIsRejectedAtConstruction(string root) =>
        Assert.Throws<ArgumentException>(() => new UidGenerator(UidGenerationStrategy.RegisteredRoot, root));

    [Fact]
    public void ARootTooLongToLeaveRoomForASuffixIsRejected()
    {
        string longRoot = "1." + string.Join('.', Enumerable.Repeat("12345", 8));

        Assert.Throws<ArgumentException>(() => new UidGenerator(UidGenerationStrategy.RegisteredRoot, longRoot));
    }
}

/// <summary>Covers Person Name construction.</summary>
public sealed class DicomPersonNameTests
{
    [Fact]
    public void ComponentsAreJoinedWithCarets() =>
        Assert.Equal("Silva^Maria^Joana", PersonNameFormatter.Compose("Silva", "Maria", "Joana", "", ""));

    [Fact]
    public void TrailingEmptyComponentsAreOmitted() =>
        Assert.Equal("Silva^Maria", PersonNameFormatter.Compose("Silva", "Maria", "", "", ""));

    [Fact]
    public void SeparatorCharactersAreStrippedFromComponents() =>
        Assert.Equal("SilvaSantos^Maria", PersonNameFormatter.Compose("Silva^Santos", "Maria", "", "", ""));

    [Fact]
    public void FreeTextIsReorderedIntoFamilyThenGiven() =>
        Assert.Equal("Ribeiro^Ana", PersonNameFormatter.FromFreeText("Ana Ribeiro"));

    [Fact]
    public void FreeTextAlreadyInDicomFormIsPassedThrough() =>
        Assert.Equal("Ribeiro^Ana", PersonNameFormatter.FromFreeText("Ribeiro^Ana"));

    [Fact]
    public void EmptyFreeTextProducesAnEmptyValue() =>
        Assert.Equal(string.Empty, PersonNameFormatter.FromFreeText("   "));

    [Fact]
    public void AnOverLongNameIsTruncatedToTheVrLimit()
    {
        string result = PersonNameFormatter.Compose(new string('A', 80), "B", "", "", "");

        Assert.Equal(DicomValueRules.MaxPersonNameGroupLength, result.Length);
    }
}

/// <summary>
/// Covers construction of the Encapsulated PDF object itself, attribute by attribute
/// against the Information Object Definition in DICOM PS3.3 Annex A.45.1.
/// </summary>
public sealed class EncapsulatedPdfBuilderTests
{
    [Fact]
    public void TheObjectDeclaresTheEncapsulatedPdfStorageSopClass()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();

        Assert.Equal(
            DicomConstants.EncapsulatedPdfStorageSopClassUid,
            built.File.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID));
    }

    [Fact]
    public void TheFileMetaGroupAgreesWithTheDataSet()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        DicomFileMetaInformation meta = built.File.FileMetaInfo;

        Assert.Equal(
            built.File.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID),
            meta.GetSingleValue<string>(DicomTag.MediaStorageSOPClassUID));

        Assert.Equal(
            built.File.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID),
            meta.GetSingleValue<string>(DicomTag.MediaStorageSOPInstanceUID));

        Assert.Equal(
            DicomConstants.ExplicitVrLittleEndianUid,
            meta.GetSingleValue<string>(DicomTag.TransferSyntaxUID));
    }

    [Fact]
    public void EveryMandatoryAttributeIsPresent()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        DicomDataset dataset = built.File.Dataset;

        DicomTag[] required =
        {
            DicomTag.SpecificCharacterSet,
            DicomTag.PatientName,
            DicomTag.PatientID,
            DicomTag.PatientBirthDate,
            DicomTag.PatientSex,
            DicomTag.StudyInstanceUID,
            DicomTag.SeriesInstanceUID,
            DicomTag.SOPInstanceUID,
            DicomTag.AccessionNumber,
            DicomTag.StudyDate,
            DicomTag.StudyTime,
            DicomTag.ContentDate,
            DicomTag.ContentTime,
            DicomTag.InstanceCreationDate,
            DicomTag.InstanceCreationTime,
            DicomTag.Modality,
            DicomTag.ConversionType,
            DicomTag.MIMETypeOfEncapsulatedDocument,
            DicomTag.Manufacturer,
            DicomTag.SeriesNumber,
            DicomTag.InstanceNumber,
            DicomTag.BurnedInAnnotation,
            DicomTag.DocumentTitle,
            DicomTag.EncapsulatedDocument,
            DicomTag.StudyID,
            DicomTag.ReferringPhysicianName,
            DicomTag.AcquisitionDateTime,
            DicomTag.ConceptNameCodeSequence
        };

        foreach (DicomTag tag in required)
        {
            Assert.True(dataset.Contains(tag), $"The mandatory attribute {tag} is missing.");
        }
    }

    [Fact]
    public void ThePdfIsEmbeddedByteForByte()
    {
        byte[] original = File.ReadAllBytes(TestSupport.SamplePdfPath);
        BuiltDicomObject built = TestSupport.BuildSampleObject();

        byte[] embedded = built.File.Dataset.GetValues<byte>(DicomTag.EncapsulatedDocument);

        // The payload is padded to an even length, as OB requires, but the document
        // itself must be unaltered or a clinician could not open it from the archive.
        Assert.True(embedded.Length >= original.Length);
        Assert.True(embedded.Length % 2 == 0);
        Assert.Equal(original, embedded.Take(original.Length).ToArray());
    }

    [Fact]
    public void AnOddLengthPayloadIsPaddedToAnEvenLength()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");

        try
        {
            byte[] original = File.ReadAllBytes(TestSupport.SamplePdfPath);

            // Force an odd length by appending a single trailing byte after the trailer.
            byte[] odd = original.Length % 2 == 0
                ? original.Concat(new byte[] { 0x0A }).ToArray()
                : original;

            File.WriteAllBytes(path, odd);
            Assert.True(odd.Length % 2 != 0);

            var configuration = TestSupport.CreateConfiguration();
            var logger = new RecordingLogger();
            var inspector = new Core.Pdf.PdfInspector(logger);
            var builder = new EncapsulatedPdfBuilder(
                configuration, UidGenerator.FromConfiguration(configuration), inspector, logger);

            BuiltDicomObject built = builder.Build(path, TestSupport.CreateDemographics());
            byte[] embedded = built.File.Dataset.GetValues<byte>(DicomTag.EncapsulatedDocument);

            Assert.Equal(odd.Length + 1, embedded.Length);
            Assert.Equal(0x00, embedded[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheThreeIdentifiersAreDistinct()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();

        Assert.NotEqual(built.StudyInstanceUid, built.SeriesInstanceUid);
        Assert.NotEqual(built.SeriesInstanceUid, built.SopInstanceUid);
        Assert.NotEqual(built.StudyInstanceUid, built.SopInstanceUid);
    }

    [Fact]
    public void TwoRunsOverTheSameInputProduceDifferentIdentifiers()
    {
        BuiltDicomObject first = TestSupport.BuildSampleObject();
        BuiltDicomObject second = TestSupport.BuildSampleObject();

        Assert.NotEqual(first.SopInstanceUid, second.SopInstanceUid);
        Assert.NotEqual(first.StudyInstanceUid, second.StudyInstanceUid);
    }

    [Fact]
    public void ASuppliedStudyInstanceUidIsUsedSoTheDocumentJoinsTheExistingStudy()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.ExistingStudyInstanceUid = "1.2.826.0.1.3680043.10.999.1.20260909";

        BuiltDicomObject built = TestSupport.BuildSampleObject(demographics: demographics);

        Assert.Equal(demographics.ExistingStudyInstanceUid, built.StudyInstanceUid);
        Assert.Equal(
            demographics.ExistingStudyInstanceUid,
            built.File.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID));
    }

    [Fact]
    public void AccentedNamesSurviveAUtf8RoundTrip()
    {
        AppConfiguration configuration = TestSupport.CreateConfiguration();
        configuration.CharacterSet = DicomCharacterSet.Utf8;

        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.FamilyName = "Gonçalves Aparício";
        demographics.GivenName = "João";

        BuiltDicomObject built = TestSupport.BuildSampleObject(configuration, demographics);

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dcm");

        try
        {
            built.File.Save(path);
            DicomFile reopened = DicomFile.Open(path);

            Assert.Equal("ISO_IR 192", reopened.Dataset.GetSingleValue<string>(DicomTag.SpecificCharacterSet));
            Assert.Equal(
                "Gonçalves Aparício^João",
                reopened.Dataset.GetSingleValue<string>(DicomTag.PatientName));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheConfiguredModalityIsWritten()
    {
        AppConfiguration configuration = TestSupport.CreateConfiguration();
        configuration.Modality = "DOC";

        BuiltDicomObject built = TestSupport.BuildSampleObject(configuration);

        Assert.Equal("DOC", built.File.Dataset.GetSingleValue<string>(DicomTag.Modality));
    }

    [Fact]
    public void TheDefaultModalityIsOtherAsSpecified()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();

        Assert.Equal("OT", built.File.Dataset.GetSingleValue<string>(DicomTag.Modality));
    }

    [Fact]
    public void ThePageCountIsRecordedInTheReservedPrivateBlock()
    {
        AppConfiguration configuration = TestSupport.CreateConfiguration();
        configuration.RecordPageCountInPrivateTag = true;

        BuiltDicomObject built = TestSupport.BuildSampleObject(configuration);

        var creator = DicomDictionary.Default.GetPrivateCreator(DicomConstants.PrivateCreator);
        DicomTag tag = built.File.Dataset.GetPrivateTag(
            new DicomTag(DicomConstants.PrivateGroup, 0x01, creator));

        Assert.True(built.File.Dataset.Contains(tag));
        Assert.Equal("2", built.File.Dataset.GetSingleValue<string>(tag));
    }

    [Fact]
    public void ThePrivatePageCountCanBeSuppressed()
    {
        AppConfiguration configuration = TestSupport.CreateConfiguration();
        configuration.RecordPageCountInPrivateTag = false;

        BuiltDicomObject built = TestSupport.BuildSampleObject(configuration);

        Assert.DoesNotContain(built.File.Dataset, item => item.Tag.IsPrivate);
    }

    [Fact]
    public void TheInstitutionNameFromTheConfigurationIsWritten()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();

        Assert.Equal("Test Hospital", built.File.Dataset.GetSingleValue<string>(DicomTag.InstitutionName));
    }

    [Fact]
    public void TheSuggestedFileNameIsSafeOnAWindowsFileSystem()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.PatientId = "ID/with:illegal*chars";
        demographics.AccessionNumber = "ACC 001";

        BuiltDicomObject built = TestSupport.BuildSampleObject(demographics: demographics);

        Assert.DoesNotContain(built.SuggestedFileName, c => Path.GetInvalidFileNameChars().Contains(c));
        Assert.EndsWith(".dcm", built.SuggestedFileName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheImplementationIdentityIsThisApplicationNotTheUnderlyingToolkit()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dcm");

        try
        {
            // The toolkit rebuilds the meta group on save, so this must be asserted on a
            // reopened file rather than on the in memory object.
            built.File.Save(path);
            DicomFile reopened = DicomFile.Open(path);

            Assert.Equal(
                UidGenerator.DefaultImplementationClassUid,
                reopened.FileMetaInfo.GetSingleValue<string>(DicomTag.ImplementationClassUID));

            Assert.Equal(
                UidGenerator.DefaultImplementationVersionName,
                reopened.FileMetaInfo.GetSingleValue<string>(DicomTag.ImplementationVersionName));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Covers the Information Object Definition validator.</summary>
public sealed class EncapsulatedPdfValidatorTests
{
    private readonly EncapsulatedPdfValidator _validator = new();

    [Fact]
    public void AFreshlyBuiltObjectPassesValidation()
    {
        ValidationResult result = _validator.Validate(TestSupport.BuildSampleObject().File);

        Assert.True(result.IsValid, result.ToDisplayString());
    }

    [Fact]
    public void AMissingTypeOneAttributeIsDetected()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        built.File.Dataset.Remove(DicomTag.Modality);

        ValidationResult result = _validator.Validate(built.File);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("Modality", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingTypeTwoAttributeIsDetected()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        built.File.Dataset.Remove(DicomTag.ReferringPhysicianName);

        Assert.False(_validator.Validate(built.File).IsValid);
    }

    [Fact]
    public void AnEmptyTypeTwoAttributeIsAcceptedBecauseTheStandardPermitsIt()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        built.File.Dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, string.Empty);

        Assert.True(_validator.Validate(built.File).IsValid);
    }

    [Fact]
    public void AWrongMimeTypeIsDetected()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        built.File.Dataset.AddOrUpdate(DicomTag.MIMETypeOfEncapsulatedDocument, "application/octet-stream");

        Assert.False(_validator.Validate(built.File).IsValid);
    }

    [Fact]
    public void ContentThatIsNotAPdfIsDetected()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        built.File.Dataset.AddOrUpdate(
            new DicomOtherByte(DicomTag.EncapsulatedDocument, Encoding.ASCII.GetBytes("NOT A PDF FILE!!")));

        ValidationResult result = _validator.Validate(built.File);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("PDF header", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IdenticalStudyAndSeriesIdentifiersAreDetected()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        string studyUid = built.File.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
        built.File.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, studyUid);

        Assert.False(_validator.Validate(built.File).IsValid);
    }

    [Fact]
    public void AMalformedDateIsDetected()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        built.File.Dataset.AddOrUpdate(DicomTag.StudyDate, "09-09-2026");

        Assert.False(_validator.Validate(built.File).IsValid);
    }

    [Fact]
    public void AMetaGroupThatDisagreesWithTheDataSetIsDetected()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();
        built.File.FileMetaInfo.AddOrUpdate(DicomTag.MediaStorageSOPInstanceUID, "1.2.3.4.5");

        ValidationResult result = _validator.Validate(built.File);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "FileMeta");
    }

    [Fact]
    public void AWrongSopClassIsDetected()
    {
        BuiltDicomObject built = TestSupport.BuildSampleObject();

        // The Secondary Capture SOP Class, which some tools produce for PDFs by mistake.
        built.File.Dataset.AddOrUpdate(DicomTag.SOPClassUID, "1.2.840.10008.5.1.4.1.1.7");

        Assert.False(_validator.Validate(built.File).IsValid);
    }
}
