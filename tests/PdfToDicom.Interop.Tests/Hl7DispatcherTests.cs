using PdfToDicom.Interop.Hl7V2.Dispatch;
using PdfToDicom.Interop.Hl7V2.Profiles;
using PdfToDicom.Interop.Hl7V2.Transport;
using PdfToDicom.Interop.Messaging;
using PdfToDicom.Interop.Transactions;
using Xunit;

namespace PdfToDicom.Interop.Tests;

/// <summary>A transport that returns whatever a test tells it to, and counts the asking.</summary>
internal sealed class ScriptedTransport : IHl7Transport
{
    private readonly Queue<Hl7SendResult> _results;

    public ScriptedTransport(params Hl7SendResult[] results)
    {
        _results = new Queue<Hl7SendResult>(results);
    }

    public string DestinationName => "Scripted";

    public int Calls { get; private set; }

    public bool ExpectedAcknowledgement { get; set; }

    public Task<Hl7SendResult> SendAsync(
        ReadOnlyMemory<byte> encodedMessage,
        IProgress<Hl7TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(_results.Count > 0
            ? _results.Dequeue()
            : new Hl7SendResult(Hl7DeliveryState.Acknowledged, "ok", BytesWereWritten: true));
    }

    public static Hl7SendResult Acknowledged(string ack = "MSA|AA|1") =>
        new(Hl7DeliveryState.Acknowledged, "replied",
            System.Text.Encoding.ASCII.GetBytes(ack), BytesWereWritten: true);

    public static Hl7SendResult Sent() =>
        new(Hl7DeliveryState.Sent, "sent", BytesWereWritten: true);

    /// <summary>A failure before anything left: safe to try again.</summary>
    public static Hl7SendResult Refused() =>
        new(Hl7DeliveryState.Failed, "could not be reached", BytesWereWritten: false);

    /// <summary>A failure after the bytes left: not safe to try again.</summary>
    public static Hl7SendResult TimedOutAfterSending() =>
        new(Hl7DeliveryState.Failed, "did not respond within the configured timeout.",
            BytesWereWritten: true);
}

/// <summary>A transaction store in memory, so a test asserts rather than reads files.</summary>
internal sealed class RecordingStore : ITransactionStore
{
    public List<IntegrationTransaction> Written { get; } = [];

    public Task RecordAsync(IntegrationTransaction transaction, CancellationToken cancellationToken = default)
    {
        Written.Add(transaction);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IntegrationTransaction>> ReadDayAsync(
        DateOnly day, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IntegrationTransaction>>(Written);

    public Task<IntegrationTransaction?> FindEarlierDeliveryAsync(
        string contentHash, string destinationName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Written.LastOrDefault(t =>
            t.ContentHash == contentHash &&
            t.DestinationName == destinationName &&
            t.DeliveryState is "Sent" or "Acknowledged"));
}

public sealed class Hl7DispatcherTests
{
    private const string ReportProfile = """
    {
      "profileId": "example-oru-r01",
      "messageType": "ORU", "triggerEvent": "R01", "hl7Version": "2.4",
      "encoding": { "characterSet": "8859/1" },
      "acknowledgement": { "expect": "none", "acceptAckType": "NE", "applicationAckType": "NE" },
      "segments": [ { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
                    { "id": "PID", "usage": "R", "min": 1, "max": 1 } ],
      "fields": [ { "path": "PID-3.1", "name": "Patient identifier", "usage": "R" } ],
      "mappings": [
        { "target": "MSH-3", "source": "destination.sendingApplication" },
        { "target": "MSH-5", "source": "destination.receivingApplication" },
        { "target": "PID-3.1", "source": "patient.id", "required": true }
      ]
    }
    """;

    private static Hl7Profile Profile(string json = ReportProfile) =>
        Hl7ProfileReader.Read(json).Profile!;

    private static Hl7Destination Destination(int retries = 2) => new()
    {
        Id = "archive",
        Name = "Report archive",
        Host = "127.0.0.1",
        Port = 2575,
        RetryCount = retries,
        RetryDelaySeconds = 0,
        SendingApplication = "PDFTODICOM",
        ReceivingApplication = "ARCHIVE"
    };

    private static MessageSubject Subject() => new MessageSubject().With("patient.id", "1234567");

    private static Hl7Dispatcher DispatcherFor(
        ScriptedTransport transport, RecordingStore store) =>
        new(store,
            (_, expectAck) => { transport.ExpectedAcknowledgement = expectAck; return transport; },
            delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task AGoodMessageGoesOutAndIsWrittenDown()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Acknowledged());
        var store = new RecordingStore();

        Hl7DispatchResult result = await DispatcherFor(transport, store)
            .SendAsync(Profile(), Destination(), Subject(), "1.2.3.4");

        Assert.Equal(Hl7DispatchOutcome.Acknowledged, result.Outcome);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, result.Attempts);

        IntegrationTransaction written = Assert.Single(store.Written);
        Assert.Equal("Acknowledged", written.DeliveryState);
        Assert.Equal("ORU^R01", written.MessageType);
        Assert.Equal("example-oru-r01", written.ProfileId);
        Assert.Equal("Report archive", written.DestinationName);
        Assert.Equal("1.2.3.4", written.RelatedSopInstanceUid);
        Assert.Equal(result.MessageControlId, written.MessageControlId);
    }

