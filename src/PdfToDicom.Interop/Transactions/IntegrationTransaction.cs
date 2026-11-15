namespace PdfToDicom.Interop.Transactions;

/// <summary>
/// One attempt to deliver one message to one destination, as it is written down.
/// </summary>
/// <remarks>
/// <para>
/// Every field is a string or a number rather than an enumeration, and that is deliberate.
/// A file written by one version of this software is read by a later one, and a stored
/// value that no longer corresponds to any member must not make the record unreadable. A
/// record of a transmission that cannot be read is worse than no record, because somebody
/// will be looking at it precisely when something has gone wrong.
/// </para>
/// <para>
/// The message itself is not here by default. See <see cref="MessageBody"/>.
/// </para>
/// </remarks>
public sealed record IntegrationTransaction
{
    /// <summary>This software's own identifier for the attempt.</summary>
    public required string TransactionId { get; init; }

    /// <summary>The identifier carried in the message, which a receiver echoes back.</summary>
    public required string MessageControlId { get; init; }

    /// <summary>The destination, by the name an administrator gave it.</summary>
    public required string DestinationName { get; init; }

    /// <summary>The message type and trigger event, for example "ORU^R01".</summary>
    public required string MessageType { get; init; }

    /// <summary>Which profile built it, and which revision of that profile.</summary>
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>The profile revision, so a message can be explained after the profile changed.</summary>
    public int ProfileRevision { get; init; }

    /// <summary>When the attempt was made, in universal time.</summary>
    public required DateTimeOffset AttemptedUtc { get; init; }

    /// <summary>Which attempt this was, counting from one.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>
    /// How far it got: "Failed", "Sent" or "Acknowledged". Never collapsed into a single
    /// success flag, because a write to a socket is not acceptance by an application.
    /// </summary>
    public required string DeliveryState { get; init; }

    /// <summary>The acknowledgement code, where one was received: AA, AE, AR, CA, CE or CR.</summary>
    public string AcknowledgementCode { get; init; } = string.Empty;

    /// <summary>What the acknowledgement said, where it said anything.</summary>
    public string AcknowledgementText { get; init; } = string.Empty;

    /// <summary>What went wrong, where something did.</summary>
    public string ErrorDetail { get; init; } = string.Empty;

    /// <summary>
    /// A hash of exactly the bytes that were transmitted.
    /// </summary>
    /// <remarks>
    /// This is what makes duplicate protection possible without keeping the message. Two
    /// attempts that would produce the same bytes for the same destination are the same
    /// document, whatever identifier was generated for them.
    /// </remarks>
    public required string ContentHash { get; init; }

    /// <summary>
    /// The DICOM object this message accompanies, where there is one.
    /// </summary>
    /// <remarks>
    /// The one field that ties the two halves of a conversion together. Without it, an
    /// archive holding a document and a record system that never heard of it cannot be
    /// reconciled from this end.
    /// </remarks>
    public string RelatedSopInstanceUid { get; init; } = string.Empty;

    /// <summary>How long the attempt took.</summary>
    public double ElapsedMilliseconds { get; init; }

    /// <summary>
    /// The message as transmitted, kept only when a site has asked for it.
    /// </summary>
    /// <remarks>
    /// Empty by default, and deliberately so. The body of an ORU is the report: a patient's
    /// name, their identifier and the clinical text, sitting in a file that somebody may
    /// later attach to a support request. Keeping it helps when an interface is being
    /// commissioned and is a disclosure waiting to happen afterwards, so it is something a
    /// site turns on, with a retention period, rather than something that happens.
    /// </remarks>
    public string MessageBody { get; init; } = string.Empty;
}
