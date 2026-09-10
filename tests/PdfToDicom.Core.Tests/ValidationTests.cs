using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Pdf;
using PdfToDicom.Core.Validation;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Covers the rules that stop a non conformant or mis-identified document from being
/// created in the first place.
/// </summary>
public sealed class PatientDemographicsValidatorTests
{
    private readonly PatientDemographicsValidator _validator = new(() => new DateTime(2026, 9, 9));

    [Fact]
    public void CompleteDemographicsAreAccepted()
    {
        ValidationResult result = _validator.Validate(TestSupport.CreateDemographics());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPatientIdIsRejected(string patientId)
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.PatientId = patientId;

        ValidationResult result = _validator.Validate(demographics);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == nameof(demographics.PatientId));
    }

    [Fact]
    public void EmptyPatientNameIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.FamilyName = string.Empty;
        demographics.GivenName = string.Empty;

        ValidationResult result = _validator.Validate(demographics);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == nameof(demographics.FamilyName));
    }

    [Fact]
    public void MissingBirthDateIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.DateOfBirth = null;

        ValidationResult result = _validator.Validate(demographics);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == nameof(demographics.DateOfBirth));
    }

    [Fact]
    public void FutureBirthDateIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.DateOfBirth = new DateTime(2026, 9, 10);

        ValidationResult result = _validator.Validate(demographics);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == nameof(demographics.DateOfBirth));
    }

    [Fact]
    public void ImplausiblyEarlyBirthDateIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.DateOfBirth = new DateTime(1801, 1, 1);

        Assert.False(_validator.Validate(demographics).IsValid);
    }

    [Fact]
    public void EmptyAccessionNumberIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.AccessionNumber = string.Empty;

        ValidationResult result = _validator.Validate(demographics);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == nameof(demographics.AccessionNumber));
    }

    [Fact]
    public void AccessionNumberLongerThanShortStringIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();

        // Accession Number has VR SH, limited to sixteen characters.
        demographics.AccessionNumber = new string('A', 17);

        ValidationResult result = _validator.Validate(demographics);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == nameof(demographics.AccessionNumber));
    }

    [Fact]
    public void PatientSexIsOptional()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.Sex = PatientSex.Unspecified;

        Assert.True(_validator.Validate(demographics).IsValid);
    }

    [Fact]
    public void AccentedNameIsAcceptedWhenTheCharacterSetIsUnicode()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.FamilyName = "Gonçalves Aparício";
        demographics.GivenName = "João";

        Assert.True(_validator.Validate(demographics, characterSetSupportsUnicode: true).IsValid);
    }

    [Fact]
    public void NonLatinNameIsRejectedWhenTheCharacterSetCannotRepresentIt()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();

        // Greek cannot be represented in ISO_IR 100, so the operator must be told rather
        // than having the name silently corrupted inside the archive.
        demographics.FamilyName = "Παπαδόπουλος";

        ValidationResult result = _validator.Validate(demographics, characterSetSupportsUnicode: false);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void CaretInANameComponentIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.FamilyName = "Silva^Santos";

        Assert.False(_validator.Validate(demographics).IsValid);
    }

    [Fact]
    public void AnEmbeddedControlCharacterFromABarcodeScannerIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();

        // A misread barcode can inject a control character into the middle of the value.
        // That cannot be corrected automatically, so the operator is asked to rescan.
        demographics.PatientId = "PID\u0009000123";

        ValidationResult result = _validator.Validate(demographics);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("barcode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ATrailingCarriageReturnFromAScannerIsToleratedRatherThanRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();

        // Most scanners are configured to send a terminator after the value. Rejecting
        // that would make the application unusable with standard hardware, so the
        // terminator is trimmed instead. The value itself is unambiguous.
        demographics.PatientId = "PID000123\r\n";
        demographics.AccessionNumber = "ACC0001\r";

        Assert.True(_validator.Validate(demographics).IsValid);
    }

    [Fact]
    public void MalformedStudyInstanceUidIsRejected()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.ExistingStudyInstanceUid = "1.2.03.4";

        Assert.False(_validator.Validate(demographics).IsValid);
    }
}

