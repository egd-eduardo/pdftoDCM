using PdfToDicom.Interop.Hl7V2.Model;

namespace PdfToDicom.Interop.Hl7V2.Acknowledgement;

/// <summary>
/// Reads an acknowledgement out of whatever came back.
/// </summary>
public static class Hl7AcknowledgementReader
{
    /// <summary>Reads an acknowledgement from its ER7 text.</summary>
    /// <exception cref="Hl7ParseException">The reply is not a message at all.</exception>
    public static Hl7Acknowledgement Read(string er7) => Read(Hl7Message.Parse(er7));

    /// <summary>Reads an acknowledgement from an already parsed message.</summary>
    /// <remarks>
    /// Tolerant about the shape and strict about the meaning. A reply with no MSA is
    /// reported as unknown rather than refused, because a receiver answering oddly has
    /// still answered, and an operator is better served by being told what arrived than by
    /// an exception. Nothing in that tolerance makes an unrecognised answer count as a
    /// success.
    /// </remarks>
    public static Hl7Acknowledgement Read(Hl7Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Hl7Segment? msa = message.FindSegment("MSA");

        string code = msa is null ? string.Empty : message["MSA-1"].Trim();
        string controlId = msa is null ? string.Empty : message["MSA-2"].Trim();
        string text = msa is null ? string.Empty : message["MSA-3"].Trim();

        return new Hl7Acknowledgement(
            code,
            KindOf(code),
            controlId,
            text,
            ErrorsIn(message));
    }

    /// <summary>
    /// Renders a whole field as text, components and all.
    /// </summary>
    /// <remarks>
    /// Reading only the first component is the obvious thing and loses the only part worth
    /// having. An ERR field is composite, and in both the old and the new shape the
    /// sentence explaining why a report was refused sits in a later component: taking
    /// component one of <c>PID^1^3^101^Required field missing</c> yields "PID".
    /// </remarks>
    private static string Flatten(Hl7Field? field)
    {
        if (field is null)
        {
            return string.Empty;
        }

        IEnumerable<string> parts = field.Repetitions
            .SelectMany(r => r.Components)
            .SelectMany(c => c.Subcomponents)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0);

        return string.Join(" ", parts);
    }

    private static AcknowledgementKind KindOf(string code) =>
        code.ToUpperInvariant() switch
        {
            "AA" => AcknowledgementKind.ApplicationAccept,
            "AE" => AcknowledgementKind.ApplicationError,
            "AR" => AcknowledgementKind.ApplicationReject,
            "CA" => AcknowledgementKind.CommitAccept,
            "CE" => AcknowledgementKind.CommitError,
            "CR" => AcknowledgementKind.CommitReject,
            _ => AcknowledgementKind.Unknown
        };

    /// <summary>
    /// Everything the ERR segments have to say.
    /// </summary>
    /// <remarks>
    /// Where the error text lives moved between versions: through 2.3.1 it is in ERR-1,
    /// from 2.5 it is spread across ERR-3 for the code and ERR-8 for a message meant for a
    /// person. Rather than branch on the version in MSH-12, which a receiver answering an
    /// error may well have got wrong, every one of those is read and whatever is there is
    /// kept. The cost is an occasional repetition in the text; the alternative is losing the
    /// one sentence explaining why a report was refused.
    /// </remarks>
    private static IReadOnlyList<string> ErrorsIn(Hl7Message message)
    {
        var errors = new List<string>();

        foreach (Hl7Segment segment in message.FindSegments("ERR"))
        {
            // 1 is where the older versions put everything; 3 is the coded reason from 2.5
            // onwards; 7 is diagnostic text and 8 is the sentence meant for a person. All
            // four are read because any of them may hold the only explanation there is.
            foreach (int position in new[] { 1, 3, 7, 8 })
            {
                string value = Flatten(segment.PeekField(position));

                if (value.Length > 0 && !errors.Contains(value, StringComparer.Ordinal))
                {
                    errors.Add(value);
                }
            }
        }

        return errors;
    }
}
