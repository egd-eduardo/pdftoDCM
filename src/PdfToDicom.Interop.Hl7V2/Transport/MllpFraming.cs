namespace PdfToDicom.Interop.Hl7V2.Transport;

/// <summary>
/// Minimal Lower Layer Protocol framing: the three bytes that tell a receiver where a
/// message begins and ends.
/// </summary>
/// <remarks>
/// <para>
/// A message is wrapped as <c>0x0B</c>, the message, then <c>0x1C 0x0D</c>. That is the
/// whole protocol. It exists because TCP is a byte stream with no notion of a message
/// boundary, and without the wrapper a receiver cannot tell one message from two arriving
/// together, or from half of one.
/// </para>
/// <para>
/// The bytes are deliberately outside the printable range, so they cannot occur inside a
/// message encoded in any of the character sets this library supports. There is therefore
/// no escaping to do, and none is attempted: a payload that somehow contained an end block
/// would be a payload that is already wrong, and quietly rewriting it would hide that.
/// </para>
/// </remarks>
public static class MllpFraming
{
    /// <summary>Begins a frame. Vertical tab in ASCII.</summary>
    public const byte StartBlock = 0x0B;

    /// <summary>Ends a frame, with <see cref="CarriageReturn"/> after it. File separator.</summary>
    public const byte EndBlock = 0x1C;

    /// <summary>The byte that closes a frame after the end block.</summary>
    public const byte CarriageReturn = 0x0D;

    /// <summary>Wraps an encoded message for transmission.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var framed = new byte[payload.Length + 3];

        framed[0] = StartBlock;
        payload.CopyTo(framed.AsSpan(1));
        framed[^2] = EndBlock;
        framed[^1] = CarriageReturn;

        return framed;
    }

    /// <summary>
    /// Recovers the message from a complete frame, reporting rather than throwing when the
    /// bytes are not one.
    /// </summary>
    public static bool TryUnframe(ReadOnlySpan<byte> frame, out byte[] payload)
    {
        payload = [];

        if (frame.Length < 3 ||
            frame[0] != StartBlock ||
            frame[^2] != EndBlock ||
            frame[^1] != CarriageReturn)
        {
            return false;
        }

        payload = frame[1..^2].ToArray();
        return true;
    }

    /// <summary>
    /// Reads exactly one frame from a stream and returns the message inside it.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="maximumPayloadBytes">
    /// How large a message this reader will accept. A peer that never sends an end block
    /// would otherwise be able to make this grow until the process dies, and a receiver
    /// that cannot refuse a message is a receiver that can be stopped by one.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="MllpProtocolException">
    /// The stream ended inside a frame, or the frame exceeded the limit.
    /// </exception>
    public static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        int maximumPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPayloadBytes, 1);

        var payload = new List<byte>(capacity: 1024);
        var single = new byte[1];
        bool started = false;
        bool sawEndBlock = false;

        while (true)
        {
            int read = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new MllpProtocolException(
                    started
                        ? "The connection closed in the middle of a message."
                        : "The connection closed before a message began.");
            }

            byte current = single[0];

            if (!started)
            {
                // Anything before the start block is discarded rather than treated as
                // content. A keep alive newline from a peer is not an error worth failing
                // a transmission over.
                if (current == StartBlock)
                {
                    started = true;
                }

                continue;
            }

            if (sawEndBlock)
            {
                if (current == CarriageReturn)
                {
                    return payload.ToArray();
                }

                throw new MllpProtocolException(
                    $"The end of the message was followed by 0x{current:X2} rather than a " +
                    "carriage return, so the frame is malformed.");
            }

            if (current == EndBlock)
            {
                sawEndBlock = true;
                continue;
            }

            if (payload.Count >= maximumPayloadBytes)
            {
                throw new MllpProtocolException(
                    $"The message exceeded {maximumPayloadBytes} bytes without ending. " +
                    "It was refused rather than read any further.");
            }

            payload.Add(current);
        }
    }
}

/// <summary>Raised when bytes on the wire are not a well formed frame.</summary>
public sealed class MllpProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MllpProtocolException(string message) : base(message)
    {
    }
}
