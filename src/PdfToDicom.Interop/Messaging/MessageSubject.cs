namespace PdfToDicom.Interop.Messaging;

/// <summary>
/// The application's data, in the form a mapping reads it: named values, and named
/// attachments for anything that is not text.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a bag of named strings rather than a typed model of a patient and a study.
/// A typed model would have to grow a property every time a site wanted to map something
/// nobody had thought of, and growing a type is a release. A name an administrator can
/// write in a profile is the whole point.
/// </para>
/// <para>
/// It lives in the protocol independent assembly because it is not an HL7 idea. The same
/// subject would feed a FHIR resource.
/// </para>
/// </remarks>
public sealed class MessageSubject
{
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, byte[]> _attachments;

    /// <summary>Creates a subject from named values and attachments.</summary>
    public MessageSubject(
        IDictionary<string, string>? values = null,
        IDictionary<string, byte[]>? attachments = null)
    {
        _values = new Dictionary<string, string>(
            values ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

        _attachments = new Dictionary<string, byte[]>(
            attachments ?? new Dictionary<string, byte[]>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A subject with nothing in it.</summary>
    public static MessageSubject Empty { get; } = new();

    /// <summary>Every named value.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Every named attachment.</summary>
    public IReadOnlyDictionary<string, byte[]> Attachments => _attachments;

    /// <summary>
    /// Reads a value, answering empty when there is none.
    /// </summary>
    /// <remarks>
    /// Empty rather than null or an exception, because a profile asking for an optional
    /// value is the ordinary case and should not have to say so twice. Whether an empty
    /// result is acceptable is the mapping's business, which is why a mapping can be
    /// marked as required.
    /// </remarks>
    public string Get(string name) =>
        name is not null && _values.TryGetValue(name, out string? value) ? value : string.Empty;

    /// <summary>Whether a named value is present and not empty.</summary>
    public bool Has(string name) => !string.IsNullOrEmpty(Get(name));

    /// <summary>Reads an attachment, or null when there is none.</summary>
    public byte[]? GetAttachment(string name) =>
        name is not null && _attachments.TryGetValue(name, out byte[]? bytes) ? bytes : null;

    /// <summary>Returns a copy with one more value, leaving this one alone.</summary>
    public MessageSubject With(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var values = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase)
        {
            [name] = value ?? string.Empty
        };

        return new MessageSubject(values, _attachments);
    }

    /// <summary>Returns a copy with one more attachment.</summary>
    public MessageSubject WithAttachment(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);

        var attachments = new Dictionary<string, byte[]>(_attachments, StringComparer.OrdinalIgnoreCase)
        {
            [name] = bytes
        };

        return new MessageSubject(_values, attachments);
    }
}
