using PdfToDicom.Interop.Hl7V2.Er7;
using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Hl7V2.Profiles;

namespace PdfToDicom.Interop.Hl7V2.Inspection;

/// <summary>
/// Lays a message out against a profile so that somebody can see what is in it, what is
/// missing from it, and why a receiver might refuse it.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of the message editor that can be tested. The window above it binds to
/// what this produces and adds no rules of its own, which matters in a product whose
/// interface has no automated coverage at all: a defect in the tree somebody reads while
/// commissioning an interface would otherwise be found by that person, at a site.
/// </para>
/// <para>
/// The inspection is built by merging the message with the profile rather than by walking
/// the message. A missing mandatory field is, by definition, not in the message, and
/// finding one is the single reason this screen exists.
/// </para>
/// </remarks>
public static class Hl7MessageInspector
{
    /// <summary>Lays out a message against the profile it claims to follow.</summary>
    public static MessageInspection Inspect(Hl7Message message, Hl7Profile profile)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(profile);

        IReadOnlyList<Hl7MessageIssue> issues = Hl7MessageValidator.Validate(message, profile);

        var segments = new List<InspectedSegment>();

        foreach (string name in SegmentOrder(message, profile))
        {
            ProfileSegment? rules = profile.SegmentNamed(name);
            IReadOnlyList<Hl7Segment> present = message.FindSegments(name);

            if (present.Count == 0)
            {
                // Expected and absent. Shown so that its absence is visible rather than
                // inferred from a list that simply does not mention it.
                segments.Add(new InspectedSegment(
                    name, 1, rules?.Usage ?? ProfileUsage.Optional, rules?.Group ?? string.Empty,
                    IsPresent: false,
                    Fields: FieldsOf(message, profile, name, occurrence: 1, segment: null, issues),
                    Issues: IssuesFor(issues, name)));

                continue;
            }

            for (int occurrence = 1; occurrence <= present.Count; occurrence++)
            {
                segments.Add(new InspectedSegment(
                    name, occurrence, rules?.Usage ?? ProfileUsage.Optional,
                    rules?.Group ?? string.Empty,
                    IsPresent: true,
                    Fields: FieldsOf(
                        message, profile, name, occurrence, present[occurrence - 1], issues),
                    // A complaint about the segment belongs on its first occurrence, since
                    // cardinality is a statement about all of them together.
                    Issues: occurrence == 1 ? IssuesFor(issues, name) : []));
            }
        }

