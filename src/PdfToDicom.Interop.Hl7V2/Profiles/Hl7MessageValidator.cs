using PdfToDicom.Interop.Hl7V2.Model;

namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// Checks a message against the profile it claims to follow.
/// </summary>
/// <remarks>
/// <para>
/// Used on the way out, before anything is transmitted, and on a message somebody pasted
/// into the inspector to ask why a receiver rejected it. The same rules answer both
/// questions, which is the reason it is a separate type from the builder.
/// </para>
/// <para>
/// It reports; it never repairs. A validator that quietly corrected a message would make
/// the preview a lie, and the whole value of showing somebody what will go on the wire is
/// that it is what will go on the wire.
/// </para>
/// </remarks>
public static class Hl7MessageValidator
{
    /// <summary>Checks a message and lists everything wrong with it.</summary>
    public static IReadOnlyList<Hl7MessageIssue> Validate(Hl7Message message, Hl7Profile profile)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(profile);

        var issues = new List<Hl7MessageIssue>();

        CheckSegments(message, profile, issues);
        CheckFields(message, profile, issues);

        return issues;
    }

    private static void CheckSegments(
        Hl7Message message, Hl7Profile profile, List<Hl7MessageIssue> issues)
    {
        foreach (ProfileSegment segment in profile.Segments)
        {
            int present = message.FindSegments(segment.Id).Count;

            switch (segment.Usage)
            {
                case ProfileUsage.NotSupported when present > 0:
                    issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, segment.Id,
                        "This destination does not accept this segment, and the message " +
                        $"carries {present} of them."));
                    break;

                case ProfileUsage.Required when present == 0:
                    issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, segment.Id,
                        "This segment is required and the message does not have one."));

                    // Reported once. The minimum below says the same thing in other words,
                    // and two complaints about one absence bury whichever came first.
                    continue;
            }

            if (segment.Usage != ProfileUsage.NotSupported && present < segment.Min)
            {
                issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, segment.Id,
                    $"At least {segment.Min} of these is required and the message has {present}."));
            }

            if (!segment.IsUnbounded && present > segment.Max)
            {
                issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, segment.Id,
                    $"At most {segment.Max} of these is accepted and the message has {present}."));
            }
        }
    }

    private static void CheckFields(
        Hl7Message message, Hl7Profile profile, List<Hl7MessageIssue> issues)
    {
        foreach (ProfileField field in profile.Fields)
        {
            if (!Hl7Path.TryParse(field.Path, out Hl7Path? path))
            {
                continue;
            }

            // A field inside a segment the message does not carry is not a separate
            // problem. The missing segment has already been reported, and repeating it once
            // per field would bury it.
            if (message.FindSegment(path.Segment, path.SegmentOccurrence) is null)
            {
                continue;
            }

            string value = message.Get(path);

            switch (field.Usage)
            {
                case ProfileUsage.Required when string.IsNullOrEmpty(value):
                    issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, field.Path,
                        string.IsNullOrWhiteSpace(field.Name)
                            ? "This field is required and is empty."
                            : $"{field.Name} is required by this destination and is empty."));
                    continue;

                case ProfileUsage.NotSupported when !string.IsNullOrEmpty(value):
                    issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, field.Path,
                        "This destination does not accept this field, and it has a value."));
                    continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (field.MaxLength > 0 && value.Length > field.MaxLength)
            {
                // Not truncated here. Shortening a patient's name without being asked is
                // precisely the silent alteration this product refuses; a profile that
                // wants it says so with a transform.
                issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, field.Path,
                    $"This destination accepts {field.MaxLength} characters and the value " +
                    $"is {value.Length}. Add a truncate transform to the mapping if " +
                    "shortening it is acceptable."));
            }

            if (field.AllowedValues.Count > 0 &&
                !field.AllowedValues.Contains(value, StringComparer.Ordinal))
            {
                issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, field.Path,
                    $"'{value}' is not one of the values this destination accepts here: " +
                    string.Join(", ", field.AllowedValues.Select(v => $"'{v}'")) + "."));
            }
        }
    }
}
