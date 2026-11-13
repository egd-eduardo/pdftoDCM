namespace PdfToDicom.Interop.Hl7V2.Model;

/// <summary>
/// One component of a field repetition, itself divisible into subcomponents.
/// </summary>
/// <remarks>
/// Values held here are decoded: a component containing a literal separator holds that
/// character, not its escape sequence. Escaping belongs to the codec and happens once, on
/// the way out.
/// </remarks>
public sealed class Hl7Component
{
    private readonly List<string> _subcomponents = [];

    /// <summary>Creates an empty component.</summary>
    public Hl7Component()
    {
    }

    /// <summary>Creates a component holding a single value.</summary>
    public Hl7Component(string? value)
    {
        _subcomponents.Add(value ?? string.Empty);
    }

    /// <summary>The subcomponents, in order. Always holds at least one entry.</summary>
    public IReadOnlyList<string> Subcomponents => _subcomponents;

    /// <summary>
    /// The component's first subcomponent, which for a component that was never divided
    /// is simply its value.
    /// </summary>
    public string Value
    {
        get => _subcomponents.Count > 0 ? _subcomponents[0] : string.Empty;
        set => SetSubcomponent(1, value);
    }

    /// <summary>True when every subcomponent is empty.</summary>
    public bool IsEmpty => _subcomponents.All(string.IsNullOrEmpty);

    /// <summary>
    /// Reads a subcomponent by its one based position. A position that does not exist
    /// reads as empty rather than throwing, because a mapping that asks for an optional
    /// subcomponent should not have to check first.
    /// </summary>
    public string GetSubcomponent(int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

        return position <= _subcomponents.Count ? _subcomponents[position - 1] : string.Empty;
    }

    /// <summary>
    /// Writes a subcomponent by its one based position, creating any empty ones before it.
    /// </summary>
    public void SetSubcomponent(int position, string? value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

        while (_subcomponents.Count < position)
        {
            _subcomponents.Add(string.Empty);
        }

        _subcomponents[position - 1] = value ?? string.Empty;
    }

    /// <summary>Replaces every subcomponent with the supplied values.</summary>
    public void SetSubcomponents(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        _subcomponents.Clear();
        _subcomponents.AddRange(values.Select(v => v ?? string.Empty));

        if (_subcomponents.Count == 0)
        {
            _subcomponents.Add(string.Empty);
        }
    }
}