        return new MessageInspection(segments, issues, message.ToEr7());
    }

    /// <summary>
    /// Says what two profiles disagree about for the same message.
    /// </summary>
    /// <remarks>
    /// Used when an interface is being commissioned and nobody is sure which of two
    /// descriptions is the real one. A difference is far more informative than either list
    /// on its own, because the overlap is almost always the uninteresting part.
    /// </remarks>
    public static ProfileComparison Compare(Hl7Message message, Hl7Profile left, Hl7Profile right)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        MessageInspection first = Inspect(message, left);
        MessageInspection second = Inspect(message, right);

        static string Key(Hl7MessageIssue issue) => $"{issue.Location}|{issue.Message}";

        var inSecond = second.Issues.Select(Key).ToHashSet(StringComparer.Ordinal);
        var inFirst = first.Issues.Select(Key).ToHashSet(StringComparer.Ordinal);

        return new ProfileComparison(
            left.ProfileId,
            right.ProfileId,
            first.Issues.Where(i => !inSecond.Contains(Key(i))).ToList(),
            second.Issues.Where(i => !inFirst.Contains(Key(i))).ToList(),
            first.IsSendable,
            second.IsSendable);
    }

    /// <summary>
    /// The segments to show, in the profile's order first and then anything the message
    /// carries that the profile never mentioned.
    /// </summary>
    /// <remarks>
    /// The profile's order is used because that is the order the vendor's document uses,
    /// and somebody reading this screen almost always has that document open. Segments the
    /// profile does not describe go last rather than being hidden: an unexpected segment in
    /// a message is exactly the sort of thing worth seeing.
    /// </remarks>
    private static IEnumerable<string> SegmentOrder(Hl7Message message, Hl7Profile profile)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProfileSegment segment in profile.Segments)
        {
            if (seen.Add(segment.Id))
            {
                yield return segment.Id;
            }
        }

        foreach (Hl7Segment segment in message.Segments)
        {
            if (seen.Add(segment.Name))
            {
                yield return segment.Name;
            }
        }
    }

    private static IReadOnlyList<InspectedField> FieldsOf(
        Hl7Message message,
        Hl7Profile profile,
        string segmentName,
        int occurrence,
        Hl7Segment? segment,
        IReadOnlyList<Hl7MessageIssue> issues)
    {
        var fields = new List<InspectedField>();
        var described = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // What the profile describes, whether or not the message has it.
        foreach (ProfileField rules in profile.Fields)
        {
            if (!Hl7Path.TryParse(rules.Path, out Hl7Path? path) ||
                !path.Segment.Equals(segmentName, StringComparison.OrdinalIgnoreCase) ||
                path.SegmentOccurrence != occurrence)
            {
                continue;
            }

            described.Add(path.ToString());

            // A profile path that names a component is read as that component; one that
            // names a whole field is shown whole. Reading MSH-9 as its first component
            // would report "ORU" for a field that carries "ORU^R01^ORU_R01".
            string value = segment is null
                ? string.Empty
                : NamesAComponent(rules.Path)
                    ? message.Get(path)
                    : Er7Codec.WriteField(segment.PeekField(path.Field), message.Delimiters);

            fields.Add(new InspectedField(
                rules.Path,
                rules.Name,
                value,
                rules.Usage,
                segment is null || string.IsNullOrEmpty(value)
                    ? InspectedFieldOrigin.InProfileOnly
                    : InspectedFieldOrigin.InBoth,
                IssuesFor(issues, rules.Path)));
        }

        // What the message carries that the profile never mentioned.
        if (segment is not null)
        {
            for (int position = 1; position <= segment.FieldCount; position++)
            {
                string path = $"{segmentName}{(occurrence > 1 ? $"[{occurrence}]" : string.Empty)}-{position}";
                Hl7Field? field = segment.PeekField(position);

                if (field is null || field.IsEmpty || described.Contains(path))
                {
                    continue;
                }

                // MSH-1 and MSH-2 are the delimiters themselves. Shown, because somebody
                // debugging an encoding problem needs them, and never flagged, because they
                // are not values anybody configured. Shown raw, because encoding them would
                // render the field separator as the escape sequence that stands for it and
                // make the one field whose literal characters are the point unreadable.
                bool isDelimiterField =
                    segmentName.Equals("MSH", StringComparison.OrdinalIgnoreCase) && position <= 2;

                fields.Add(new InspectedField(
                    path, string.Empty,
                    isDelimiterField ? field.Value : Er7Codec.WriteField(field, message.Delimiters),
                    ProfileUsage.Optional,
                    InspectedFieldOrigin.InMessageOnly, IssuesFor(issues, path)));
            }
        }

        return fields;
    }

    /// <summary>Whether a profile path addresses a component rather than a whole field.</summary>
    /// <remarks>
    /// Asked of the written path and not of the parsed one, because a parsed path reports
    /// component 1 whether or not a component was written.
    /// </remarks>
    private static bool NamesAComponent(string path)
    {
        int dash = path.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 && path.IndexOf('.', dash) > dash;
    }

    private static IReadOnlyList<Hl7MessageIssue> IssuesFor(
        IReadOnlyList<Hl7MessageIssue> issues, string location) =>
        issues.Where(i => string.Equals(i.Location, location, StringComparison.OrdinalIgnoreCase))
              .ToList();
}

/// <summary>What two profiles say differently about one message.</summary>
/// <param name="LeftProfileId">The first profile.</param>
/// <param name="RightProfileId">The second.</param>
/// <param name="OnlyLeftObjects">What the first objects to and the second does not.</param>
/// <param name="OnlyRightObjects">What the second objects to and the first does not.</param>
/// <param name="LeftAccepts">Whether the first would let the message be sent.</param>
/// <param name="RightAccepts">Whether the second would.</param>
public sealed record ProfileComparison(
    string LeftProfileId,
    string RightProfileId,
    IReadOnlyList<Hl7MessageIssue> OnlyLeftObjects,
    IReadOnlyList<Hl7MessageIssue> OnlyRightObjects,
    bool LeftAccepts,
    bool RightAccepts)
{
    /// <summary>True when the two profiles treat this message identically.</summary>
    public bool Agree => OnlyLeftObjects.Count == 0 && OnlyRightObjects.Count == 0;
}