    [Fact]
    public async Task TheDestinationsOwnIdentifiersAreAvailableToTheProfile()
    {
        // So that a site can map MSH-3 and MSH-5 without the mapping language having to
        // know what a destination is.
        var transport = new ScriptedTransport(ScriptedTransport.Acknowledged());

        Hl7DispatchResult result = await DispatcherFor(transport, new RecordingStore())
            .SendAsync(Profile(), Destination(), Subject());

        Assert.Equal("PDFTODICOM", result.Built!["MSH-3"]);
        Assert.Equal("ARCHIVE", result.Built["MSH-5"]);
    }

    [Fact]
    public async Task AProfileThatAcknowledgesNothingIsNotWaitedOn()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Sent());

        await DispatcherFor(transport, new RecordingStore())
            .SendAsync(Profile(), Destination(), Subject());

        Assert.False(transport.ExpectedAcknowledgement);
    }

    [Fact]
    public async Task AMessageThatFailsItsProfileIsNeverSentAndIsStillRecorded()
    {
        // An audit trail that omits refusals hides how often an interface is misconfigured,
        // which is exactly what somebody commissioning it needs to see.
        var transport = new ScriptedTransport();
        var store = new RecordingStore();

        Hl7DispatchResult result = await DispatcherFor(transport, store)
            .SendAsync(Profile(), Destination(), MessageSubject.Empty);

        Assert.Equal(Hl7DispatchOutcome.Refused, result.Outcome);
        Assert.Equal(0, transport.Calls);
        Assert.NotNull(result.Built);
        Assert.NotEmpty(result.Issues);

        Assert.Equal("Refused", Assert.Single(store.Written).DeliveryState);
    }

    [Fact]
    public async Task AFailureBeforeAnythingLeftIsTriedAgain()
    {
        var transport = new ScriptedTransport(
            ScriptedTransport.Refused(),
            ScriptedTransport.Refused(),
            ScriptedTransport.Acknowledged());

        var store = new RecordingStore();

        Hl7DispatchResult result = await DispatcherFor(transport, store)
            .SendAsync(Profile(), Destination(retries: 2), Subject());

        Assert.Equal(Hl7DispatchOutcome.Acknowledged, result.Outcome);
        Assert.Equal(3, transport.Calls);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, store.Written.Count);
        Assert.Equal([1, 2, 3], store.Written.Select(t => t.AttemptNumber));
    }

    [Fact]
    public async Task WhenEveryAttemptIsRefusedTheFailureSaysHowManyWereMade()
    {
        var transport = new ScriptedTransport(
            ScriptedTransport.Refused(), ScriptedTransport.Refused(), ScriptedTransport.Refused());

        Hl7DispatchResult result = await DispatcherFor(transport, new RecordingStore())
            .SendAsync(Profile(), Destination(retries: 2), Subject());

        Assert.Equal(Hl7DispatchOutcome.Failed, result.Outcome);
        Assert.Equal(3, transport.Calls);
        Assert.Contains("3 attempt", result.Message, StringComparison.Ordinal);
        Assert.False(result.SomethingLeft);
    }

    [Fact]
    public async Task AFailureAfterTheBytesLeftIsNeverTriedAgain()
    {
        // The rule the whole design exists to express. The receiver may already hold the
        // document, and a second copy of a report in a patient's record is worse than none.
        var transport = new ScriptedTransport(
            ScriptedTransport.TimedOutAfterSending(), ScriptedTransport.Acknowledged());

        Hl7DispatchResult result = await DispatcherFor(transport, new RecordingStore())
            .SendAsync(Profile(), Destination(retries: 5), Subject());

        Assert.Equal(Hl7DispatchOutcome.SentButUnconfirmed, result.Outcome);
        Assert.Equal(1, transport.Calls);
        Assert.True(result.SomethingLeft);
        Assert.True(result.NeedsAttention);
        Assert.Contains("may already have arrived", result.Message, StringComparison.Ordinal);
        Assert.Contains(result.MessageControlId, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnconfirmedSendBlocksALaterIdenticalOneJustAsFirmly()
    {
        // Recorded as sent precisely so the duplicate guard treats "we do not know" as
        // "possibly yes". Anything weaker and the second attempt sends it again by hand.
        var store = new RecordingStore();

        Hl7DispatchResult first = await DispatcherFor(
                new ScriptedTransport(ScriptedTransport.TimedOutAfterSending()), store)
            .SendAsync(Profile(), Destination(retries: 0), Subject());

        Assert.Equal(Hl7DispatchOutcome.SentButUnconfirmed, first.Outcome);

        var second = new ScriptedTransport(ScriptedTransport.Acknowledged());

        Hl7DispatchResult again = await DispatcherFor(second, store)
            .SendAsync(Profile(), Destination(retries: 0), Subject());

        Assert.Equal(Hl7DispatchOutcome.AlreadyDelivered, again.Outcome);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task TheSameDocumentIsNotSentTwiceAndTheOperatorIsToldWhy()
    {
        var store = new RecordingStore();

        Hl7DispatchResult first = await DispatcherFor(
                new ScriptedTransport(ScriptedTransport.Acknowledged()), store)
            .SendAsync(Profile(), Destination(), Subject());

        var second = new ScriptedTransport(ScriptedTransport.Acknowledged());

        Hl7DispatchResult again = await DispatcherFor(second, store)
            .SendAsync(Profile(), Destination(), Subject());

        Assert.Equal(Hl7DispatchOutcome.AlreadyDelivered, again.Outcome);
        Assert.Equal(0, second.Calls);
        Assert.Contains(first.MessageControlId, again.Message, StringComparison.Ordinal);
        Assert.Equal("Duplicate", store.Written.Last().DeliveryState);
    }

    [Fact]
    public async Task ADifferentDocumentToTheSameDestinationGoesThrough()
    {
        var store = new RecordingStore();

        await DispatcherFor(new ScriptedTransport(ScriptedTransport.Acknowledged()), store)
            .SendAsync(Profile(), Destination(), Subject());

        var second = new ScriptedTransport(ScriptedTransport.Acknowledged());

        Hl7DispatchResult other = await DispatcherFor(second, store)
            .SendAsync(Profile(), Destination(), new MessageSubject().With("patient.id", "7654321"));

        Assert.Equal(Hl7DispatchOutcome.Acknowledged, other.Outcome);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task TheAcknowledgementIsKeptWithTheAttempt()
    {
        var store = new RecordingStore();

        Hl7DispatchResult result = await DispatcherFor(
                new ScriptedTransport(ScriptedTransport.Acknowledged("MSA|AE|1|Unknown patient")),
                store)
            .SendAsync(Profile(), Destination(), Subject());

        Assert.Contains("MSA|AE", result.Acknowledgement, StringComparison.Ordinal);
        Assert.Contains("Unknown patient", store.Written.Last().AcknowledgementText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADestinationThatCannotBeUsedSaysWhy()
    {
        IReadOnlyList<string> problems = new Hl7Destination
        {
            Name = "",
            Host = "",
            Port = 0,
            ConnectTimeoutSeconds = 0
        }.Check();

        Assert.Contains(problems, p => p.Contains("needs a name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("host name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("not a port", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptingAnyCertificateIsAllowedAndSaidOutLoud()
    {
        IReadOnlyList<string> problems = new Hl7Destination
        {
            UseTls = true,
            AcceptAnyTlsCertificate = true
        }.Check();

        Assert.Contains(problems, p =>
            p.Contains("not authenticated", StringComparison.Ordinal));
    }

    [Fact]
    public void ADestinationBecomesTransportOptionsWithoutLosingAnything()
    {
        MllpOptions options = Destination().ToTransportOptions(expectAcknowledgement: false);

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(2575, options.Port);
        Assert.Equal("Report archive", options.Name);
        Assert.False(options.ExpectAcknowledgement);
    }
}
