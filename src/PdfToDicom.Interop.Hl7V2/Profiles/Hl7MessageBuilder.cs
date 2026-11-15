using PdfToDicom.Interop.Hl7V2.Encoding;
using PdfToDicom.Interop.Hl7V2.Model;
using PdfToDicom.Interop.Messaging;

namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// Turns a profile and a subject into a message, and says what is wrong with the result.
/// </summary>
/// <remarks>
/// <para>
/// The builder reads the profile and nothing else. There is no branch here on message type,
/// no knowledge that PID holds a patient, and no segment name written in this file except
/// MSH, which the standard fixes and which carries the profile's own declarations.
/// </para>
/// <para>
/// It always returns a message, even one it found errors in. The editor an administrator
/// uses to find a missing mandatory field has to show them something, and a builder that
/// returned nothing would make the most useful screen in the product impossible. Nothing is
/// transmitted on that basis: the transmitting code asks
/// <see cref="Hl7BuildResult.IsSendable"/>, never whether a message exists.
/// </para>
/// </remarks>
public sealed class Hl7MessageBuilder
{
    private readonly Hl7ControlIdGenerator _controlIds;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a builder, optionally with a clock and generator of your choosing.</summary>
    public Hl7MessageBuilder(
        Hl7ControlIdGenerator? controlIds = null, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _controlIds = controlIds ?? new Hl7ControlIdGenerator(_clock);
    }

    /// <summary>Builds a message for one subject against one profile.</summary>
    public Hl7BuildResult Build(Hl7Profile profile, MessageSubject subject)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(subject);

        var issues = new List<Hl7MessageIssue>();

        if (!Hl7CharacterSet.TryResolve(profile.Encoding.CharacterSet, out System.Text.Encoding? charset))
        {
            issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, "encoding.characterSet",
                $"'{profile.Encoding.CharacterSet}' is not a character set this build supports."));

            return new Hl7BuildResult(null, issues);
        }

        var message = new Hl7Message(DelimitersFor(profile));

        WriteHeader(message, profile);
        ApplyMappings(message, profile, subject, charset, issues);

        issues.AddRange(Hl7MessageValidator.Validate(message, profile));

        return new Hl7BuildResult(message, issues);
    }

    private static Hl7Delimiters DelimitersFor(Hl7Profile profile)
    {
        ProfileEncoding encoding = profile.Encoding;

        // A profile that got this far has been checked, so the characters are known good.
        return new Hl7Delimiters(
            encoding.FieldSeparator[0],
            encoding.EncodingCharacters[0],
            encoding.EncodingCharacters[1],
            encoding.EncodingCharacters[2],
            encoding.EncodingCharacters[3]);
    }

    /// <summary>
    /// Writes the header fields the profile itself determines.
    /// </summary>
    /// <remarks>
    /// Written before the mappings run, so that a profile which wants something different
    /// in one of them can simply map it and win. The alternative, refusing to let a mapping
    /// touch the header, would mean a site needing a different sending facility had to wait
    /// for a release.
    /// </remarks>
    private void WriteHeader(Hl7Message message, Hl7Profile profile)
    {
        message["MSH-7"] = _clock().ToString("yyyyMMddHHmmss");
        message["MSH-9.1"] = profile.MessageType;
        message["MSH-9.2"] = profile.TriggerEvent;

        if (!string.IsNullOrWhiteSpace(profile.MessageStructure))
        {
            message["MSH-9.3"] = profile.MessageStructure;
        }

        message["MSH-10"] = _controlIds.Next(profile.ControlIdPrefix);
        message["MSH-11"] = profile.ProcessingId;
        message["MSH-12"] = profile.Hl7Version;
        message["MSH-15"] = profile.Acknowledgement.AcceptAckType;
        message["MSH-16"] = profile.Acknowledgement.ApplicationAckType;

        if (!string.IsNullOrWhiteSpace(profile.Encoding.CharacterSet))
        {
            message["MSH-18"] = profile.Encoding.CharacterSet;
        }
    }

    private void ApplyMappings(
        Hl7Message message,
        Hl7Profile profile,
        MessageSubject subject,
        System.Text.Encoding charset,
        List<Hl7MessageIssue> issues)
    {
        bool transliterate = string.Equals(
            profile.Encoding.OnUnrepresentableCharacter, "transliterate",
            StringComparison.OrdinalIgnoreCase);

        foreach (ProfileMapping mapping in profile.Mappings)
        {
            string value = Resolve(mapping, subject);
            value = Hl7Transforms.Apply(mapping.Transform, value);
            value = Translate(mapping, profile, value);

            if (string.IsNullOrEmpty(value))
            {
                if (mapping.Required)
                {
                    issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, mapping.Target,
                        $"This destination requires a value here and none was available" +
                        (string.IsNullOrWhiteSpace(mapping.Source)
                            ? "."
                            : $", because '{mapping.Source}' is empty.")));
                }

                // An empty optional value is left out rather than written as an empty
                // field, so the message carries only what it actually has.
                continue;
            }

            char? unrepresentable = Hl7CharacterSet.FindUnrepresentable(value, charset);

            if (unrepresentable is not null)
            {
                if (!transliterate)
                {
                    issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Error, mapping.Target,
                        $"'{value}' cannot be written in {profile.Encoding.CharacterSet}: the " +
                        $"character '{unrepresentable}' has no representation in it. The " +
                        "message was not built rather than built with the name altered."));

                    continue;
                }

                string stripped = Hl7CharacterSet.ToRepresentable(value, charset);

                issues.Add(new Hl7MessageIssue(ProfileIssueSeverity.Warning, mapping.Target,
                    $"'{value}' was written as '{stripped}' because " +
                    $"{profile.Encoding.CharacterSet} cannot carry it. The DICOM object keeps " +
                    "the original spelling, so the two systems now hold different values."));

                value = stripped;
            }

            message[mapping.Target] = value;
        }
    }

    private static string Resolve(ProfileMapping mapping, MessageSubject subject)
    {
        if (string.IsNullOrWhiteSpace(mapping.Source))
        {
            return mapping.Value;
        }

        string fromSubject = subject.Get(mapping.Source);

        // A fixed value beside a source is a default, not a competitor. The profile reader
        // warns that this is what it means, so nobody has to guess.
        return string.IsNullOrEmpty(fromSubject) ? mapping.Value : fromSubject;
    }

    private static string Translate(ProfileMapping mapping, Hl7Profile profile, string value)
    {
        if (string.IsNullOrWhiteSpace(mapping.Table) ||
            !profile.Tables.TryGetValue(mapping.Table, out IReadOnlyDictionary<string, string>? table))
        {
            return value;
        }

        // A value the table does not mention passes through unchanged rather than becoming
        // empty. Emptying it would quietly drop information; passing it through leaves the
        // field validator to object if the receiver will not accept it, which names the
        // field and the value.
        return table.TryGetValue(value, out string? translated) ? translated : value;
    }
}
