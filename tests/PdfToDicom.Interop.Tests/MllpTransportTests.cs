using System.Text;
using PdfToDicom.Interop.Hl7V2.Transport;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class MllpTransportTests
{
    private const string Message = "MSH|^~\\&|PDFTODICOM||||||ORU^R01|1|P|2.4";
    private const string Acknowledgement = "MSH|^~\\&|RECEIVER|||||ACK|1|P|2.4\rMSA|AA|1";

    private static byte[] Encoded => Encoding.ASCII.GetBytes(Message);

    private static MllpOptions OptionsFor(int port, bool expectAck = true) =>
        new("127.0.0.1", port)
        {
            Name = "Test destination",
            ConnectTimeoutSeconds = 5,
            ReceiveTimeoutSeconds = 2,
            ExpectAcknowledgement = expectAck
        };

    [Fact]
    public async Task AMessageArrivesIntactAndItsReplyComesBack()
    {
        await using FakeMllpServer server = FakeMllpServer.ThatAcknowledges(Acknowledgement);
        var transport = new MllpTransport(OptionsFor(server.Port));

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Acknowledged, result.State);
        Assert.True(result.WasSent);
        Assert.NotNull(result.Acknowledgement);
        Assert.Equal(Acknowledgement, Encoding.ASCII.GetString(result.Acknowledgement!));
        Assert.Equal(Message, Encoding.ASCII.GetString(Assert.Single(server.Received)));
    }

    [Fact]
    public async Task ADestinationThatDoesNotAcknowledgeReportsSentAndNothingMore()
    {
        // The supplied statement declares NE in both acknowledgement fields. Waiting for a
        // reply that will never come would turn every success into a timeout, and claiming
        // more than "sent" would be a lie about a clinical document.
        await using FakeMllpServer server = FakeMllpServer.ThatStaysSilent();
        var transport = new MllpTransport(OptionsFor(server.Port, expectAck: false));

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Sent, result.State);
        Assert.True(result.WasSent);
        Assert.Null(result.Acknowledgement);
        Assert.Contains("not known from here", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASilentDestinationThatShouldAnswerTimesOutAndSaysWhatIsUnknown()
    {
        await using FakeMllpServer server = FakeMllpServer.ThatStaysSilent();
        var transport = new MllpTransport(OptionsFor(server.Port));

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Failed, result.State);
        Assert.False(result.WasSent);
        Assert.Contains("did not respond", result.Message, StringComparison.Ordinal);

        // The message did leave, so the operator must not be told it certainly did not
        // arrive. Saying nothing is known is the only honest answer.
        Assert.Contains("Nothing is known", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADestinationThatIsNotListeningFailsWithoutThrowing()
    {
        // A port nothing listens on: the connection is refused immediately.
        var transport = new MllpTransport(OptionsFor(port: 9));

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Failed, result.State);
        Assert.NotNull(result.Error);
        Assert.Contains("could not be reached", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADestinationThatHangsUpAfterReadingIsAFailureNotASuccess()
    {
        await using FakeMllpServer server = FakeMllpServer.ThatHangsUp();
        var transport = new MllpTransport(OptionsFor(server.Port));

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Failed, result.State);
        Assert.False(result.WasSent);
    }

    [Fact]
    public async Task AReplyThatIsNotAFrameIsRefusedRatherThanInterpreted()
    {
        await using FakeMllpServer server =
            FakeMllpServer.ThatReplies(Encoding.ASCII.GetBytes("not a frame at all"));
        var transport = new MllpTransport(OptionsFor(server.Port));

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Failed, result.State);
    }

    [Fact]
    public async Task NoiseBeforeTheReplyDoesNotSpoilIt()
    {
        await using FakeMllpServer server = FakeMllpServer.ThatChattersFirst(Acknowledgement);
        var transport = new MllpTransport(OptionsFor(server.Port));

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Acknowledged, result.State);
        Assert.Equal(Acknowledgement, Encoding.ASCII.GetString(result.Acknowledgement!));
    }

    [Fact]
    public async Task AnOverlongReplyIsRefusedAtTheConfiguredLimit()
    {
        byte[] huge = MllpFraming.Frame(new byte[4096]);
        await using FakeMllpServer server = FakeMllpServer.ThatReplies(huge);

        var transport = new MllpTransport(OptionsFor(server.Port) with
        {
            MaximumAcknowledgementBytes = 128
        });

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Failed, result.State);
        Assert.Contains("not a valid HL7 frame", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingIsReportedAsCancellationAndNotAsANetworkFault()
    {
        await using FakeMllpServer server = FakeMllpServer.ThatStaysSilent();
        var transport = new MllpTransport(OptionsFor(server.Port));

        using var cancelling = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Hl7SendResult result = await transport.SendAsync(Encoded, cancellationToken: cancelling.Token);

        Assert.Equal(Hl7DeliveryState.Failed, result.State);
        Assert.Contains("cancelled", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressIsReportedInOrderSoTheInterfaceCanShowWhereItIs()
    {
        await using FakeMllpServer server = FakeMllpServer.ThatAcknowledges(Acknowledgement);
        var transport = new MllpTransport(OptionsFor(server.Port));

        // Deliberately not Progress<T>. That one posts its callbacks to a synchronisation
        // context, so a test using it can only assert after waiting, and a test that waits
        // is a test that passes alone and fails under load. This one asked for exactly that
        // lesson. Recording inline tests the contract that matters, which is the order the
        // transport calls Report in.
        var stages = new List<Hl7TransmissionStage>();
        var progress = new RecordingProgress(p => stages.Add(p.Stage));

        await transport.SendAsync(Encoded, progress);

        Assert.Equal(
            [
                Hl7TransmissionStage.Connecting,
                Hl7TransmissionStage.Sending,
                Hl7TransmissionStage.AwaitingAcknowledgement,
                Hl7TransmissionStage.Completed
            ],
            stages);
    }

    [Fact]
    public async Task AMessageOfSeveralMegabytesSurvivesTheJourney()
    {
        // An encapsulated PDF in OBX-5 is not a few hundred bytes, and a transport that
        // only works for small messages would fail on exactly the ones that matter.
        byte[] large = Encoding.ASCII.GetBytes(Message + "\rOBX|1|ED|" + new string('A', 2_000_000));

        await using FakeMllpServer server = FakeMllpServer.ThatAcknowledges(Acknowledgement);
        var transport = new MllpTransport(OptionsFor(server.Port) with { ReceiveTimeoutSeconds = 20 });

        Hl7SendResult result = await transport.SendAsync(large);

        Assert.Equal(Hl7DeliveryState.Acknowledged, result.State);
        Assert.Equal(large, Assert.Single(server.Received));
    }

    [Fact]
    public async Task TheElapsedTimeIsRecordedForTheTransactionLog()
    {
        await using FakeMllpServer server = FakeMllpServer.ThatAcknowledges(Acknowledgement);
        var transport = new MllpTransport(OptionsFor(server.Port));

        Hl7SendResult result = await transport.SendAsync(Encoded);

        Assert.True(result.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task ARefusedConnectionSaysNothingLeftSoARetryIsSafe()
    {
        Hl7SendResult result = await new MllpTransport(OptionsFor(port: 9)).SendAsync(Encoded);

        Assert.False(result.BytesWereWritten);
        Assert.False(result.RetryCouldDuplicate);
    }

    [Fact]
    public async Task ATimeoutAfterSendingSaysTheBytesLeftSoARetryIsNot()
    {
        // The pair that the dispatcher's no retry rule rests on. If the transport ever
        // stopped distinguishing these, an automatic retry would quietly duplicate a report.
        await using FakeMllpServer server = FakeMllpServer.ThatStaysSilent();

        Hl7SendResult result = await new MllpTransport(OptionsFor(server.Port)).SendAsync(Encoded);

        Assert.Equal(Hl7DeliveryState.Failed, result.State);
        Assert.True(result.BytesWereWritten);
        Assert.True(result.RetryCouldDuplicate);
    }

    [Fact]
    public async Task ASuccessfulSendAlsoRecordsThatTheBytesLeft()
    {
        await using FakeMllpServer server = FakeMllpServer.ThatAcknowledges(Acknowledgement);

        Hl7SendResult result = await new MllpTransport(OptionsFor(server.Port)).SendAsync(Encoded);

        Assert.True(result.BytesWereWritten);
        Assert.False(result.RetryCouldDuplicate);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("host", 0)]
    [InlineData("host", 70000)]
    public void OptionsThatCannotDescribeADestinationAreRefused(string host, int port)
    {
        Assert.ThrowsAny<ArgumentException>(() => new MllpOptions(host, port));
    }
}

/// <summary>
/// An <see cref="IProgress{T}"/> that reports on the calling thread, so a test can assert
/// without waiting for anything.
/// </summary>
internal sealed class RecordingProgress(Action<Hl7TransmissionProgress> onReport)
    : IProgress<Hl7TransmissionProgress>
{
    public void Report(Hl7TransmissionProgress value) => onReport(value);
}
