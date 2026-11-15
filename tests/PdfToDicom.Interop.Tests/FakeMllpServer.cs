using System.Net;
using System.Net.Sockets;
using PdfToDicom.Interop.Hl7V2.Transport;

namespace PdfToDicom.Interop.Tests;

/// <summary>
/// A receiving system that does exactly what a test tells it to, including the unhelpful
/// things a real one does.
/// </summary>
/// <remarks>
/// Proving the transport behaves when a peer answers is easy and proves little. What costs
/// a report is the peer that accepts a connection and then says nothing, or drops halfway,
/// or answers with bytes that are not a frame. Those are the cases this exists for.
/// </remarks>
internal sealed class FakeMllpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<byte[], Stream, CancellationToken, Task> _respond;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private readonly List<byte[]> _received = [];
    private readonly object _gate = new();

    private FakeMllpServer(Func<byte[], Stream, CancellationToken, Task> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>The port the operating system picked, so tests never collide.</summary>
    public int Port { get; }

    /// <summary>Every message the server has read, unframed.</summary>
    public IReadOnlyList<byte[]> Received
    {
        get { lock (_gate) { return _received.ToList(); } }
    }

    /// <summary>The raw bytes of the first connection, before any framing was removed.</summary>
    public byte[] RawFirstConnection { get; private set; } = [];

    /// <summary>A server that replies with the supplied acknowledgement.</summary>
    public static FakeMllpServer ThatAcknowledges(string acknowledgement) =>
        new(async (_, stream, token) =>
        {
            byte[] reply = MllpFraming.Frame(System.Text.Encoding.ASCII.GetBytes(acknowledgement));
            await stream.WriteAsync(reply, token);
            await stream.FlushAsync(token);
        });

    /// <summary>A server that reads the message and then says nothing at all.</summary>
    public static FakeMllpServer ThatStaysSilent() =>
        new(async (_, _, token) => await Task.Delay(Timeout.Infinite, token));

    /// <summary>A server that reads the message and then closes the connection.</summary>
    public static FakeMllpServer ThatHangsUp() => new((_, _, _) => Task.CompletedTask);

    /// <summary>A server that writes arbitrary bytes rather than a frame.</summary>
    public static FakeMllpServer ThatReplies(byte[] rawReply) =>
        new(async (_, stream, token) =>
        {
            await stream.WriteAsync(rawReply, token);
            await stream.FlushAsync(token);
        });

    /// <summary>A server that sends noise before a perfectly good acknowledgement.</summary>
    public static FakeMllpServer ThatChattersFirst(string acknowledgement) =>
        new(async (_, stream, token) =>
        {
            await stream.WriteAsync(new byte[] { 0x0D, 0x0A, 0x20 }, token);
            byte[] reply = MllpFraming.Frame(System.Text.Encoding.ASCII.GetBytes(acknowledgement));
            await stream.WriteAsync(reply, token);
            await stream.FlushAsync(token);
        });

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                await using NetworkStream stream = client.GetStream();

                byte[] message = await MllpFraming.ReadFrameAsync(
                    stream, maximumPayloadBytes: 16 * 1024 * 1024, _stopping.Token);

                lock (_gate)
                {
                    _received.Add(message);
                }

                await _respond(message, stream, _stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception)
        {
            // A test that closes a connection early makes the read throw here. That is the
            // behaviour under test, not a failure of the harness.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();

        try
        {
            await _accepting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // The accept loop is already gone.
        }

        _stopping.Dispose();
    }
}