/// <summary>Covers the Value Representation level rules.</summary>
public sealed class DicomValueRulesTests
{
    [Theory]
    [InlineData("PACS")]
    [InlineData("PDF2DCM")]
    [InlineData("A")]
    [InlineData("ABCDEFGHIJKLMNOP")]
    public void ValidAeTitlesAreAccepted(string aeTitle) =>
        Assert.True(DicomValueRules.IsValidAeTitle(aeTitle, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    [InlineData(" LEADING")]
    [InlineData("TRAILING ")]
    [InlineData("BACK\\SLASH")]
    [InlineData("ACCENTÉD")]
    public void InvalidAeTitlesAreRejected(string aeTitle) =>
        Assert.False(DicomValueRules.IsValidAeTitle(aeTitle, out _));

    [Theory]
    [InlineData("1.2.840.10008.5.1.4.1.1.104.1")]
    [InlineData("2.25.123456789")]
    [InlineData("0")]
    public void ValidUidsAreAccepted(string uid) =>
        Assert.True(DicomValueRules.IsValidUid(uid, out _));

    [Theory]
    [InlineData("")]
    [InlineData("1.2.")]
    [InlineData(".1.2")]
    [InlineData("1.02.3")]
    [InlineData("1.2.a")]
    public void InvalidUidsAreRejected(string uid) =>
        Assert.False(DicomValueRules.IsValidUid(uid, out _));

    [Fact]
    public void UidLongerThanSixtyFourCharactersIsRejected()
    {
        string tooLong = "1." + string.Join('.', Enumerable.Repeat("123456", 12));

        Assert.True(tooLong.Length > DicomValueRules.MaxUidLength);
        Assert.False(DicomValueRules.IsValidUid(tooLong, out _));
    }

    [Fact]
    public void SanitiseRemovesBackslashesAndTrimsToLength()
    {
        string result = DicomValueRules.Sanitise("  ab\\cdef  ", maxLength: 4);

        Assert.Equal("abcd", result);
    }
}

/// <summary>Covers the checks applied to the selected PDF.</summary>
public sealed class PdfInspectorTests
{
    private readonly PdfInspector _inspector = new(new RecordingLogger());

    [Fact]
    public void SamplePdfIsAccepted() =>
        Assert.True(_inspector.Validate(TestSupport.SamplePdfPath).IsValid);

    [Fact]
    public void SamplePdfPageCountIsRead()
    {
        PdfDocumentInfo info = _inspector.Inspect(TestSupport.SamplePdfPath);

        Assert.Equal(2, info.PageCount);
        Assert.False(info.IsEncrypted);
        Assert.Equal("Example Radiology Report", info.Title);
    }

    [Fact]
    public void MissingFileIsRejected()
    {
        ValidationResult result = _inspector.Validate(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".pdf"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void EmptyFileIsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, Array.Empty<byte>());

        try
        {
            ValidationResult result = _inspector.Validate(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileWithoutAPdfHeaderIsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(path, "This is a text file that was renamed to .pdf");

        try
        {
            Assert.False(_inspector.Validate(path).IsValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NullPathIsRejected() =>
        Assert.False(_inspector.Validate(null).IsValid);
}

/// <summary>Covers the network configuration checks.</summary>
public sealed class PacsDestinationValidatorTests
{
    private readonly PacsDestinationValidator _validator = new();

    private static PacsDestination CreateDestination() => new()
    {
        Name = "Main PACS",
        CalledAeTitle = "PACS",
        Host = "192.168.1.50",
        Port = 104
    };

    [Fact]
    public void WellFormedDestinationIsAccepted() =>
        Assert.True(_validator.Validate(CreateDestination(), "PDF2DCM").IsValid);

    [Fact]
    public void InvalidCalledAeTitleIsRejected()
    {
        PacsDestination destination = CreateDestination();
        destination.CalledAeTitle = "THIS_AE_TITLE_IS_FAR_TOO_LONG";

        Assert.False(_validator.Validate(destination, "PDF2DCM").IsValid);
    }

    [Fact]
    public void InvalidCallingAeTitleIsRejected() =>
        Assert.False(_validator.Validate(CreateDestination(), string.Empty).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void PortOutsideTheValidRangeIsRejected(int port)
    {
        PacsDestination destination = CreateDestination();
        destination.Port = port;

        Assert.False(_validator.Validate(destination, "PDF2DCM").IsValid);
    }

    [Fact]
    public void MalformedHostIsRejected()
    {
        PacsDestination destination = CreateDestination();
        destination.Host = "not a host name";

        Assert.False(_validator.Validate(destination, "PDF2DCM").IsValid);
    }

    [Fact]
    public void HostNameIsAccepted()
    {
        PacsDestination destination = CreateDestination();
        destination.Host = "pacs.hospital.example";

        Assert.True(_validator.Validate(destination, "PDF2DCM").IsValid);
    }

    [Fact]
    public void ConfigurationWithNoEnabledDestinationIsRejected()
    {
        AppConfiguration configuration = TestSupport.CreateConfiguration();
        configuration.Destinations.ForEach(d => d.IsEnabled = false);

        Assert.False(_validator.ValidateAll(configuration).IsValid);
    }

    [Fact]
    public void DuplicateDestinationsProduceAWarningRatherThanAnError()
    {
        AppConfiguration configuration = TestSupport.CreateConfiguration();
        PacsDestination duplicate = configuration.Destinations[0].Clone();
        duplicate.Id = Guid.NewGuid().ToString("N");
        duplicate.Name = "Copy";
        configuration.Destinations.Add(duplicate);

        ValidationResult result = _validator.ValidateAll(configuration);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Message.Contains("more than once", StringComparison.OrdinalIgnoreCase));
    }
}
