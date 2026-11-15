using System.Text.RegularExpressions;

namespace PdfToDicom.Interop.Hl7V2.Model;

/// <summary>
/// A reference to one place in a message, written the way conformance statements write it.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is <c>SEG[occurrence]-field(repetition).component.subcomponent</c>, where
/// everything after the field number is optional and defaults to the first:
/// </para>
/// <list type="bullet">
///   <item><description><c>PID-3</c> the third field of the first PID</description></item>
///   <item><description><c>PID-5.1.1</c> its first subcomponent, as a statement writes a surname</description></item>
///   <item><description><c>PID-3(2).1</c> the first component of the second repetition</description></item>
///   <item><description><c>OBX[2]-5</c> the fifth field of the second OBX segment</description></item>
/// </list>
/// <para>
/// Two different brackets for two different things is a cost, but the alternative is
/// worse. Conformance statements and vendor notes already write field repetition in
/// parentheses, and a mapping file is read by somebody holding such a document. Segment
/// occurrence has no settled notation at all, so it gets the one that is still free.
/// </para>
/// </remarks>
public sealed partial class Hl7Path : IEquatable<Hl7Path>
{
    [GeneratedRegex(
        @"^(?<seg>[A-Za-z][A-Za-z0-9]{2})(\[(?<occ>\d+)\])?-(?<fld>\d+)(\((?<rep>\d+)\))?(\.(?<cmp>\d+))?(\.(?<sub>\d+))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    private Hl7Path(
        string segment, int segmentOccurrence, int field, int repetition, int component, int subcomponent)
    {
        Segment = segment;
        SegmentOccurrence = segmentOccurrence;
        Field = field;
        Repetition = repetition;
        Component = component;
        Subcomponent = subcomponent;
    }

    /// <summary>The three character segment name, upper cased.</summary>
    public string Segment { get; }

    /// <summary>Which occurrence of that segment, one based.</summary>
    public int SegmentOccurrence { get; }

    /// <summary>The field position, one based.</summary>
    public int Field { get; }

    /// <summary>Which repetition of that field, one based.</summary>
    public int Repetition { get; }

    /// <summary>The component position, one based.</summary>
    public int Component { get; }

    /// <summary>The subcomponent position, one based.</summary>
    public int Subcomponent { get; }

    /// <summary>Parses a path, throwing if it is not one.</summary>
    /// <exception cref="FormatException">The text is not a path.</exception>
    public static Hl7Path Parse(string text)
    {
        if (!TryParse(text, out Hl7Path? path))
        {
            throw new FormatException(
                $"'{text}' is not an HL7 path. Expected something of the form " +
                "\"PID-5.1.1\", \"PID-3(2).1\" or \"OBX[2]-5\".");
        }

        return path;
    }

    /// <summary>Parses a path, reporting failure rather than throwing.</summary>
    public static bool TryParse(string? text, out Hl7Path path)
    {
        path = null!;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = PathPattern().Match(text.Trim());

        if (!match.Success)
        {
            return false;
        }

        int Group(string name, int fallback) =>
            match.Groups[name].Success ? int.Parse(match.Groups[name].Value) : fallback;

        int occurrence = Group("occ", 1);
        int field = int.Parse(match.Groups["fld"].Value);
        int repetition = Group("rep", 1);
        int component = Group("cmp", 1);
        int subcomponent = Group("sub", 1);

        // Zero is not a position in HL7, where everything is counted from one. Allowing it
        // would make "PID-0" mean the segment name in some implementations and nothing in
        // others, which is exactly the ambiguity a mapping file cannot afford.
        if (field < 1 || occurrence < 1 || repetition < 1 || component < 1 || subcomponent < 1)
        {
            return false;
        }

        path = new Hl7Path(
            match.Groups["seg"].Value.ToUpperInvariant(),
            occurrence, field, repetition, component, subcomponent);

        return true;
    }

    /// <summary>Renders the path in the same grammar it is parsed from.</summary>
    public override string ToString()
    {
        string segment = SegmentOccurrence > 1 ? $"{Segment}[{SegmentOccurrence}]" : Segment;
        string field = Repetition > 1 ? $"{Field}({Repetition})" : Field.ToString();

        // The trailing ".1.1" is noise on a path that addresses a whole field, and a
        // mapping file is read by people.
        if (Subcomponent > 1)
        {
            return $"{segment}-{field}.{Component}.{Subcomponent}";
        }

        return Component > 1 ? $"{segment}-{field}.{Component}" : $"{segment}-{field}";
    }

    /// <inheritdoc />
    public bool Equals(Hl7Path? other) =>
        other is not null &&
        Segment == other.Segment &&
        SegmentOccurrence == other.SegmentOccurrence &&
        Field == other.Field &&
        Repetition == other.Repetition &&
        Component == other.Component &&
        Subcomponent == other.Subcomponent;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Hl7Path);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Segment, SegmentOccurrence, Field, Repetition, Component, Subcomponent);
}
