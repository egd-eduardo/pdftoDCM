using System.Text.Json;
using FellowOakDicom;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Core.Validation;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Covers persistence of the configuration, including the failure modes that would
/// otherwise stop the application from starting on a clinical workstation.
/// </summary>
public sealed class ConfigurationServiceTests : IDisposable
{
    private readonly string _folder;
    private readonly string _path;

    /// <summary>Creates an isolated configuration file for each test.</summary>
    public ConfigurationServiceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "PdfToDicomTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "config.json");
    }

    [Fact]
    public void AMissingFileProducesUsableDefaultsAndIsWrittenToDisk()
    {
        var service = new ConfigurationService(_path);
        AppConfiguration configuration = service.Load();

        Assert.True(File.Exists(_path));
        Assert.False(string.IsNullOrWhiteSpace(configuration.CallingAeTitle));
        Assert.False(string.IsNullOrWhiteSpace(configuration.OutputFolder));
        Assert.False(string.IsNullOrWhiteSpace(configuration.LogFolder));
        Assert.NotEmpty(configuration.Destinations);
    }

    [Fact]
    public void SavedValuesSurviveAReload()
    {
        var service = new ConfigurationService(_path);
        AppConfiguration configuration = service.Load();

        configuration.InstitutionName = "Hospital de São João";
        configuration.CallingAeTitle = "WARD07";
        configuration.CharacterSet = DicomCharacterSet.Latin1;
        configuration.Destinations[0].Port = 11112;
        service.Save(configuration);

        AppConfiguration reloaded = new ConfigurationService(_path).Load();

        Assert.Equal("Hospital de São João", reloaded.InstitutionName);
        Assert.Equal("WARD07", reloaded.CallingAeTitle);
        Assert.Equal(DicomCharacterSet.Latin1, reloaded.CharacterSet);
        Assert.Equal(11112, reloaded.Destinations[0].Port);
    }

    [Fact]
    public void ACorruptFileIsQuarantinedAndReplacedRatherThanStoppingStartup()
    {
        File.WriteAllText(_path, "{ this is not valid json");

        var service = new ConfigurationService(_path);
        AppConfiguration configuration = service.Load();

        // The application must still start.
        Assert.NotNull(configuration);
        Assert.NotEmpty(configuration.CallingAeTitle);

        // The damaged file must still be available for investigation.
        Assert.NotEmpty(Directory.GetFiles(_folder, "*.corrupt-*"));
    }

    [Fact]
    public void OutOfRangeValuesAreRepairedOnLoad()
    {
        AppConfiguration configuration = ConfigurationService.CreateDefaults();
        configuration.Destinations[0].Port = 999_999;
        configuration.Destinations[0].TimeoutSeconds = -5;
        configuration.Destinations[0].RetryCount = 500;
        configuration.LogRetentionDays = -1;

        File.WriteAllText(_path, JsonSerializer.Serialize(configuration, new JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        }));

        AppConfiguration loaded = new ConfigurationService(_path).Load();

        Assert.InRange(loaded.Destinations[0].Port, 1, 65535);
        Assert.InRange(loaded.Destinations[0].TimeoutSeconds, 1, 600);
        Assert.InRange(loaded.Destinations[0].RetryCount, 0, 10);
        Assert.True(loaded.LogRetentionDays >= 0);
    }

    [Fact]
    public void TheCharacterSetMapsToTheCorrectDefinedTerm()
    {
        var configuration = new AppConfiguration { CharacterSet = DicomCharacterSet.Utf8 };
        Assert.Equal("ISO_IR 192", configuration.SpecificCharacterSetValue);
        Assert.True(configuration.CharacterSetSupportsUnicode);

        configuration.CharacterSet = DicomCharacterSet.Latin1;
        Assert.Equal("ISO_IR 100", configuration.SpecificCharacterSetValue);
        Assert.False(configuration.CharacterSetSupportsUnicode);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // Cleanup only.
        }
    }
}

