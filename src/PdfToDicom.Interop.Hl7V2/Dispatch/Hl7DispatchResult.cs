using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Hl7V2.Profiles;

namespace PdfToDicom.Interop.Hl7V2.Dispatch;

/// <summary>What became of one message to one destination.</summary>
public enum Hl7DispatchOutcome
{
    /// <summary>Nothing was sent, and nothing reached the wire.</summary>
    Failed = 0,

    /// <summary>The message did not pass its profile, so it was never built into bytes.</summary>
    Refused = 1,

    /// <summary>
    /// This document had already reached this destination, so it was not sent again.
    /// </summary>
    AlreadyDelivered = 2,

    /// <summary>The bytes left. Whether an application read them is not known.</summary>
    Sent = 3,

    /// <summary>The receiver replied.</summary>
    Acknowledged = 4,

    /// <summary>
    /// The bytes left and then something went wrong, so nobody knows what became of them.
    /// </summary>
    /// <remarks>
    /// Its own outcome rather than a kind of failure, because it is the one case where
    /// neither "it arrived" nor "it did not" can honestly be said, and the one case an
    /// automatic retry must not touch.
    /// </remarks>
    SentButUnconfirmed = 5
}

/// <summary>The result of trying to deliver one message to one destination.</summary>
/// <param name="Outcome">What became of it.</param>
/// <param name="Message">What an operator is told.</param>
/// <param name="Built">The message, when one was built, for the preview and the log.</param>
/// <param name="Issues">Anything the profile objected to.</param>
/// <param name="Attempts">How many times it was tried.</param>
/// <param name="MessageControlId">The identifier written into MSH-10, where there is one.</param>
/// <param name="Acknowledgement">The reply, where one came back.</param>
public sealed record Hl7DispatchResult(
    Hl7DispatchOutcome Outcome,
    string Message,
    Hl7Message? Built = null,
    IReadOnlyList<Hl7MessageIssue>? Issues = null,
    int Attempts = 0,
    string MessageControlId = "",
    string Acknowledgement = "")
{
    /// <summary>Everything the profile objected to.</summary>
    public IReadOnlyList<Hl7MessageIssue> Issues { get; init; } = Issues ?? [];

    /// <summary>
    /// Whether anything at all reached the receiving system, confirmed or not.
    /// </summary>
    /// <remarks>
    /// Deliberately true for an unconfirmed send. Anything deciding whether to try again,
    /// or whether a document may already exist at the far end, has to treat "we do not
    /// know" as "possibly yes".
    /// </remarks>
    public bool SomethingLeft =>
        Outcome is Hl7DispatchOutcome.Sent
            or Hl7DispatchOutcome.Acknowledged
            or Hl7DispatchOutcome.SentButUnconfirmed;

    /// <summary>Whether a person needs to look at this.</summary>
    public bool NeedsAttention =>
        Outcome is Hl7DispatchOutcome.Failed
            or Hl7DispatchOutcome.Refused
            or Hl7DispatchOutcome.SentButUnconfirmed;
}
