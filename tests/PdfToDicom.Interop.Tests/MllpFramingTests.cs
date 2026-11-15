using PdfToDicom.Interop.Hl7V2.Transport;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class MllpFramingTests
{
    [Fact]
    public void AFrameIsTheMessageBetweenThreeAgreedBytes()
    {
        byte[] framed = MllpFraming.Frame("MSH|^~\\&"u8);

        Assert.Equal(MllpFraming.StartBlock, framed[0]);
        Assert.Equal(MllpFraming.EndBlock, framed[^2]);
        Assert.Equal(MllpFraming.CarriageReturn, framed[^1]);
        Assert.Equal("MSH|^~\\&", System.Text.Encoding.ASCII.GetString(framed[1..^2]));
    }

    [Fact]
    public void AnEmptyPayloadStillProducesAWellFormedFrame()
    {
        byte[] framed = MllpFraming.Frame([]);

        Assert.Equal(3, framed.Length);
        Assert.True(MllpFraming.TryUnframe(framed, out byte[] payload));
        Assert.Empty(payload);
    }

    [Fact]
    public void AFrameUnwrapsToExactlyWhatWentIn()
    {
        byte[] payload = [0x01, 0x7F, 0x80, 0xFF];

        Assert.True(MllpFraming.TryUnframe(MllpFraming.Frame(payload), out byte[] recovered));
        Assert.Equal(payload, recovered);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x0B })]
    [InlineData(new byte[] { 0x0B, 0x41, 0x1C })]
    [InlineData(new byte[] { 0x41, 0x42, 0x1C, 0x0D })]
    [InlineData(new byte[] { 0x0B, 0x41, 0x0D, 0x0D })]
    public void BytesThatAreNotAFrameAreReportedRatherThanThrown(byte[] bytes)
    {
        Assert.False(MllpFraming.TryUnframe(bytes, out _));
    }

    [Fact]
    public async Task AFrameIsReadBackOffAStream()
    {
        using var stream = new MemoryStream(MllpFraming.Frame("ACK"u8));

        byte[] payload = await MllpFraming.ReadFrameAsync(stream, 1024);

        Assert.Equal("ACK", System.Text.Encoding.ASCII.GetString(payload));
    }

    [Fact]
    public async Task NoiseBeforeTheStartBlockIsDiscardedRatherThanRead()
    {
        // A keep alive newline from a peer is not worth failing a transmission over.
        byte[] noisy = [0x0D, 0x0A, .. MllpFraming.Frame("ACK"u8)];
        using var stream = new MemoryStream(noisy);

        Assert.Equal("ACK", System.Text.Encoding.ASCII.GetString(
            await MllpFraming.ReadFrameAsync(stream, 1024)));
    }

    [Fact]
    public async Task AStreamThatEndsInsideAFrameIsAnError()
    {
        using var stream = new MemoryStream([MllpFraming.StartBlock, 0x41, 0x42]);

        MllpProtocolException error = await Assert.ThrowsAsync<MllpProtocolException>(
            () => MllpFraming.ReadFrameAsync(stream, 1024));

        Assert.Contains("middle of a message", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamThatEndsBeforeAFrameBeginsSaysSo()
    {
        using var stream = new MemoryStream([0x0D, 0x0A]);

        MllpProtocolException error = await Assert.ThrowsAsync<MllpProtocolException>(
            () => MllpFraming.ReadFrameAsync(stream, 1024));

        Assert.Contains("before a message began", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndBlockNotFollowedByACarriageReturnIsMalformed()
    {
        using var stream = new MemoryStream(
            [MllpFraming.StartBlock, 0x41, MllpFraming.EndBlock, 0x41]);

        await Assert.ThrowsAsync<MllpProtocolException>(
            () => MllpFraming.ReadFrameAsync(stream, 1024));
    }

    [Fact]
    public async Task AMessageThatNeverEndsIsRefusedAtTheConfiguredLimit()
    {
        // A receiver that cannot refuse a message is a receiver that can be stopped by one.
        byte[] endless = [MllpFraming.StartBlock, .. new byte[5000]];
        using var stream = new MemoryStream(endless);

        MllpProtocolException error = await Assert.ThrowsAsync<MllpProtocolException>(
            () => MllpFraming.ReadFrameAsync(stream, maximumPayloadBytes: 100));

        Assert.Contains("exceeded 100 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingCanBeCancelled()
    {
        using var stream = new MemoryStream(MllpFraming.Frame("ACK"u8));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MllpFraming.ReadFrameAsync(stream, 1024, cancelled.Token));
    }

    [Fact]
    public void AFrameSurvivesAPayloadLargerThanAnyBuffer()
    {
        // An encapsulated document in OBX-5 is megabytes of base64, not a few hundred bytes.
        byte[] large = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(large);

        Assert.True(MllpFraming.TryUnframe(MllpFraming.Frame(large), out byte[] recovered));
        Assert.Equal(large, recovered);
    }
}
