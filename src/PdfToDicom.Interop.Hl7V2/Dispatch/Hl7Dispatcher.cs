using PdfToDicom.Interop.Hl7V2.Encoding;
using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Hl7V2.Profiles;
using PdfToDicom.Interop.Hl7V2.Transport;
using PdfToDicom.Interop.Messaging;
using PdfToDicom.Interop.Transactions;

namespace PdfToDicom.Interop.Hl7V2.Dispatch;

/// <summary>
/// Builds a message, checks it, refuses to send it twice, sends it, and writes down what
/// happened.
/// </summary>
/// <remarks>
/// The one place where the pieces meet. Everything below it does one thing and knows
/// nothing of the others: the codec does not know what a destination is, the transport does
/// not know what a profile is, the store does not know what HL7 is. The ordering decisions
/// that matter clinically are all here, which is where they can be read together.
/// </remarks>
public sealed class Hl7Dispatcher
{
    private readonly ITransactionStore _transactions;
    private readonly Func<Hl7Destination, bool, IHl7Transport> _transports;
    private readonly Hl7MessageBuilder _builder;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates a dispatcher.</summary>
    /// <param name="transactions">Where attempts are written down.</param>
    /// <param name="transports">
    /// Produces a transport for a destination and whether an acknowledgement is expected.
    /// Injected rather than constructed here so that a test can stand in for a network, and
    /// so that a second transport can be added without touching this type.
    /// </param>
    /// <param name="builder">The message builder, or a default one.</param>
    /// <param name="clock">The clock, for the transaction record.</param>
    /// <param name="delay">How to wait between attempts. A test supplies one that does not.</param>
    public Hl7Dispatcher(
        ITransactionStore transactions,
        Func<Hl7Destination, bool, IHl7Transport> transports,
        Hl7MessageBuilder? builder = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        _builder = builder ?? new Hl7MessageBuilder();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    /// <summary>Delivers one subject to one destination under one profile.</summary>
    public async Task<Hl7DispatchResult> SendAsync(
        Hl7Profile profile,
        Hl7Destination destination,
        MessageSubject subject,
        string relatedSopInstanceUid = "",
        IProgress<Hl7TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(subject);

        Hl7BuildResult built = _builder.Build(profile, WithDestination(subject, destination));

        if (!built.IsSendable)
        {
            // Refused before anything is encoded. Recorded all the same: an audit trail that
            // omits refusals hides how often an interface is misconfigured, which is exactly
            // what somebody commissioning it needs to see.
            await RecordAsync(
                    profile, destination, built.Message, Hl7DispatchOutcome.Refused, attempt: 0,
                    contentHash: string.Empty, relatedSopInstanceUid,
                    detail: string.Join("; ", built.Errors.Select(e => $"{e.Location}: {e.Message}")),
                    elapsed: TimeSpan.Zero, acknowledgement: string.Empty, cancellationToken)
                .ConfigureAwait(false);

            return new Hl7DispatchResult(
                Hl7DispatchOutcome.Refused,
                "The message did not pass the profile for this destination, so nothing was sent.",
                built.Message, built.Issues, Attempts: 0,
                MessageControlId: built.Message?["MSH-10"] ?? string.Empty);
        }

        Hl7Message message = built.Message!;
        string controlId = message["MSH-10"];

        byte[] encoded;

        try
        {
            encoded = Hl7CharacterSet.Encode(
                message.ToEr7(),
                Hl7CharacterSet.Resolve(profile.Encoding.CharacterSet),
                UnrepresentableCharacterPolicy.Refuse);
        }
        catch (Hl7CharacterSetException ex)
        {
            // The builder checks each mapped value, so reaching here means something not
            // mapped carried it. Refusing rather than sending an altered message is the
            // same answer in either case.
            return new Hl7DispatchResult(
                Hl7DispatchOutcome.Refused, ex.Message, message, built.Issues,
                MessageControlId: controlId);
        }

        string hash = ContentHash.Of(encoded);

        IntegrationTransaction? earlier = await _transactions
            .FindEarlierDeliveryAsync(hash, destination.Name, cancellationToken)
            .ConfigureAwait(false);

        if (earlier is not null)
        {
            await RecordAsync(
                    profile, destination, message, Hl7DispatchOutcome.AlreadyDelivered, attempt: 0,
                    hash, relatedSopInstanceUid,
                    detail: $"Already delivered as {earlier.MessageControlId} on " +
                            $"{earlier.AttemptedUtc:yyyy-MM-dd HH:mm} UTC.",
                    elapsed: TimeSpan.Zero, acknowledgement: string.Empty, cancellationToken)
                .ConfigureAwait(false);

            return new Hl7DispatchResult(
                Hl7DispatchOutcome.AlreadyDelivered,
                $"This document already reached {destination.Name} as " +
                $"{earlier.MessageControlId}, so it was not sent again.",
                message, built.Issues, Attempts: 0, MessageControlId: controlId);
        }

        return await DeliverAsync(
                profile, destination, message, encoded, hash, controlId, built.Issues,
                relatedSopInstanceUid, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Hl7DispatchResult> DeliverAsync(
        Hl7Profile profile,
        Hl7Destination destination,
        Hl7Message message,
        byte[] encoded,
        string hash,
        string controlId,
        IReadOnlyList<Hl7MessageIssue> issues,
        string relatedSopInstanceUid,
        IProgress<Hl7TransmissionProgress>? progress,
        CancellationToken cancellationToken)
    {
        IHl7Transport transport = _transports(destination, !profile.Acknowledgement.ExpectsNone);

        int attempts = 0;
        Hl7SendResult? last = null;

        for (int attempt = 1; attempt <= destination.RetryCount + 1; attempt++)
        {
            attempts = attempt;

            last = await transport.SendAsync(encoded, progress, cancellationToken)
                .ConfigureAwait(false);

            Hl7DispatchOutcome outcome = OutcomeOf(last);

            await RecordAsync(
                    profile, destination, message, outcome, attempt, hash, relatedSopInstanceUid,
                    detail: last.Message, last.Elapsed,
                    acknowledgement: AcknowledgementText(last), cancellationToken)
                .ConfigureAwait(false);

            if (last.WasSent)
            {
                return Finish(outcome, last.Message, message, issues, attempts, controlId, last);
            }

            // The rule this whole design exists to express. A failure after the bytes left
            // may mean the receiver has the document already, so trying again risks a second
            // copy of a report in a patient's record. Nobody can decide that from here.
            if (last.RetryCouldDuplicate)
            {
                return Finish(
                    Hl7DispatchOutcome.SentButUnconfirmed,
                    $"{last.Message} It was not sent again, because it may already have " +
                    "arrived and a second copy would be worse than none. Check " +
                    $"{destination.Name} for {controlId} before resending.",
                    message, issues, attempts, controlId, last);
            }

            if (attempt <= destination.RetryCount)
            {
                await _delay(TimeSpan.FromSeconds(destination.RetryDelaySeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return Finish(
            Hl7DispatchOutcome.Failed,
            $"{last?.Message} Nothing reached {destination.Name} after {attempts} attempt(s).",
            message, issues, attempts, controlId, last);
    }

    private static Hl7DispatchResult Finish(
        Hl7DispatchOutcome outcome,
        string text,
        Hl7Message message,
        IReadOnlyList<Hl7MessageIssue> issues,
        int attempts,
        string controlId,
        Hl7SendResult? last) =>
        new(outcome, text, message, issues, attempts, controlId, AcknowledgementText(last));

    private static Hl7DispatchOutcome OutcomeOf(Hl7SendResult result) => result.State switch
    {
        Hl7DeliveryState.Acknowledged => Hl7DispatchOutcome.Acknowledged,
        Hl7DeliveryState.Sent => Hl7DispatchOutcome.Sent,
        _ => result.RetryCouldDuplicate
            ? Hl7DispatchOutcome.SentButUnconfirmed
            : Hl7DispatchOutcome.Failed
    };

    private static string AcknowledgementText(Hl7SendResult? result) =>
        result?.Acknowledgement is { Length: > 0 } bytes
            ? System.Text.Encoding.ASCII.GetString(bytes)
            : string.Empty;

    /// <summary>
    /// Adds the destination's own identifiers to the subject, so a profile can map them
    /// without the destination having to be known to the mapping language.
    /// </summary>
    private static MessageSubject WithDestination(MessageSubject subject, Hl7Destination destination) =>
        subject
            .With("destination.sendingApplication", destination.SendingApplication)
            .With("destination.sendingFacility", destination.SendingFacility)
            .With("destination.receivingApplication", destination.ReceivingApplication)
            .With("destination.receivingFacility", destination.ReceivingFacility)
            .With("destination.name", destination.Name);

    private Task RecordAsync(
        Hl7Profile profile,
        Hl7Destination destination,
        Hl7Message? message,
        Hl7DispatchOutcome outcome,
        int attempt,
        string contentHash,
        string relatedSopInstanceUid,
        string detail,
        TimeSpan elapsed,
        string acknowledgement,
        CancellationToken cancellationToken)
    {
        string state = outcome switch
        {
            Hl7DispatchOutcome.Acknowledged => "Acknowledged",
            Hl7DispatchOutcome.Sent => "Sent",

            // Recorded as sent, and this is the important one. The duplicate guard reads
            // this field, and a document that may already be at the far end must stop a
            // later identical send just as firmly as one that certainly is.
            Hl7DispatchOutcome.SentButUnconfirmed => "Sent",

            Hl7DispatchOutcome.AlreadyDelivered => "Duplicate",
            Hl7DispatchOutcome.Refused => "Refused",
            _ => "Failed"
        };

        var transaction = new IntegrationTransaction
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            MessageControlId = message?["MSH-10"] ?? string.Empty,
            DestinationName = destination.Name,
            MessageType = profile.MessageTypeAndTrigger,
            ProfileId = profile.ProfileId,
            ProfileRevision = profile.Revision,
            AttemptedUtc = _clock(),
            AttemptNumber = attempt,
            DeliveryState = state,
            AcknowledgementText = acknowledgement,
            ErrorDetail = outcome is Hl7DispatchOutcome.Acknowledged or Hl7DispatchOutcome.Sent
                ? string.Empty
                : detail,
            ContentHash = contentHash,
            RelatedSopInstanceUid = relatedSopInstanceUid,
            ElapsedMilliseconds = elapsed.TotalMilliseconds
        };

        return _transactions.RecordAsync(transaction, cancellationToken);
    }
}