/// <summary>
/// Covers the orchestration layer end to end, short of the network.
/// </summary>
/// <remarks>
/// Transmission is exercised with a stub network service so the tests remain
/// deterministic and runnable without a PACS. Verification against a real archive is
/// covered separately by the DCMTK based integration script described in the technical
/// documentation.
/// </remarks>
public sealed class ConversionWorkflowServiceTests : IDisposable
{
    private readonly string _folder;
    private readonly ApplicationServices _services;
    private readonly StubNetworkService _network;

    /// <summary>Builds an isolated service graph for each test.</summary>
    public ConversionWorkflowServiceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "PdfToDicomTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);

        string configurationPath = Path.Combine(_folder, "config.json");

        AppConfiguration configuration = ConfigurationService.CreateDefaults();
        configuration.OutputFolder = Path.Combine(_folder, "out");
        configuration.LogFolder = Path.Combine(_folder, "logs");
        configuration.InstitutionName = "Test Hospital";
        new ConfigurationService(configurationPath).Save(configuration);

        _services = new ApplicationServices(configurationPath);
        _network = new StubNetworkService();

        Workflow = new ConversionWorkflowService(
            _services.ConfigurationService,
            _services.PdfInspector,
            _services.Builder,
            _services.DicomValidator,
            _network,
            _services.FileWriter,
            _services.Logger,
            _services.AuditLogger);
    }

    private ConversionWorkflowService Workflow { get; }

    [Fact]
    public async Task ASuccessfulRunGeneratesSavesAndTransmits()
    {
        ConversionResult result = await Workflow.RunAsync(
            TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.True(result.DicomGenerated);
        Assert.True(result.IsCompleteSuccess);
        Assert.NotNull(result.GeneratedFilePath);
        Assert.True(File.Exists(result.GeneratedFilePath));
        Assert.Single(result.Transmissions);
        Assert.True(result.Transmissions[0].IsSuccess);
    }

    [Fact]
    public async Task IncompleteDemographicsStopTheRunBeforeAnythingIsGenerated()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.PatientId = string.Empty;

        ConversionResult result = await Workflow.RunAsync(TestSupport.SamplePdfPath, demographics);

        Assert.False(result.DicomGenerated);
        Assert.Empty(result.Transmissions);
        Assert.Contains(result.ValidationIssues, i => i.Severity == ValidationSeverity.Error);

        // Nothing must have been written, and above all nothing must have been sent.
        Assert.Equal(0, _network.StoreCallCount);
    }

    [Fact]
    public async Task GenerateOnlySavesTheObjectWithoutOpeningAnAssociation()
    {
        ConversionResult result = await Workflow.RunAsync(
            TestSupport.SamplePdfPath,
            TestSupport.CreateDemographics(),
            new ConversionOptions(Transmit: false));

        Assert.True(result.DicomGenerated);
        Assert.NotNull(result.GeneratedFilePath);
        Assert.Empty(result.Transmissions);
        Assert.Equal(0, _network.StoreCallCount);
    }

    [Fact]
    public async Task AFailedTransmissionIsReportedAndTheLocalCopyIsKept()
    {
        _network.NextOutcome = TransmissionOutcome.ConnectionFailed;

        ConversionResult result = await Workflow.RunAsync(
            TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.True(result.DicomGenerated);
        Assert.False(result.IsCompleteSuccess);
        Assert.False(result.AnyTransmissionSucceeded);

        // The object must survive a failure so the operator can retry without re-entering
        // the patient details or regenerating the document.
        Assert.NotNull(result.GeneratedFilePath);
        Assert.True(File.Exists(result.GeneratedFilePath));
        Assert.False(result.GeneratedFileDeleted);
    }

    [Fact]
    public async Task TheLocalCopyIsDeletedOnlyWhenConfiguredAndTheTransmissionSucceeded()
    {
        AppConfiguration configuration = _services.ConfigurationService.Current;
        configuration.AutoDeleteAfterSuccessfulTransmission = true;
        _services.ConfigurationService.Save(configuration);

        ConversionResult result = await Workflow.RunAsync(
            TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.True(result.IsCompleteSuccess);
        Assert.True(result.GeneratedFileDeleted);
        Assert.Null(result.GeneratedFilePath);
    }

    [Fact]
    public async Task TheGeneratedObjectPassesIodValidationOnDisk()
    {
        ConversionResult result = await Workflow.RunAsync(
            TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.NotNull(result.GeneratedFilePath);

        DicomFile reopened = DicomFile.Open(result.GeneratedFilePath!);
        ValidationResult validation = new EncapsulatedPdfValidator().Validate(reopened);

        Assert.True(validation.IsValid, validation.ToDisplayString());
    }

    [Fact]
    public async Task EachRunProducesANewInstanceIdentifier()
    {
        ConversionResult first = await Workflow.RunAsync(
            TestSupport.SamplePdfPath, TestSupport.CreateDemographics());
        ConversionResult second = await Workflow.RunAsync(
            TestSupport.SamplePdfPath, TestSupport.CreateDemographics());

        Assert.NotEqual(first.SopInstanceUid, second.SopInstanceUid);

        // Two documents written in the same second must not collide on the file system.
        Assert.NotEqual(first.GeneratedFilePath, second.GeneratedFilePath);
    }

    [Fact]
    public void ValidateInputsMirrorsTheRulesTheRunWillApply()
    {
        PatientDemographics demographics = TestSupport.CreateDemographics();
        demographics.AccessionNumber = string.Empty;

        ValidationResult result = Workflow.ValidateInputs(
            TestSupport.SamplePdfPath, demographics, requireDestination: true);

        Assert.False(result.IsValid);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // Cleanup only.
        }
    }
}

/// <summary>
/// A network service that records what it was asked to do and returns a configured
/// outcome, so the workflow can be tested without a PACS.
/// </summary>
internal sealed class StubNetworkService : Abstractions.IDicomNetworkService
{
    /// <summary>Outcome returned by the next call to <see cref="StoreAsync"/>.</summary>
    public TransmissionOutcome NextOutcome { get; set; } = TransmissionOutcome.Succeeded;

    /// <summary>Number of store attempts made.</summary>
    public int StoreCallCount { get; private set; }

    /// <summary>Number of verification attempts made.</summary>
    public int VerifyCallCount { get; private set; }

    /// <inheritdoc />
    public Task<TransmissionResult> VerifyAsync(
        PacsDestination destination,
        string callingAeTitle,
        CancellationToken cancellationToken = default)
    {
        VerifyCallCount++;
        return Task.FromResult(Build(destination, TransmissionOutcome.Succeeded));
    }

    /// <inheritdoc />
    public Task<TransmissionResult> StoreAsync(
        DicomFile file,
        PacsDestination destination,
        string callingAeTitle,
        IProgress<Abstractions.TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        StoreCallCount++;
        return Task.FromResult(Build(destination, NextOutcome));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TransmissionResult>> StoreToAllAsync(
        DicomFile file,
        IReadOnlyList<PacsDestination> destinations,
        string callingAeTitle,
        IProgress<Abstractions.TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TransmissionResult>();

        foreach (PacsDestination destination in destinations)
        {
            results.Add(await StoreAsync(file, destination, callingAeTitle, progress, cancellationToken));
        }

        return results;
    }

    private static TransmissionResult Build(PacsDestination destination, TransmissionOutcome outcome) => new()
    {
        DestinationName = destination.Name,
        CalledAeTitle = destination.CalledAeTitle,
        Host = destination.Host,
        Port = destination.Port,
        Outcome = outcome,
        AttemptCount = 1,
        Duration = TimeSpan.FromMilliseconds(5),
        DicomStatusCode = outcome == TransmissionOutcome.Succeeded ? "0000" : null,
        Message = outcome == TransmissionOutcome.Succeeded
            ? "Accepted by the stub."
            : "Rejected by the stub."
    };
}
