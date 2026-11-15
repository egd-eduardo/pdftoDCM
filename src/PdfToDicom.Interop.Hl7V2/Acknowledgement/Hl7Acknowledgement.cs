using PdfToDicom.Interop.Hl7V2.Model;

namespace PdfToDicom.Interop.Hl7V2.Acknowledgement;

/// <summary>
/// What a receiving system said back, in the vocabulary of HL7 table 0008.
/// </summary>
/// <remarks>
/// The commit codes exist because an interface engine may answer for itself before the
/// application behind it has seen the message. A commit accept therefore says the engine
/// has the message safely, and nothing at all about whether a patient's record now holds
/// the report. The two are kept apart here for that reason, and a profile that only ever
/// sees one of them loses nothing by the distinction existing.
/// </remarks>
public enum AcknowledgementKind
{
    /// <summary>Not a code this build recognises.</summary>
    Unknown = 0,

    /// <summary>AA, application accept. The receiving application took it.</summary>
    ApplicationAccept = 1,

    /// <summary>AE, application error. It understood the message and could not process it.</summary>
    ApplicationError = 2,

    /// <summary>AR, application reject. It refused the message outright.</summary>
    ApplicationReject = 3,

    /// <summary>CA, commit accept. An engine has it; the application may not have seen it.</summary>
    CommitAccept = 4,

    /// <summary>CE, commit error.</summary>
    CommitError = 5,

    /// <summary>CR, commit reject.</summary>
    CommitReject = 6
}

/// <summary>One acknowledgement, read.</summary>
/// <param name="Code">The raw value of MSA-1, whatever it was.</param>
/// <param name="Kind">What that code means, or unknown.</param>
/// <param name="AcknowledgedControlId">MSA-2, the identifier of the message being answered.</param>
/// <param name="Text">MSA-3, which is often the only explanation there is.</param>
/// <param name="Errors">Anything the ERR segments added.</param>
public sealed record Hl7Acknowledgement(
    string Code,
    AcknowledgementKind Kind,
    string AcknowledgedControlId,
    string Text,
    IReadOnlyList<string> Errors)
{
    /// <summary>
    /// Whether the receiver took the message.
    /// </summary>
    /// <remarks>
    /// An unknown code is not positive. A receiver answering with something this build does
    /// not recognise has said something, and guessing that it meant yes is the one reading
    /// that could put a report nowhere while the operator is told it arrived.
    /// </remarks>
    public bool IsPositive =>
        Kind is AcknowledgementKind.ApplicationAccept or AcknowledgementKind.CommitAccept;

    /// <summary>
    /// Whether this answers the message that was sent.
    /// </summary>
    /// <remarks>
    /// An acknowledgement naming a different message is not an acknowledgement of this one.
    /// It usually means two conversations have been crossed, which is worth finding out
    /// about immediately rather than discovering later from a missing report. An empty
    /// MSA-2 is accepted, because some interfaces leave it empty and refusing those would
    /// break a working integration over a field nobody reads.
    /// </remarks>
    public bool Answers(string sentControlId) =>
        string.IsNullOrEmpty(AcknowledgedControlId) ||
        string.Equals(AcknowledgedControlId, sentControlId, StringComparison.Ordinal);

    /// <summary>A line for an operator, naming the code and whatever explanation came with it.</summary>
    public string Describe()
    {
        string detail = string.Join("; ",
            new[] { Text }.Concat(Errors).Where(s => !string.IsNullOrWhiteSpace(s)));

        string meaning = Kind switch
        {
            AcknowledgementKind.ApplicationAccept => "accepted",
            AcknowledgementKind.ApplicationError => "could not be processed",
            AcknowledgementKind.ApplicationReject => "was rejected",
            AcknowledgementKind.CommitAccept => "was received, though not yet processed",
            AcknowledgementKind.CommitError => "was received and could not be committed",
            AcknowledgementKind.CommitReject => "was received and rejected",
            _ => $"was answered with '{Code}', which this build does not recognise"
        };

        return detail.Length > 0
            ? $"The message {meaning}: {detail}"
            : $"The message {meaning}.";
    }
}
