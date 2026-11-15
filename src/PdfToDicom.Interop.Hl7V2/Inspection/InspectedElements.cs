using PdfToDicom.Interop.Hl7V2.Profiles;

namespace PdfToDicom.Interop.Hl7V2.Inspection;

/// <summary>Why a field appears in an inspection.</summary>
public enum InspectedFieldOrigin
{
    /// <summary>It is in the message and the profile says nothing about it.</summary>
    InMessageOnly = 0,

    /// <summary>It is in the message and the profile describes it.</summary>
    InBoth = 1,

    /// <summary>
    /// The profile describes it and the message does not have it.
    /// </summary>
    /// <remarks>
    /// The reason an inspection is built by merging rather than by walking the message. A
    /// missing mandatory field is invisible in the message itself, and finding it is the
    /// single thing somebody opens this screen to do.
    /// </remarks>
    InProfileOnly = 2
}

/// <summary>One field, as somebody looking at a message needs to see it.</summary>
/// <param name="Path">Where it is, written the way a conformance statement writes it.</param>
/// <param name="Name">What the profile calls it, or empty when the profile is silent.</param>
/// <param name="Value">The decoded value, empty when there is none.</param>
/// <param name="Usage">What the profile requires, or optional when it says nothing.</param>
/// <param name="Origin">Whether it came from the message, the profile, or both.</param>
/// <param name="Issues">Anything wrong with this particular field.</param>
public sealed record InspectedField(
    string Path,
    string Name,
    string Value,
    ProfileUsage Usage,
    InspectedFieldOrigin Origin,
    IReadOnlyList<Hl7MessageIssue> Issues)
{
    /// <summary>True when the field carries nothing.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>True when this is a mandatory field with nothing in it.</summary>
    public bool IsMissingAndRequired => Usage == ProfileUsage.Required && IsEmpty;

    /// <summary>True when something here would stop the message being sent.</summary>
    public bool HasError => Issues.Any(i => i.Severity == ProfileIssueSeverity.Error);
}

/// <summary>One segment of a message, with its fields.</summary>
/// <param name="Name">The three character name.</param>
/// <param name="Occurrence">Which occurrence of it, counting from one.</param>
/// <param name="Usage">What the profile requires of it.</param>
/// <param name="Group">The segment group the profile puts it in, for orientation.</param>
/// <param name="IsPresent">Whether the message actually carries it.</param>
/// <param name="Fields">Its fields, in order.</param>
/// <param name="Issues">Anything wrong with the segment as a whole.</param>
public sealed record InspectedSegment(
    string Name,
    int Occurrence,
    ProfileUsage Usage,
    string Group,
    bool IsPresent,
    IReadOnlyList<InspectedField> Fields,
    IReadOnlyList<Hl7MessageIssue> Issues)
{
    /// <summary>True when something here would stop the message being sent.</summary>
    public bool HasError =>
        Issues.Any(i => i.Severity == ProfileIssueSeverity.Error) || Fields.Any(f => f.HasError);

    /// <summary>A label for a tree: "PID" or "OBX (2)".</summary>
    public string Label => Occurrence > 1 ? $"{Name} ({Occurrence})" : Name;
}

/// <summary>A whole message, laid out for somebody to look at.</summary>
/// <param name="Segments">Every segment, present or merely expected.</param>
/// <param name="Issues">Everything wrong, in one list.</param>
/// <param name="Er7">Exactly what would go on the wire.</param>
public sealed record MessageInspection(
    IReadOnlyList<InspectedSegment> Segments,
    IReadOnlyList<Hl7MessageIssue> Issues,
    string Er7)
{
    /// <summary>Whether the message satisfies the profile it was checked against.</summary>
    public bool IsSendable => !Issues.Any(i => i.Severity == ProfileIssueSeverity.Error);

    /// <summary>
    /// The mandatory fields that are empty, which is what this screen is opened to find.
    /// </summary>
    public IReadOnlyList<InspectedField> MissingMandatoryFields =>
        Segments.SelectMany(s => s.Fields).Where(f => f.IsMissingAndRequired).ToList();

    /// <summary>The errors on their own.</summary>
    public IReadOnlyList<Hl7MessageIssue> Errors =>
        Issues.Where(i => i.Severity == ProfileIssueSeverity.Error).ToList();
}
