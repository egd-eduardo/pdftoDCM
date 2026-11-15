namespace PdfToDicom.Interop.Hl7V2.Transport;

/// <summary>
/// How far a transmission got. The three states are kept apart on purpose.
/// </summary>
/// <remarks>
/// A successful write to a socket is not acceptance by the receiving application, and a
/// product that shows one tick for both is lying to the person who has to decide whether a
/// report reached the record. The supplied conformance statement makes this concrete: it
/// declares NE in both acknowledgement fields and returns nothing at all, so
/// <see cref="Sent"/> is the furthest a transmission to it can ever get, and the interface
/// must say so rather than imply more.
/// </remarks>
public enum Hl7DeliveryState
{
    /// <summary>Nothing was transmitted. The reason is in the result's message.</summary>
    Failed = 0,

    /// <summary>
    /// The bytes were written and the peer accepted them at the transport level. Whether
    /// any application read them is unknown and must not be guessed at.
    /// </summary>
    Sent = 1,

    /// <summary>
    /// A reply was received and is carried in the result. What it says is the
    /// acknowledgement layer's business, not the transport's.
    /// </summary>
    Acknowledged = 2
}

/// <summary>The outcome of one attempt to deliver one message.</summary>
/// <param name="State">How far it got.</param>
/// <param name="Message">An operator facing description, in the same spirit as the DICOM side.</param>
/// <param name="Acknowledgement">The reply, when one was received and expected.</param>
/// <param name="Elapsed">How long the attempt took, connection included.</param>
/// <param name="Error">The underlying failure, when there was one.</param>
public sealed record Hl7SendResult(
    Hl7DeliveryState State,
    string Message,
    byte[]? Acknowledgement = null,
    TimeSpan Elapsed = default,
    Exception? Error = null)
{
    /// <summary>True when the bytes reached the peer, acknowledged or not.</summary>
    public bool WasSent => State is Hl7DeliveryState.Sent or Hl7DeliveryState.Acknowledged;
}

/// <summary>Stages of a transmission, reported in order.</summary>
/// <remarks>
/// Deliberately the same shape as the DICOM side's progress reporting, so that the
/// interface which already knows how to display one does not have to learn a second.
/// </remarks>
public enum Hl7TransmissionStage
{
    /// <summary>Opening the connection.</summary>
    Connecting = 0,

    /// <summary>Writing the framed message.</summary>
    Sending = 1,

    /// <summary>Waiting for a reply, where the profile expects one.</summary>
    AwaitingAcknowledgement = 2,

    /// <summary>The attempt finished, successfully or otherwise.</summary>
    Completed = 3
}

/// <summary>Progress information for one attempt.</summary>
/// <param name="DestinationName">Which destination is being contacted.</param>
/// <param name="Stage">What is happening now.</param>
/// <param name="Message">An operator facing description of this stage.</param>
public sealed record Hl7TransmissionProgress(
    string DestinationName,
    Hl7TransmissionStage Stage,
    string Message);

/// <summary>Delivers one encoded message to one destination.</summary>
/// <remarks>
/// The interface takes bytes rather than a message, because by this point the character
/// set has already been applied and changing anything would change what was validated.
/// Retries, duplicate protection and routing live above this: an implementation makes one
/// attempt and reports what happened.
/// </remarks>
public interface IHl7Transport
{
    /// <summary>A name for the destination, used in progress and in the transaction record.</summary>
    string DestinationName { get; }

    /// <summary>Makes one attempt to deliver one message.</summary>
    Task<Hl7SendResult> SendAsync(
        ReadOnlyMemory<byte> encodedMessage,
        IProgress<Hl7TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
