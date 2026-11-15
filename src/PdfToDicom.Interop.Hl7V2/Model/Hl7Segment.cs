namespace PdfToDicom.Interop.Hl7V2.Model;

/// <summary>
/// One segment: a three character name followed by its fields.
/// </summary>
/// <remarks>
/// The name is not checked against any list of known segments, and that is the point. A
/// Z segment carrying, say, a Study Instance UID is an ordinary segment here, which is what
/// allows a site specific one to be configured rather than coded.
/// </remarks>
public sealed class Hl7Segment
{
    private readonly List<Hl7Field> _fields = [];

    /// <summary>Creates a segment with the supplied name.</summary>
    /// <exception cref="ArgumentException">The name is not three characters.</exception>
    public Hl7Segment(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Length != 3)
        {
            throw new ArgumentException(
                $"A segment name is exactly three characters. Received '{name}'.", nameof(name));
        }

        Name = name;
    }

    /// <summary>The three character segment name, for example PID, OBX or ZDS.</summary>
    public string Name { get; }

    /// <summary>The fields, in order. Index 0 holds field 1.</summary>
    public IReadOnlyList<Hl7Field> Fields => _fields;

    /// <summary>
    /// The field at a one based position, creating it and any before it if needed.
    /// </summary>
    /// <remarks>
    /// For MSH, position 1 is the field separator itself and position 2 the encoding
    /// characters, as the standard requires. The codec supplies both when it reads a
    /// message and writes them back from the delimiters rather than from here, so a caller
    /// that overwrites either of them changes nothing about how the message is encoded.
    /// </remarks>
    public Hl7Field Field(int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

        while (_fields.Count < position)
        {
            _fields.Add(new Hl7Field());
        }

        return _fields[position - 1];
    }

    /// <summary>Reads a field without creating it.</summary>
    public Hl7Field? PeekField(int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

        return position <= _fields.Count ? _fields[position - 1] : null;
    }

    /// <summary>Appends a field.</summary>
    public Hl7Field AddField(string? value = null)
    {
        var field = new Hl7Field(value);
        _fields.Add(field);
        return field;
    }

    /// <summary>The number of fields the segment currently holds.</summary>
    public int FieldCount => _fields.Count;
}
