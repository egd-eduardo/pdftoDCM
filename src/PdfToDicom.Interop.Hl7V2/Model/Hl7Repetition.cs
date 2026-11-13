namespace PdfToDicom.Interop.Hl7V2.Model;

/// <summary>
/// One occurrence of a field. A field that does not repeat has exactly one of these.
/// </summary>
/// <remarks>
/// Repetition is modelled explicitly rather than as a special case of a component, because
/// the two mean entirely different things to a receiving system: a second repetition of
/// PID-3 is a second identifier for the same patient, while a second component of the
/// first repetition is part of that one identifier.
/// </remarks>
public sealed class Hl7Repetition
{
    private readonly List<Hl7Component> _components = [];

    /// <summary>Creates an empty repetition.</summary>
    public Hl7Repetition()
    {
    }

    /// <summary>Creates a repetition holding a single undivided value.</summary>
    public Hl7Repetition(string? value)
    {
        _components.Add(new Hl7Component(value));
    }

    /// <summary>The components, in order.</summary>
    public IReadOnlyList<Hl7Component> Components => _components;

    /// <summary>
    /// The repetition's first component's first subcomponent: the value of a field that was
    /// never divided.
    /// </summary>
    public string Value
    {
        get => _components.Count > 0 ? _components[0].Value : string.Empty;
        set => Component(1).Value = value;
    }

    /// <summary>True when every component is empty.</summary>
    public bool IsEmpty => _components.All(c => c.IsEmpty);

    /// <summary>
    /// The component at a one based position, creating it and any before it if needed.
    /// </summary>
    public Hl7Component Component(int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

        while (_components.Count < position)
        {
            _components.Add(new Hl7Component());
        }

        return _components[position - 1];
    }

    /// <summary>
    /// Reads a component without creating it, which is what a read of an absent optional
    /// component should do.
    /// </summary>
    public Hl7Component? PeekComponent(int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

        return position <= _components.Count ? _components[position - 1] : null;
    }

    /// <summary>Appends a component.</summary>
    public Hl7Component AddComponent(string? value = null)
    {
        var component = new Hl7Component(value);
        _components.Add(component);
        return component;
    }
}
