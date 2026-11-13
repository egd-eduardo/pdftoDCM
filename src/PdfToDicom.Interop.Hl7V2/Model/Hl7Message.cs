using PdfToDicom.Interop.Hl7V2.Encoding;
using PdfToDicom.Interop.Hl7V2.Er7;

namespace PdfToDicom.Interop.Hl7V2.Model;

/// <summary>
/// A complete HL7 version 2 message: its delimiters and its segments, in order.
/// </summary>
/// <remarks>
/// The type knows nothing about message types, trigger events or profiles. An ORU is a
/// message whose MSH-9 happens to read ORU, and this class cannot tell the difference.
/// Everything that depends on knowing is a layer above, which is what keeps a vendor's
/// requirements out of the parser.
/// </remarks>
public sealed class Hl7Message
{
    private readonly List<Hl7Segment> _segments = [];

    /// <summary>Creates an empty message with the supplied delimiters.</summary>
    public Hl7Message(Hl7Delimiters? delimiters = null)
    {
        Delimiters = delimiters ?? Hl7Delimiters.Default;
    }

    /// <summary>The delimiters this message is encoded with.</summary>
    public Hl7Delimiters Delimiters { get; }

    /// <summary>The segments, in order.</summary>
    public IReadOnlyList<Hl7Segment> Segments => _segments;

    /// <summary>Reads a message from its ER7 form.</summary>
    public static Hl7Message Parse(string text) => Er7Codec.Parse(text);

    /// <summary>Writes the message in ER7 form, with carriage returns between segments.</summary>
    public string ToEr7() => Er7Codec.Write(this);

    /// <inheritdoc />
    public override string ToString() => ToEr7();

    /// <summary>Appends a segment.</summary>
    public Hl7Segment AddSegment(string name)
    {
        var segment = new Hl7Segment(name);
        _segments.Add(segment);
        return segment;
    }

    /// <summary>Appends an already built segment.</summary>
    public Hl7Segment AddSegment(Hl7Segment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        _segments.Add(segment);
        return segment;
    }

    /// <summary>
    /// Finds an occurrence of a segment by name, one based, or null when there is no such
    /// occurrence.
    /// </summary>
    public Hl7Segment? FindSegment(string name, int occurrence = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);

        int seen = 0;

        foreach (Hl7Segment segment in _segments)
        {
            if (!segment.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++seen == occurrence)
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>Every occurrence of a segment, in order.</summary>
    public IReadOnlyList<Hl7Segment> FindSegments(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _segments
            .Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Reads or writes one place in the message, addressed the way a conformance statement
    /// addresses it.
    /// </summary>
    /// <remarks>
    /// A read of something absent answers with an empty string rather than throwing or
    /// answering null, so that a mapping asking for an optional field does not have to
    /// check first. A write creates whatever is missing on the way, including the segment,
    /// which is what lets a profile build a message by assignment alone.
    /// </remarks>
    public string this[string path]
    {
        get => Get(Hl7Path.Parse(path));
        set => Set(Hl7Path.Parse(path), value);
    }

    /// <summary>Reads one place in the message, or an empty string when it is absent.</summary>
    public string Get(Hl7Path path)
    {
        ArgumentNullException.ThrowIfNull(path);

        Hl7Segment? segment = FindSegment(path.Segment, path.SegmentOccurrence);

        return segment?
            .PeekField(path.Field)?
            .PeekRepetition(path.Repetition)?
            .PeekComponent(path.Component)?
            .GetSubcomponent(path.Subcomponent)
            ?? string.Empty;
    }

    /// <summary>
    /// Writes one place in the message, creating the segment, field, repetition and
    /// components needed to reach it.
    /// </summary>
    /// <remarks>
    /// A missing segment occurrence is created by appending, which means writing to
    /// <c>OBX[3]-5</c> when there is one OBX produces three. That is deliberate: a profile
    /// driven builder adds repeating segments in order, and silently writing into the first
    /// one instead would put an observation on the wrong line.
    /// </remarks>
    public void Set(Hl7Path path, string? value)
    {
        ArgumentNullException.ThrowIfNull(path);

        Hl7Segment? segment = FindSegment(path.Segment, path.SegmentOccurrence);

        if (segment is null)
        {
            // Append until the occurrence asked for exists. Counting first, rather than
            // appending one and looking again, keeps this terminating by construction.
            for (int present = FindSegments(path.Segment).Count;
                 present < path.SegmentOccurrence;
                 present++)
            {
                segment = AddSegment(path.Segment);
            }
        }

        if (segment is null)
        {
            throw new InvalidOperationException(
                $"The segment for '{path}' could neither be found nor created.");
        }

        segment
            .Field(path.Field)
            .Repetition(path.Repetition)
            .Component(path.Component)
            .SetSubcomponent(path.Subcomponent, value);
    }
}
