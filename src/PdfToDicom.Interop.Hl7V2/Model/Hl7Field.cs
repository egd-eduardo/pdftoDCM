namespace PdfToDicom.Interop.Hl7V2.Model;

/// <summary>
/// One field of a segment, holding one or more repetitions.
/// </summary>
public sealed class Hl7Field
{
    private readonly List<Hl7Repetition> _repetitions = [];

    /// <summary>Creates an empty field.</summary>
    public Hl7Field()
    {
    }

    /// <summary>Creates a field holding a single undivided value.</summary>
    public Hl7Field(string? value)
    {
        _repetitions.Add(new Hl7Repetition(value));
    }

    /// <summary>The repetitions, in order.</summary>
    public IReadOnlyList<Hl7Repetition> Repetitions => _repetitions;

    /// <summary>
    /// The first repetition's first component's first subcomponent: the value of a simple
    /// field.
    /// </summary>
    public string Value
    {
        get => _repetitions.Count > 0 ? _repetitions[0].Value : string.Empty;
        set => Repetition(1).Value = value;
    }

    /// <summary>True when every repetition is empty.</summary>
    public bool IsEmpty => _repetitions.All(r => r.IsEmpty);

    /// <summary>
    /// Whether the field carries HL7's explicit null, two double quote characters, which
    /// means "this value is known to be absent" rather than "nothing was supplied".
    /// </summary>
    /// <remarks>
    /// The distinction matters to a receiving system that updates records, where an empty
    /// field leaves a value alone and an explicit null erases it. This library stores the
    /// two quotes verbatim and never invents or removes them; it only reports the
    /// difference, because acting on it is the caller's decision and not a parser's.
    /// </remarks>
    public bool IsExplicitNull =>
        _repetitions.Count == 1 && _repetitions[0].Value == "\"\"";

    /// <summary>
    /// The repetition at a one based position, creating it and any before it if needed.
    /// </summary>
    public Hl7Repetition Repetition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

        while (_repetitions.Count < position)
        {
            _repetitions.Add(new Hl7Repetition());
        }

        return _repetitions[position - 1];
    }

    /// <summary>Reads a repetition without creating it.</summary>
    public Hl7Repetition? PeekRepetition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

        return position <= _repetitions.Count ? _repetitions[position - 1] : null;
    }

    /// <summary>Appends a repetition.</summary>
    public Hl7Repetition AddRepetition(string? value = null)
    {
        var repetition = new Hl7Repetition(value);
        _repetitions.Add(repetition);
        return repetition;
    }
}
