using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Integration;
using PdfToDicom.Interop.Hl7V2.Dispatch;
using PdfToDicom.Interop.Hl7V2.Profiles;
using PdfToDicom.Interop.Hl7V2.Transport;
using PdfToDicom.Interop.Transactions;
using Xunit;

namespace PdfToDicom.Integration.Tests;

/// <summary>A conversion that succeeds or fails on demand, and counts the asking.</summary>
internal sealed class StubConversion : IConversionRunner
{
    private readonly bool _succeeds;

    public StubConversion(bool succeeds = true)
    {
        _succeeds = succeeds;
    }

    public int Calls { get; private set; }

    public Task<ConversionResult> RunAsync(
        string sourcePath,
        PatientDemographics demographics,
        ConversionOptions? options,
        IProgress<TransmissionProgress>? progress,
        CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult(new ConversionResult
        {
            DicomGenerated = _succeeds,
            StudyInstanceUid = _succeeds ? "1.2.3.4" : null,
            SopInstanceUid = _succeeds ? "1.2.3.6" : null,
            SopInstanceUids = _succeeds ? ["1.2.3.6"] : [],
            Transmissions = _succeeds
                ? [new TransmissionResult
                   {
                       DestinationName = "Archive",
                       Outcome = TransmissionOutcome.Succeeded,
                       Message = "accepted"
                   }]
                : [],
            Summary = _succeeds ? "The document was sent." : "The document was not produced."
        });
    }
}

/// <summary>A transport that always answers the same way.</summary>
internal sealed class AlwaysTransport : IHl7Transport
{
    private readonly Hl7SendResult _result;

    public AlwaysTransport(Hl7SendResult result)
    {
        _result = result;
    }

    public string DestinationName => "Always";

    public int Calls { get; private set; }

    public Task<Hl7SendResult> SendAsync(
        ReadOnlyMemory<byte> encodedMessage,
        IProgress<Hl7TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(_result);
    }
}

