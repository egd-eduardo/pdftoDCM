using System.Security.Cryptography;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Licensing;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Covers licence code issuing and checking.
/// </summary>
/// <remarks>
/// The most important assertions here are the negative ones: that a build with no
/// publisher key withholds nothing at all, and that a fault inside the licensing code
/// cannot stop a document being transmitted. A licensing subsystem that can lock a
/// hospital out of its own workflow is a far worse defect than an unlicensed copy.
/// </remarks>
public sealed class LicensingTests
{
    /// <summary>A publisher key pair generated for the test, never a real one.</summary>
    private static (ECDsa Key, string PublicKey) CreateKeyPair()
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static string Issue(ECDsa key, LicencePayload payload)
    {
        byte[] bytes = payload.ToBytes();
        return LicenceCodec.Encode(bytes, LicenceCodec.Sign(bytes, key));
    }

    private static LicencePayload Payload(
        string licensee = "Hospital de Exemplo",
        DateTime? expiresOn = null,
        byte[]? machineHash = null,
        int seats = 0) => new()
        {
            Licensee = licensee,
            IssuedOn = new DateTime(2026, 1, 1),
            ExpiresOn = expiresOn,
            MachineHash = machineHash,
            Seats = seats
        };

    // ---------------------------------------------------------------- Dormant

    [Fact]
    public void WithNoPublisherKeyNothingIsEnforced()
    {
        var service = new LicenceService();

        Assert.False(service.IsEnforced);

        // Every input, valid or not, must reach the same permissive answer.
        foreach (string? code in new[] { null, string.Empty, "   ", "nonsense", "PDFDCM1.a.b" })
        {
            LicenceStatus status = service.Check(code);

            Assert.Equal(LicenceState.NotEnforced, status.State);
            Assert.True(status.MayTransmit);
            Assert.False(status.ShouldWarn);
        }
    }

    [Fact]
    public void TheShippedBuildIsDormant()
    {
        // The constant is what decides whether an existing installation changes behaviour
        // when this feature is merged. It must stay blank until the publisher chooses
        // otherwise, so the guarantee is asserted rather than merely documented.
        Assert.Equal(LicenceService.PublisherPublicKey, string.Empty);
    }

    [Fact]
    public void AMalformedPublisherKeyIsTreatedAsUnenforcedRatherThanPunishingTheCustomer()
    {
        var logger = new RecordingLogger();
        var service = new LicenceService(logger, publicKeyOverride: "this is not a key");

        LicenceStatus status = service.Check("PDFDCM1.AQID.BAUG");

        Assert.Equal(LicenceState.NotEnforced, status.State);
        Assert.True(status.MayTransmit);
        Assert.NotEmpty(logger.Errors);
    }

    // ---------------------------------------------------------------- Happy path

    [Fact]
    public void AGenuineCodeIsAccepted()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            string code = Issue(key, Payload(seats: 20));
            LicenceStatus status = new LicenceService(publicKeyOverride: publicKey).Check(code);

