namespace PdfToDicom.Interop.Hl7V2.Encoding;

/// <summary>
/// The five characters that give an ER7 message its structure, and the escaping rules
/// that follow from them.
/// </summary>
/// <remarks>
/// <para>
/// A message declares its own delimiters in its first two fields, so nothing here is a
/// constant that a profile could not override. The defaults are the ones every
/// implementation in practice uses, and the ones the supplied conformance statement
/// names.
/// </para>
/// <para>
/// This type is the only place that knows what a separator means. The model above it
/// holds decoded values and never sees an escape sequence; the codec escapes on the way
/// out and unescapes on the way in. Keeping that in one place is what makes a field
/// containing a literal "|" or "&amp;" survive a round trip, which is not a theoretical
/// concern: a street address with an ampersand in it is ordinary.
/// </para>
/// </remarks>
public sealed class Hl7Delimiters
{
    /// <summary>The delimiters every implementation uses unless a message says otherwise.</summary>
    public static Hl7Delimiters Default { get; } = new('|', '^', '~', '\\', '&');

    /// <summary>Creates a delimiter set.</summary>
    /// <exception cref="ArgumentException">Two delimiters are the same character.</exception>
    public Hl7Delimiters(char field, char component, char repetition, char escape, char subcomponent)
    {
        char[] all = [field, component, repetition, escape, subcomponent];

        if (all.Distinct().Count() != all.Length)
        {
            throw new ArgumentException(
                "The five HL7 delimiters must all differ from one another. " +
                $"Received '{field}', '{component}', '{repetition}', '{escape}', '{subcomponent}'.");
        }

        if (all.Any(char.IsControl))
        {
            // A carriage return as a delimiter would end the segment it appears in, and the
            // message would parse as something structurally different rather than fail.
            throw new ArgumentException("An HL7 delimiter may not be a control character.");
        }

        Field = field;
        Component = component;
        Repetition = repetition;
        Escape = escape;
        Subcomponent = subcomponent;
    }

    /// <summary>Separates fields. MSH-1 carries this character itself.</summary>
    public char Field { get; }

    /// <summary>Separates components within a field repetition.</summary>
    public char Component { get; }

    /// <summary>Separates repetitions of one field.</summary>
    public char Repetition { get; }

    /// <summary>Introduces an escape sequence.</summary>
    public char Escape { get; }

    /// <summary>Separates subcomponents within a component.</summary>
    public char Subcomponent { get; }

    /// <summary>
    /// The four characters that make up MSH-2, in the order the standard fixes:
    /// component, repetition, escape, subcomponent.
    /// </summary>
    public string EncodingCharacters => new([Component, Repetition, Escape, Subcomponent]);

    /// <summary>
    /// Reads a delimiter set out of the opening of a message, which is the only place it
    /// can come from.
    /// </summary>
    /// <remarks>
    /// The layout is fixed by the standard and not negotiable: "MSH", then the field
    /// separator, then exactly four encoding characters. Anything shorter is not a message
    /// this library can read, and guessing at it would be worse than refusing.
    /// </remarks>
    public static Hl7Delimiters ReadFromMessageHeader(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Length < 8 || !message.StartsWith("MSH", StringComparison.Ordinal))
        {
            throw new Hl7ParseException(
                "A message must begin with an MSH segment carrying its delimiters, " +
                "for example \"MSH|^~\\&|\".");
        }

        return new Hl7Delimiters(message[3], message[4], message[5], message[6], message[7]);
    }

    /// <summary>
    /// Replaces every delimiter in a value with its escape sequence, so that the value
    /// survives as data rather than changing the shape of the message.
    /// </summary>
    public string EscapeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // The escape character is replaced first. Doing it later would also rewrite the
        // backslashes introduced by the four replacements before it, doubling them.
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (char c in value)
        {
            if (c == Escape) { builder.Append(Escape).Append('E').Append(Escape); }
            else if (c == Field) { builder.Append(Escape).Append('F').Append(Escape); }
            else if (c == Component) { builder.Append(Escape).Append('S').Append(Escape); }
            else if (c == Repetition) { builder.Append(Escape).Append('R').Append(Escape); }
            else if (c == Subcomponent) { builder.Append(Escape).Append('T').Append(Escape); }
            else { builder.Append(c); }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Turns the five standard escape sequences back into the characters they stand for.
    /// </summary>
    /// <remarks>
    /// Only those five are decoded. A sequence this library does not know, such as the
    /// formatting command <c>\.br\</c> or a hexadecimal <c>\X0D\</c>, is left exactly as it
    /// was found rather than dropped or guessed at. Deciding what a formatting command
    /// means is a presentation question, and a parser that silently discarded one would
    /// lose part of a report while appearing to succeed.
    /// </remarks>
    public string UnescapeValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains(Escape))
        {
            return value ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        int index = 0;

        while (index < value.Length)
        {
            char current = value[index];

            if (current != Escape)
            {
                builder.Append(current);
                index++;
                continue;
            }

            int closing = value.IndexOf(Escape, index + 1);

            if (closing < 0)
            {
                // An unterminated escape. Keeping it verbatim is the lesser evil: the value
                // is already malformed and discarding the remainder would hide that.
                builder.Append(value, index, value.Length - index);
                break;
            }

            string sequence = value.Substring(index + 1, closing - index - 1);

            char? decoded = sequence switch
            {
                "E" => Escape,
                "F" => Field,
                "S" => Component,
                "R" => Repetition,
                "T" => Subcomponent,
                _ => null
            };

            if (decoded is null)
            {
                builder.Append(value, index, closing - index + 1);
            }
            else
            {
                builder.Append(decoded.Value);
            }

            index = closing + 1;
        }

        return builder.ToString();
    }
}