internal sealed class NullStore : ITransactionStore
{
    public Task RecordAsync(IntegrationTransaction transaction, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IntegrationTransaction>> ReadDayAsync(
        DateOnly day, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IntegrationTransaction>>([]);

    public Task<IntegrationTransaction?> FindEarlierDeliveryAsync(
        string contentHash, string destinationName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IntegrationTransaction?>(null);
}

internal sealed class CountingLogger : IAppLogger
{
    public List<string> Lines { get; } = [];

    public string LogFolder => string.Empty;

    public void Debug(string message) => Lines.Add(message);
    public void Information(string message) => Lines.Add(message);
    public void Warning(string message) => Lines.Add(message);
    public void Error(string message, Exception? exception = null) => Lines.Add(message);
    public void Flush() { }
}

public sealed class IntegratedConversionServiceTests
{
    private const string ProfileJson = """
    {
      "profileId": "p", "messageType": "ORU", "triggerEvent": "R01",
      "acknowledgement": { "expect": "none", "acceptAckType": "NE", "applicationAckType": "NE" },
      "mappings": [ { "target": "PID-3.1", "source": "patient.id", "required": true } ]
    }
    """;

    private static Hl7Profile Profile() => Hl7ProfileReader.Read(ProfileJson).Profile!;

    private static Hl7Destination Destination() =>
        new() { Id = "records", Name = "Record system", RetryCount = 0 };

    private static PatientDemographics Patient() =>
        new() { PatientId = "1234567", FamilyName = "Silva" };

    private static Hl7Plan PlanFor(IntegrationSequence sequence) =>
        new([new Hl7Delivery(Destination(), Profile())], sequence);

    private static Hl7Dispatcher DispatcherWith(IHl7Transport transport) =>
        new(new NullStore(), (_, _) => transport, delay: (_, _) => Task.CompletedTask);

    private static Hl7SendResult Sent() =>
        new(Hl7DeliveryState.Sent, "sent", BytesWereWritten: true);

    private static Hl7SendResult Unreachable() =>
        new(Hl7DeliveryState.Failed, "could not be reached", BytesWereWritten: false);

    [Fact]
    public async Task WithNothingConfiguredTheConversionRunsAndNothingElseHappens()
    {
        // The ordinary case, and the one that has to be identical to the product before
        // any of this existed.
        var conversion = new StubConversion();

        var service = new IntegratedConversionService(conversion, new CountingLogger());

        IntegratedConversionResult result = await service.RunAsync("report.pdf", Patient());

        Assert.Equal(1, conversion.Calls);
        Assert.Empty(result.Hl7);
        Assert.NotNull(result.Dicom);
        Assert.Equal("The document was sent.", result.Summary);
    }

    [Fact]
    public async Task ADispatcherWithAnEmptyPlanAlsoChangesNothing()
    {
        var conversion = new StubConversion();
        var transport = new AlwaysTransport(Sent());

        var service = new IntegratedConversionService(
            conversion, new CountingLogger(), DispatcherWith(transport));

        IntegratedConversionResult result = await service.RunAsync("report.pdf", Patient());

        Assert.Equal(1, conversion.Calls);
        Assert.Equal(0, transport.Calls);
        Assert.Empty(result.Hl7);
    }

    [Fact]
    public async Task IndependentlyTheMessageGoesEvenWhenTheConversionFailed()
    {
        // Two separate pieces of value. A record system hearing about a report is worth
        // something even when the archive did not take the object.
        var conversion = new StubConversion(succeeds: false);
        var transport = new AlwaysTransport(Sent());

        var service = new IntegratedConversionService(
            conversion, new CountingLogger(), DispatcherWith(transport));

        IntegratedConversionResult result = await service.RunAsync(
            "report.pdf", Patient(), PlanFor(IntegrationSequence.Independent));

        Assert.Equal(1, transport.Calls);
        Assert.Single(result.Hl7);
        Assert.False(result.DicomSucceeded);
    }

    [Fact]
    public async Task AnHl7FailureNeverChangesTheDicomResult()
    {
        var conversion = new StubConversion();
        var transport = new AlwaysTransport(Unreachable());

        var service = new IntegratedConversionService(
            conversion, new CountingLogger(), DispatcherWith(transport));

        IntegratedConversionResult result = await service.RunAsync(
            "report.pdf", Patient(), PlanFor(IntegrationSequence.Independent));

        Assert.True(result.DicomSucceeded);
        Assert.False(result.Hl7Succeeded);
        Assert.True(result.NeedsAttention);
    }

    [Fact]
    public async Task AnEngineThatThrowsCannotReachTheConversionPath()
    {
        // Deliberately everything is caught. A report in the archive is worth more than a
        // tidy stack trace, and the operator can act on one and not the other.
        var conversion = new StubConversion();
        var logger = new CountingLogger();

        var throwing = new Hl7Dispatcher(
            new NullStore(),
            (_, _) => throw new InvalidOperationException("the engine fell over"),
            delay: (_, _) => Task.CompletedTask);

        var service = new IntegratedConversionService(conversion, logger, throwing);

        IntegratedConversionResult result = await service.RunAsync(
            "report.pdf", Patient(), PlanFor(IntegrationSequence.Independent));

        Assert.True(result.DicomSucceeded);
        Assert.Equal(Hl7DispatchOutcome.Failed, Assert.Single(result.Hl7).Outcome);
        Assert.Contains("The DICOM side was not affected.",
            result.Hl7[0].Message, StringComparison.Ordinal);
        Assert.Contains(logger.Lines, l => l.Contains("the engine fell over", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnnouncingAfterTheArchiveMeansNothingIsAnnouncedWhenTheArchiveRefused()
    {
        // A message carrying a Study Instance UID that no archive holds points at nothing.
        var conversion = new StubConversion(succeeds: false);
        var transport = new AlwaysTransport(Sent());
        var logger = new CountingLogger();

        var service = new IntegratedConversionService(
            conversion, logger, DispatcherWith(transport));

        IntegratedConversionResult result = await service.RunAsync(
            "report.pdf", Patient(), PlanFor(IntegrationSequence.DicomThenHl7));

        Assert.Equal(0, transport.Calls);
        Assert.Empty(result.Hl7);
        Assert.Contains(logger.Lines, l => l.Contains("No HL7 message was sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnnouncingAfterTheArchiveSendsWhenTheArchiveAccepted()
    {
        var transport = new AlwaysTransport(Sent());

        var service = new IntegratedConversionService(
            new StubConversion(), new CountingLogger(), DispatcherWith(transport));

        IntegratedConversionResult result = await service.RunAsync(
            "report.pdf", Patient(), PlanFor(IntegrationSequence.DicomThenHl7));

        Assert.Equal(1, transport.Calls);
        Assert.True(result.Hl7Succeeded);
    }

    [Fact]
    public async Task AnnouncingFirstStopsTheConversionWhenTheMessageDidNotLand()
    {
        // The one sequence where HL7 stops DICOM, and it only happens because somebody
        // chose it. Nothing is left half done: the document is not converted at all.
        var conversion = new StubConversion();
        var transport = new AlwaysTransport(Unreachable());
        var logger = new CountingLogger();

        var service = new IntegratedConversionService(
            conversion, logger, DispatcherWith(transport));

        IntegratedConversionResult result = await service.RunAsync(
            "report.pdf", Patient(), PlanFor(IntegrationSequence.Hl7ThenDicom));

        Assert.Equal(0, conversion.Calls);
        Assert.Null(result.Dicom);
        Assert.Single(result.Hl7);
        Assert.Contains(logger.Lines, l => l.Contains("was not converted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnnouncingFirstConvertsOnceTheMessageLanded()
    {
        var conversion = new StubConversion();
        var transport = new AlwaysTransport(Sent());

        var service = new IntegratedConversionService(
            conversion, new CountingLogger(), DispatcherWith(transport));

        IntegratedConversionResult result = await service.RunAsync(
            "report.pdf", Patient(), PlanFor(IntegrationSequence.Hl7ThenDicom));

        Assert.Equal(1, conversion.Calls);
        Assert.True(result.DicomSucceeded);
    }

    [Fact]
    public async Task TheSummaryKeepsTheTwoHalvesApart()
    {
        // An operator acts on them completely differently: a failed archive transmission
        // means the report is not where a clinician looks, and a failed message means a
        // record system has not been told about a report that is safely filed.
        var service = new IntegratedConversionService(
            new StubConversion(), new CountingLogger(), DispatcherWith(new AlwaysTransport(Unreachable())));

        IntegratedConversionResult result = await service.RunAsync(
            "report.pdf", Patient(), PlanFor(IntegrationSequence.Independent));

        Assert.Contains("The document was sent.", result.Summary, StringComparison.Ordinal);
        Assert.Contains("HL7:", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConversionIsPassedTheSopInstanceUidSoTheTwoCanBeReconciled()
    {
        var recorded = new List<IntegrationTransaction>();

        var store = new RecordingStore(recorded);

        var service = new IntegratedConversionService(
            new StubConversion(), new CountingLogger(),
            new Hl7Dispatcher(store, (_, _) => new AlwaysTransport(Sent()),
                delay: (_, _) => Task.CompletedTask));

        await service.RunAsync("report.pdf", Patient(), PlanFor(IntegrationSequence.Independent));

        Assert.Contains(recorded, t => t.RelatedSopInstanceUid == "1.2.3.6");
    }

    private sealed class RecordingStore(List<IntegrationTransaction> into) : ITransactionStore
    {
        public Task RecordAsync(IntegrationTransaction transaction, CancellationToken cancellationToken = default)
        {
            into.Add(transaction);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IntegrationTransaction>> ReadDayAsync(
            DateOnly day, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IntegrationTransaction>>(into);

        public Task<IntegrationTransaction?> FindEarlierDeliveryAsync(
            string contentHash, string destinationName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IntegrationTransaction?>(null);
    }
}
