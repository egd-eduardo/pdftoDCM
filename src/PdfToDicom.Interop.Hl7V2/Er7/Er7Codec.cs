using System.Text;
using PdfToDicom.Interop.Hl7V2.Encoding;
using PdfToDicom.Interop.Hl7V2.Model;

namespace PdfToDicom.Interop.Hl7V2.Er7;

/// <summary>
/// Reads and writes the pipe and hat encoding, the only one in use in practice and the one
/// the supplied conformance statement names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strict in what it writes, tolerant in what it reads.</b> A message written here always
/// separates segments with a carriage return, which is what the standard says and what a
/// receiving system is entitled to expect. A message read here accepts a carriage return, a
/// line feed, or both, because messages arrive from text editors, from log files and from
/// systems that are simply wrong, and refusing to read one that a human can plainly see is
/// a message helps nobody.
/// </para>
/// <para>
/// <b>MSH is not an ordinary segment and cannot be treated as one.</b> Its first field is the
/// field separator itself and its second is the four encoding characters, so the separator
/// that would divide them is the very thing being declared. Reading it with the ordinary
/// rule produces a message whose fields are all off by one, and writing it with the
/// ordinary rule escapes the encoding characters into nonsense. Both directions therefore
/// special case it, in one place each, and the round trip test is what holds them together.
/// </para>
/// </remarks>
public static class Er7Codec
{
    /// <summary>The segment separator this codec writes. Never a line feed.</summary>
    public const char SegmentTerminator = '\r';

    /// <summary>Reads a message from its ER7 form.</summary>
    /// <exception cref="Hl7ParseException">The text is not a readable message.</exception>
    public static Hl7Message Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string trimmed = text.TrimStart('\r', '\n', ' ', '\t');

        if (trimmed.Length == 0)
        {
            throw new Hl7ParseException("The message is empty.");
        }

        Hl7Delimiters delimiters = Hl7Delimiters.ReadFromMessageHeader(trimmed);
        var message = new Hl7Message(delimiters);

        foreach (string line in SplitSegments(trimmed))
        {
            message.AddSegment(ParseSegment(line, delimiters));
        }

        return message;
    }

    /// <summary>Writes a message in ER7 form.</summary>
    public static string Write(Hl7Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var builder = new StringBuilder();

        foreach (Hl7Segment segment in message.Segments)
        {
            if (builder.Length > 0)
            {
                builder.Append(SegmentTerminator);
            }

            WriteSegment(builder, segment, message.Delimiters);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> SplitSegments(string text)
    {
        // An empty line between segments is discarded rather than becoming a segment with
        // no name, which is what a message pasted out of an email tends to contain.
        return text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.TrimEnd())
            .Where(s => s.Length > 0);
    }

    private static Hl7Segment ParseSegment(string line, Hl7Delimiters delimiters)
    {
        if (line.Length < 3)
        {
            throw new Hl7ParseException(
                $"'{line}' is too short to be a segment: a segment begins with a three " +
                "character name.");
        }

        string name = line[..3];

        if (!name.All(char.IsLetterOrDigit))
        {
            throw new Hl7ParseException(
                $"'{name}' is not a segment name. A segment begins with three letters or " +
                "digits, for example PID, OBX or ZDS.");
        }

        var segment = new Hl7Segment(name.ToUpperInvariant());
        bool isHeader = segment.Name == "MSH";

        if (line.Length == 3)
        {
            return segment;
        }

        string body = line[4..];
        string[] rawFields = body.Split(delimiters.Field);

        if (isHeader)
        {
            // MSH-1 is the separator and MSH-2 the encoding characters. Both are recorded
            // so that a caller reading MSH-2 sees what the message declared, and both are
            // written back from the delimiters rather than from these values, so neither
            // can drift out of step with how the message is actually encoded.
            segment.Field(1).Value = delimiters.Field.ToString();
            segment.Field(2).Value = delimiters.EncodingCharacters;

            // body here begins at the encoding characters, so the first entry is MSH-2 and
            // the rest follow from MSH-3.
            for (int index = 1; index < rawFields.Length; index++)
            {
                FillField(segment.Field(index + 2), rawFields[index], delimiters);
            }

            return segment;
        }

        for (int index = 0; index < rawFields.Length; index++)
        {
            FillField(segment.Field(index + 1), rawFields[index], delimiters);
        }

        return segment;
    }

    private static void FillField(Hl7Field field, string raw, Hl7Delimiters delimiters)
    {
        string[] repetitions = raw.Split(delimiters.Repetition);

        for (int r = 0; r < repetitions.Length; r++)
        {
            Hl7Repetition repetition = field.Repetition(r + 1);
            string[] components = repetitions[r].Split(delimiters.Component);

            for (int c = 0; c < components.Length; c++)
            {
                Hl7Component component = repetition.Component(c + 1);
                string[] subcomponents = components[c].Split(delimiters.Subcomponent);

                component.SetSubcomponents(
                    subcomponents.Select(delimiters.UnescapeValue));
            }
        }
    }

    private static void WriteSegment(StringBuilder builder, Hl7Segment segment, Hl7Delimiters delimiters)
    {
        builder.Append(segment.Name);

        bool isHeader = segment.Name == "MSH";

        if (isHeader)
        {
            // The separator and the encoding characters are written from the delimiters,
            // verbatim and unescaped. Escaping MSH-2 would turn "^~\&" into a sequence that
            // declares nothing, and a receiving system would be unable to read the rest.
            builder.Append(delimiters.Field).Append(delimiters.EncodingCharacters);

            for (int position = 3; position <= segment.FieldCount; position++)
            {
                builder.Append(delimiters.Field);
                WriteField(builder, segment.PeekField(position), delimiters);
            }

            return;
        }

        for (int position = 1; position <= segment.FieldCount; position++)
        {
            builder.Append(delimiters.Field);
            WriteField(builder, segment.PeekField(position), delimiters);
        }
    }

    /// <summary>Encodes one field on its own, exactly as it would appear in a message.</summary>
    /// <remarks>
    /// For anything that shows a message to a person. <see cref="Hl7Field.Value"/> is the
    /// first subcomponent, which is the right answer for a simple field and a misleading one
    /// for a composite: it renders MSH-9 as "ORU" and hides the trigger event, so somebody
    /// checking a profile against a vendor's document would conclude a value is absent when
    /// it is present. Escaping is kept, because what is on the wire is the question being
    /// asked.
    /// </remarks>
    public static string WriteField(Hl7Field? field, Hl7Delimiters? delimiters = null)
    {
        var builder = new StringBuilder();
        WriteField(builder, field, delimiters ?? Hl7Delimiters.Default);
        return builder.ToString();
    }

    private static void WriteField(StringBuilder builder, Hl7Field? field, Hl7Delimiters delimiters)
    {
        if (field is null)
        {
            return;
        }

        for (int r = 0; r < field.Repetitions.Count; r++)
        {
            if (r > 0)
            {
                builder.Append(delimiters.Repetition);
            }

            Hl7Repetition repetition = field.Repetitions[r];

            for (int c = 0; c < repetition.Components.Count; c++)
            {
                if (c > 0)
                {
                    builder.Append(delimiters.Component);
                }

                Hl7Component component = repetition.Components[c];

                for (int s = 0; s < component.Subcomponents.Count; s++)
                {
                    if (s > 0)
                    {
                        builder.Append(delimiters.Subcomponent);
                    }

                    builder.Append(delimiters.EscapeValue(component.Subcomponents[s]));
                }
            }
        }
    }
}
