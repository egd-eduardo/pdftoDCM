using System.Diagnostics;
using System.Net.Sockets;

namespace PdfToDicom.Interop.Hl7V2.Transport;

/// <summary>
/// Delivers a message over MLLP on TCP, which is how HL7 version 2 travels in practice.
/// </summary>
/// <remarks>
/// <para>
/// One attempt per call, and no retries. Retrying is a policy question that needs to know
/// whether the message is safe to send twice, and the layer that knows that is the one
/// holding the transaction record, not this one.
/// </para>
/// <para>
/// A fresh connection per message rather than a pooled one. A pool is faster and is what a
/// high volume interface engine does; here a message is a report somebody just produced,
/// the rate is a few an hour, and a connection held open across idle periods is a
/// connection that fails on the attempt that matters rather than on a harmless one. The
/// cost of being wrong about that is a lost report, so the simple thing wins until
/// somebody measures a reason to change it.
/// </para>
/// </remarks>
public sealed class MllpTransport : IHl7Transport
{
    private readonly MllpOptions _options;

    /// <summary>Creates a transport for one destination.</summary>
    public MllpTransport(MllpOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string DestinationName => _options.Name;

    /// <inheritdoc />
    public async Task<Hl7SendResult> SendAsync(
        ReadOnlyMemory<byte> encodedMessage,
        IProgress<Hl7TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var written = new Written();

        try
        {
            return await AttemptAsync(encodedMessage, progress, clock, written, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The operator cancelled. Not a failure of the destination, and reported as
            // what it is so that nobody goes looking for a network fault.
            return Failed(clock, "The transmission was cancelled.", null, written);
        }
        catch (OperationCanceledException)
        {
            // A timeout, which arrives here as a cancellation because that is how the
            // socket API expresses one.
            return Failed(
                clock,
                $"{_options.Host}:{_options.Port} did not respond within the configured " +
                "timeout. Nothing is known about whether the message was processed.",
                null, written);
        }
        catch (SocketException ex)
        {
            return Failed(
                clock,
                $"{_options.Host}:{_options.Port} could not be reached: {ex.Message}",
                ex, written);
        }
        catch (MllpProtocolException ex)
        {
            return Failed(clock, $"The reply was not a valid HL7 frame: {ex.Message}", ex, written);
        }
        catch (IOException ex)
        {
            return Failed(
                clock,
                $"The connection to {_options.Host}:{_options.Port} failed: {ex.Message}",
                ex, written);
        }
    }

    private async Task<Hl7SendResult> AttemptAsync(
        ReadOnlyMemory<byte> encodedMessage,
        IProgress<Hl7TransmissionProgress>? progress,
        Stopwatch clock,
        Written written,
        CancellationToken cancellationToken)
    {
        Report(progress, Hl7TransmissionStage.Connecting,
            $"Connecting to {_options.Host}:{_options.Port}");

        using var client = new TcpClient();

        // Nagle's algorithm delays a small write waiting for company. A message is written
        // in one go and then waited on, so the delay is pure latency with nothing to gain.
        client.NoDelay = true;

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

            await client.ConnectAsync(_options.Host, _options.Port, connectTimeout.Token)
                .ConfigureAwait(false);
        }

        await using NetworkStream network = client.GetStream();

        // Buffered because the frame reader takes one byte at a time, which is the clearest
        // way to express the protocol and the slowest way to execute it. The buffer makes
        // the two compatible.
        await using var stream = new BufferedStream(network, bufferSize: 8192);

        Report(progress, Hl7TransmissionStage.Sending,
            $"Sending {encodedMessage.Length} bytes");

        byte[] frame = MllpFraming.Frame(encodedMessage.Span);

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // From here on, a failure means the bytes left and their fate is unknown. Anything
        // deciding whether to try again needs that, and only this line knows it.
        written.Yes = true;

        if (!_options.ExpectAcknowledgement)
        {
            // Declared as answering nothing, so the transmission is complete at the point
            // the bytes left. Saying "sent" and stopping there is the honest report.
            Report(progress, Hl7TransmissionStage.Completed, "Sent. This destination does not acknowledge.");

            return new Hl7SendResult(
                Hl7DeliveryState.Sent,
                "The message was sent. This destination is configured not to acknowledge, " +
                "so whether it was processed is not known from here.",
                Acknowledgement: null,
                Elapsed: clock.Elapsed,
                BytesWereWritten: true);
        }

        Report(progress, Hl7TransmissionStage.AwaitingAcknowledgement, "Waiting for the acknowledgement");

        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ReceiveTimeoutSeconds));

        byte[] acknowledgement = await MllpFraming
            .ReadFrameAsync(stream, _options.MaximumAcknowledgementBytes, receiveTimeout.Token)
            .ConfigureAwait(false);

        Report(progress, Hl7TransmissionStage.Completed, "Acknowledgement received");

        return new Hl7SendResult(
            Hl7DeliveryState.Acknowledged,
            $"The message was sent and {_options.Name} replied.",
            acknowledgement,
            clock.Elapsed,
            BytesWereWritten: true);
    }

    private static Hl7SendResult Failed(
        Stopwatch clock, string message, Exception? error, Written written) =>
        new(Hl7DeliveryState.Failed, message, Acknowledgement: null, clock.Elapsed, error,
            written.Yes);

    /// <summary>
    /// Carries one fact out of an async method, which cannot have an out parameter.
    /// </summary>
    private sealed class Written
    {
        public bool Yes { get; set; }
    }

    private void Report(
        IProgress<Hl7TransmissionProgress>? progress, Hl7TransmissionStage stage, string message) =>
        progress?.Report(new Hl7TransmissionProgress(_options.Name, stage, message));
}
