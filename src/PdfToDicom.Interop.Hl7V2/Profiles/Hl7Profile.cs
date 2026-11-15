namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// Everything one interface needs: the message it expects, the rules it imposes, and where
/// each value comes from.
/// </summary>
/// <remarks>
/// <para>
/// A profile is data. Nothing in this type, and nothing in the loader that reads it, knows
/// what ORU means or that PID holds a patient. That is the premise of the whole exercise:
/// if adding a destination required a developer, the engine would not be generic, it would
/// just be a place where vendor code is kept.
/// </para>
/// <para>
/// The structure and the mapping are separate lists on purpose. A vendor who changes a
/// field's cardinality has not changed where the value comes from, and a site that changes
/// where a value comes from has not changed the vendor's rules. Keeping them in one list
/// would force both to be edited whenever either moved.
/// </para>
/// </remarks>
public sealed record Hl7Profile
{
    /// <summary>A stable identifier, used in routing rules and in the transaction record.</summary>
    public required string ProfileId { get; init; }

    /// <summary>What an administrator sees in a list.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Which revision of this profile. Incremented when it is edited, and recorded against
    /// every message, so that a message sent last month can still be explained.
    /// </summary>
    public int Revision { get; init; } = 1;

    /// <summary>Whether the profile may be used.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The HL7 version, which belongs to the profile and not to the product.</summary>
    public string Hl7Version { get; init; } = "2.4";

    /// <summary>The message type, for example ORU.</summary>
    public required string MessageType { get; init; }

    /// <summary>The trigger event, for example R01.</summary>
    public required string TriggerEvent { get; init; }

    /// <summary>The structure name, for example ORU_R01. Written to MSH-9.3 when present.</summary>
    public string MessageStructure { get; init; } = string.Empty;

    /// <summary>What goes in MSH-11.</summary>
    public string ProcessingId { get; init; } = "P";

    /// <summary>
    /// A prefix for the message control identifier, so that messages from this workstation
    /// are recognisable in somebody else's log.
    /// </summary>
    public string ControlIdPrefix { get; init; } = string.Empty;

    /// <summary>The delimiters, character set and unrepresentable character policy.</summary>
    public ProfileEncoding Encoding { get; init; } = new();

    /// <summary>What this interface does about acknowledgements.</summary>
    public ProfileAcknowledgement Acknowledgement { get; init; } = new();

    /// <summary>The segments, in the order they must appear.</summary>
    public IReadOnlyList<ProfileSegment> Segments { get; init; } = [];

    /// <summary>The field rules.</summary>
    public IReadOnlyList<ProfileField> Fields { get; init; } = [];

    /// <summary>Where each value comes from.</summary>
    public IReadOnlyList<ProfileMapping> Mappings { get; init; } = [];

    /// <summary>
    /// Code translation tables, by name. A table turns one vocabulary into another, which
    /// is how a sex of "Female" becomes an F without anybody writing code.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>The message type and trigger, as MSH-9 carries them.</summary>
    public string MessageTypeAndTrigger => $"{MessageType}^{TriggerEvent}";

    /// <summary>Finds the rules for one field, or null when the profile says nothing about it.</summary>
    public ProfileField? FieldAt(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Fields.FirstOrDefault(f =>
            string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds the rules for one segment, or null when the profile does not list it.</summary>
    public ProfileSegment? SegmentNamed(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return Segments.FirstOrDefault(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