            Assert.Equal(LicenceState.Valid, status.State);
            Assert.Equal("Hospital de Exemplo", status.Licensee);
            Assert.Equal(20, status.Seats);
            Assert.True(status.MayTransmit);
            Assert.False(status.ShouldWarn);
        }
    }

    [Fact]
    public void WhitespaceAndLineBreaksInAPastedCodeAreTolerated()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            string code = Issue(key, Payload());

            // What an email client does to a long code, and what a user does with it.
            string mangled = "  " + code[..20] + "\r\n" + code[20..40] + " \t " + code[40..] + "  ";

            Assert.Equal(
                LicenceState.Valid,
                new LicenceService(publicKeyOverride: publicKey).Check(mangled).State);
        }
    }

    [Fact]
    public void AnAccentedLicenseeNameSurvivesTheRoundTrip()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            string code = Issue(key, Payload("Hospital de São João, Núcleo de Imagiologia"));
            LicenceStatus status = new LicenceService(publicKeyOverride: publicKey).Check(code);

            Assert.Equal("Hospital de São João, Núcleo de Imagiologia", status.Licensee);
        }
    }

    // ---------------------------------------------------------------- Rejection

    [Fact]
    public void AMissingCodeWithholdsTransmission()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            LicenceStatus status = new LicenceService(publicKeyOverride: publicKey).Check(null);

            Assert.Equal(LicenceState.Missing, status.State);
            Assert.False(status.MayTransmit);
            Assert.True(status.ShouldWarn);
        }
    }

    [Fact]
    public void ACodeSignedByADifferentKeyIsRejected()
    {
        (ECDsa issuer, _) = CreateKeyPair();
        (ECDsa other, string otherPublicKey) = CreateKeyPair();

        using (issuer)
        using (other)
        {
            string code = Issue(issuer, Payload());
            LicenceStatus status = new LicenceService(publicKeyOverride: otherPublicKey).Check(code);

            Assert.Equal(LicenceState.Invalid, status.State);
            Assert.False(status.MayTransmit);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("PDFDCM1")]
    [InlineData("PDFDCM1.onlyonepart")]
    [InlineData("WRONGPREFIX.AQID.BAUG")]
    [InlineData("PDFDCM1...")]
    [InlineData("PDFDCM1.!!!!.????")]
    public void AMalformedCodeIsRejectedWithoutRaising(string code)
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            LicenceStatus status = new LicenceService(publicKeyOverride: publicKey).Check(code);

            Assert.False(status.MayTransmit);
            Assert.NotEmpty(status.Message);
        }
    }

    [Fact]
    public void EveryAlteredByteOfThePayloadIsDetected()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            var service = new LicenceService(publicKeyOverride: publicKey);
            string code = Issue(key, Payload(seats: 5));
            string[] parts = code.Split('.');

            // Flip one character of the payload at a time. Every single alteration must
            // fail verification: that is the whole basis of the protection.
            for (int i = 0; i < parts[1].Length; i++)
            {
                char original = parts[1][i];
                char replacement = original == 'A' ? 'B' : 'A';

                string tampered =
                    $"{parts[0]}.{parts[1][..i]}{replacement}{parts[1][(i + 1)..]}.{parts[2]}";

                Assert.False(service.Check(tampered).MayTransmit);
            }
        }
    }

    // ---------------------------------------------------------------- Expiry

    [Fact]
    public void AnInDateLicenceIsValid()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            string code = Issue(key, Payload(expiresOn: new DateTime(2026, 12, 31)));

            var service = new LicenceService(
                publicKeyOverride: publicKey,
                nowProvider: () => new DateTime(2026, 6, 1));

            LicenceStatus status = service.Check(code);

            Assert.Equal(LicenceState.Valid, status.State);
            Assert.Equal(new DateTime(2026, 12, 31), status.ExpiresOn);
        }
    }

    [Fact]
    public void TheDayAfterExpiryTransmissionContinuesUnderTheGracePeriod()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            var expiry = new DateTime(2026, 6, 30);
            string code = Issue(key, Payload(expiresOn: expiry));

            var service = new LicenceService(
                publicKeyOverride: publicKey,
                nowProvider: () => expiry.AddDays(1));

            LicenceStatus status = service.Check(code);

            // A hospital must never be cut off at midnight. It is warned, not stopped.
            Assert.Equal(LicenceState.ExpiringSoon, status.State);
            Assert.True(status.MayTransmit);
            Assert.True(status.ShouldWarn);
        }
    }

    [Fact]
    public void TheLastDayOfTheGracePeriodStillTransmits()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            var expiry = new DateTime(2026, 6, 30);
            string code = Issue(key, Payload(expiresOn: expiry));

            var service = new LicenceService(
                publicKeyOverride: publicKey,
                nowProvider: () => expiry.AddDays(LicenceService.GracePeriodDays));

            Assert.True(service.Check(code).MayTransmit);
        }
    }

    [Fact]
    public void TheDayAfterTheGracePeriodTransmissionStops()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            var expiry = new DateTime(2026, 6, 30);
            string code = Issue(key, Payload(expiresOn: expiry));

            var service = new LicenceService(
                publicKeyOverride: publicKey,
                nowProvider: () => expiry.AddDays(LicenceService.GracePeriodDays + 1));

            LicenceStatus status = service.Check(code);

            Assert.Equal(LicenceState.Expired, status.State);
            Assert.False(status.MayTransmit);
        }
    }

    [Fact]
    public void ALicenceWithNoExpiryNeverLapses()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            string code = Issue(key, Payload(expiresOn: null));

            var service = new LicenceService(
                publicKeyOverride: publicKey,
                nowProvider: () => new DateTime(2099, 1, 1));

            LicenceStatus status = service.Check(code);

            Assert.Equal(LicenceState.Valid, status.State);
            Assert.Null(status.ExpiresOn);
        }
    }

    // ---------------------------------------------------------------- Machine binding

    [Fact]
    public void ALicenceBoundToThisMachineIsAccepted()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            string code = Issue(key, Payload(machineHash: MachineFingerprint.CurrentHash()));
            LicenceStatus status = new LicenceService(publicKeyOverride: publicKey).Check(code);

            Assert.Equal(LicenceState.Valid, status.State);
            Assert.True(status.IsMachineBound);
        }
    }

    [Fact]
    public void ALicenceBoundToAnotherMachineIsRefusedAndSaysWhichMachineThisIs()
    {
        (ECDsa key, string publicKey) = CreateKeyPair();
        using (key)
        {
            byte[] elsewhere = MachineFingerprint.HashOfDisplayedId("AAAA-BBBB-CCCC-DDDD");
            string code = Issue(key, Payload(machineHash: elsewhere));

            LicenceStatus status = new LicenceService(publicKeyOverride: publicKey).Check(code);

            Assert.Equal(LicenceState.WrongMachine, status.State);
            Assert.False(status.MayTransmit);

            // The operator has to be able to quote this to have the licence reissued.
            Assert.Contains(MachineFingerprint.Current, status.Message);
        }
    }

    [Fact]
    public void TheMachineIdentifierIsStableAndReadableAloud()
    {
        string first = MachineFingerprint.Current;
        string second = MachineFingerprint.Current;

        Assert.Equal(first, second);
        Assert.Equal("XXXX-XXXX-XXXX-XXXX".Length, first.Length);
        Assert.All(first.Split('-'), group => Assert.Equal(4, group.Length));

        // The identifier a customer reads out must hash to the one this machine computes.
        Assert.Equal(MachineFingerprint.CurrentHash(), MachineFingerprint.HashOfDisplayedId(first));
    }

    [Fact]
    public void TheDisplayedIdentifierIsCaseAndSpacingInsensitive()
    {
        string identifier = MachineFingerprint.Current;

        Assert.Equal(
            MachineFingerprint.HashOfDisplayedId(identifier),
            MachineFingerprint.HashOfDisplayedId($"  {identifier.ToLowerInvariant().Replace("-", " ")}  "));
    }

    // ---------------------------------------------------------------- Payload

    [Fact]
    public void ACorruptPayloadIsReportedRatherThanRaised()
    {
        Assert.Null(LicencePayload.FromBytes(Array.Empty<byte>()));
        Assert.Null(LicencePayload.FromBytes(new byte[] { 1 }));
        Assert.Null(LicencePayload.FromBytes(new byte[] { 99, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void EveryFieldSurvivesTheBinaryRoundTrip()
    {
        var original = new LicencePayload
        {
            Licensee = "Clínica do Norte",
            IssuedOn = new DateTime(2026, 3, 15),
            ExpiresOn = new DateTime(2027, 3, 15),
            Seats = 42,
            MachineHash = MachineFingerprint.HashOfDisplayedId("1234-5678-9ABC-DEF0")
        };

        LicencePayload? restored = LicencePayload.FromBytes(original.ToBytes());

        Assert.NotNull(restored);
        Assert.Equal(original.Licensee, restored!.Licensee);
        Assert.Equal(original.IssuedOn, restored.IssuedOn);
        Assert.Equal(original.ExpiresOn, restored.ExpiresOn);
        Assert.Equal(original.Seats, restored.Seats);
        Assert.Equal(original.MachineHash, restored.MachineHash);
    }
}

/// <summary>
/// Covers what the workflow does about a licence, which is the part that could actually
/// harm a hospital if it were wrong.
/// </summary>
public sealed class LicensedWorkflowTests : IDisposable
{
    private readonly string _folder;
    private readonly ApplicationServices _services;
    private readonly StubNetworkService _network;

    /// <summary>Builds an isolated service graph for each test.</summary>
    public LicensedWorkflowTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "PdfToDicomTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);

        string configurationPath = Path.Combine(_folder, "config.json");

        AppConfiguration configuration = ConfigurationService.CreateDefaults();
        configuration.OutputFolder = Path.Combine(_folder, "out");
        configuration.LogFolder = Path.Combine(_folder, "logs");
        new ConfigurationService(configurationPath).Save(configuration);

        _services = new ApplicationServices(configurationPath);
        _network = new StubNetworkService();
    }

    private ConversionWorkflowService CreateWorkflow(ILicenceService? licence) =>
        new(_services.ConfigurationService,
            _services.PdfInspector,
            _services.Builder,
            _services.DicomValidator,
            _network,
            _services.FileWriter,
            _services.Logger,
            _services.AuditLogger,
            licence);

    [Fact]
    public async Task WithNoLicenceServiceTheWorkflowIsUnchanged()
    {
        ConversionResult result = await CreateWorkflow(null)
            .RunAsync(TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(1, _network.StoreCallCount);
    }

    [Fact]
    public async Task WithLicensingDormantTheWorkflowIsUnchanged()
    {
        // This is the case that every existing installation is in. It must behave exactly
        // as it did before licensing existed.
        ConversionResult result = await CreateWorkflow(new LicenceService())
            .RunAsync(TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(1, _network.StoreCallCount);
    }

    [Fact]
    public async Task AnUnlicensedCopyStillGeneratesAndSavesButDoesNotTransmit()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licence = new LicenceService(
            publicKeyOverride: Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        ConversionResult result = await CreateWorkflow(licence)
            .RunAsync(TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        // The clinician still gets the document. Only the transmission is withheld.
        Assert.True(result.DicomGenerated);
        Assert.NotNull(result.GeneratedFilePath);
        Assert.True(File.Exists(result.GeneratedFilePath));

        Assert.Equal(0, _network.StoreCallCount);
        Assert.Empty(result.Transmissions);

        // And the operator is told why, in the summary and in the issue list.
        Assert.Contains("licence", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.ValidationIssues, i => i.Field == "Licence");

        // A run that was asked to transmit and did not must never report success. An
        // empty transmission list alone cannot distinguish this from "generate only",
        // and the command line tool's exit code depends on the difference.
        Assert.True(result.TransmissionWithheld);
        Assert.False(result.IsCompleteSuccess);
    }

    [Fact]
    public async Task AGenerateOnlyRunIsStillACompleteSuccess()
    {
        // The other side of the distinction above: nothing was withheld here, because
        // nothing was asked for.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licence = new LicenceService(
            publicKeyOverride: Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        ConversionResult result = await CreateWorkflow(licence).RunAsync(
            TestSupport.SamplePdfPath,
            TestSupport.CreateDemographics(),
            new ConversionOptions(Transmit: false));

        Assert.False(result.TransmissionWithheld);
        Assert.True(result.IsCompleteSuccess);
    }

    [Fact]
    public async Task AProperlyLicensedCopyTransmitsNormally()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var payload = new LicencePayload
        {
            Licensee = "Hospital de Exemplo",
            IssuedOn = DateTime.UtcNow.Date
        };

        byte[] bytes = payload.ToBytes();

        AppConfiguration configuration = _services.ConfigurationService.Current;
        configuration.LicenceCode = LicenceCodec.Encode(bytes, LicenceCodec.Sign(bytes, key));
        _services.ConfigurationService.Save(configuration);

        var licence = new LicenceService(
            publicKeyOverride: Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        ConversionResult result = await CreateWorkflow(licence)
            .RunAsync(TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(1, _network.StoreCallCount);
    }

    [Fact]
    public async Task TheRetryActionCannotBeUsedToBypassTheLicence()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licence = new LicenceService(
            publicKeyOverride: Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        ConversionWorkflowService workflow = CreateWorkflow(licence);

        // Generate an object without transmitting, then attempt to send it by the retry
        // path, which does not go through RunAsync.
        ConversionResult generated = await workflow.RunAsync(
            TestSupport.SamplePdfPath,
            TestSupport.CreateDemographics(),
            new ConversionOptions(Transmit: false));

        Assert.NotNull(generated.GeneratedFilePath);

        IReadOnlyList<TransmissionResult> results = await workflow.RetryTransmissionAsync(
            FellowOakDicom.DicomFile.Open(generated.GeneratedFilePath!),
            generated.SopInstanceUid!);

        Assert.Equal(0, _network.StoreCallCount);
        Assert.All(results, r => Assert.False(r.IsSuccess));
    }

    [Fact]
    public async Task AFaultInsideTheLicenceCheckMustNotStopATransmission()
    {
        // The guarantee that matters most: whatever goes wrong in the licensing code, a
        // clinician can still send the report.
        ConversionResult result = await CreateWorkflow(new ThrowingLicenceService())
            .RunAsync(TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.True(result.IsCompleteSuccess);
        Assert.Equal(1, _network.StoreCallCount);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A locked log file is not a test failure.
        }
    }
}

/// <summary>A licence service that fails, to prove the workflow survives it.</summary>
internal sealed class ThrowingLicenceService : ILicenceService
{
    /// <inheritdoc />
    public bool IsEnforced => true;

    /// <inheritdoc />
    public string MachineId => throw new InvalidOperationException("Deliberate test failure.");

    /// <inheritdoc />
    public LicenceStatus Check(string? licenceCode) =>
        throw new InvalidOperationException("Deliberate test failure.");
}
