using System.Text.Json;
using PdfToDicom.Interop.Hl7V2.Encoding;
using PdfToDicom.Interop.Hl7V2.Model;

namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// Reads a profile from JSON and says plainly what is wrong with it.
/// </summary>
/// <remarks>
/// <para>
/// The checking is written out here rather than delegated to a JSON schema library, and
/// that was a deliberate trade. A schema would catch the shape of the document and would
/// then be a second artefact to keep in step with these types, plus a dependency. It also
/// cannot express the checks that matter most, which are not about shape at all: that a
/// mapping does not fill a segment the receiver has marked as not supported, that a
/// transform is one this build knows, that a named code table exists.
/// </para>
/// <para>
/// Everything is checked at load, never at send. A profile with a mistake in it should be
/// refused while an administrator is looking at it, not when a report is waiting to go.
/// </para>
/// </remarks>
public static class Hl7ProfileReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new ProfileUsageConverter() }
    };

    /// <summary>Reads a profile and checks it.</summary>
    public static ProfileReadResult Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Hl7Profile profile;

        try
        {
            profile = JsonSerializer.Deserialize<Hl7Profile>(json, Options)
                      ?? throw new JsonException("The document was empty.");
        }
        catch (JsonException ex)
        {
            return new ProfileReadResult(null,
            [
                new ProfileIssue(ProfileIssueSeverity.Error, "the file",
                    $"The profile could not be read as JSON: {ex.Message}")
            ]);
        }

        return new ProfileReadResult(profile, Check(profile));
    }

    /// <summary>Checks an already constructed profile.</summary>
    public static IReadOnlyList<ProfileIssue> Check(Hl7Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var issues = new List<ProfileIssue>();

        CheckIdentity(profile, issues);
        CheckEncoding(profile, issues);
        CheckAcknowledgement(profile, issues);
        CheckSegments(profile, issues);
        CheckFields(profile, issues);
        CheckMappings(profile, issues);

        return issues;
    }

    private static void CheckIdentity(Hl7Profile profile, List<ProfileIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(profile.ProfileId))
        {
            Error(issues, "profileId", "A profile needs an identifier, which routing rules refer to.");
        }

        if (string.IsNullOrWhiteSpace(profile.MessageType))
        {
            Error(issues, "messageType", "A profile needs a message type, for example ORU.");
        }

        if (string.IsNullOrWhiteSpace(profile.TriggerEvent))
        {
            Error(issues, "triggerEvent", "A profile needs a trigger event, for example R01.");
        }

        if (profile.Revision < 1)
        {
            Error(issues, "revision", "Revisions are counted from one.");
        }
    }

    private static void CheckEncoding(Hl7Profile profile, List<ProfileIssue> issues)
    {
        ProfileEncoding encoding = profile.Encoding;

        if (encoding.FieldSeparator.Length != 1)
        {
            Error(issues, "encoding.fieldSeparator", "The field separator is one character.");
        }

        if (encoding.EncodingCharacters.Length != 4)
        {
            Error(issues, "encoding.encodingCharacters",
                "There are exactly four encoding characters, in the order component, " +
                "repetition, escape, subcomponent.");
        }

        if (encoding.FieldSeparator.Length == 1 && encoding.EncodingCharacters.Length == 4)
        {
            try
            {
                _ = new Hl7Delimiters(
                    encoding.FieldSeparator[0],
                    encoding.EncodingCharacters[0],
                    encoding.EncodingCharacters[1],
                    encoding.EncodingCharacters[2],
                    encoding.EncodingCharacters[3]);
            }
            catch (ArgumentException ex)
            {
                Error(issues, "encoding", ex.Message);
            }
        }

        if (!Hl7CharacterSet.TryResolve(encoding.CharacterSet, out _))
        {
            Error(issues, "encoding.characterSet",
                $"'{encoding.CharacterSet}' is not a character set this build supports. " +
                "It supports ASCII, 8859/1 and UNICODE UTF-8.");
        }

        if (!IsOneOf(encoding.OnUnrepresentableCharacter, "refuse", "transliterate"))
        {
            Error(issues, "encoding.onUnrepresentableCharacter",
                "Write either 'refuse' or 'transliterate'.");
        }

        if (IsOneOf(encoding.OnUnrepresentableCharacter, "transliterate"))
        {
            // Not an error, because a site may have decided it. Not silent either, because
            // it means patient names may leave here different from how they were typed.
            Warn(issues, "encoding.onUnrepresentableCharacter",
                "This destination will have accents stripped from any value its character " +
                "set cannot carry. The DICOM objects keep the original spelling, so the two " +
                "systems will hold different names for the same patient.");
        }
    }

    private static void CheckAcknowledgement(Hl7Profile profile, List<ProfileIssue> issues)
    {
        if (!IsOneOf(profile.Acknowledgement.Expect, "none", "accept", "application"))
        {
            Error(issues, "acknowledgement.expect",
                "Write 'none', 'accept' or 'application'.");
        }

        if (profile.Acknowledgement.TimeoutSeconds < 1)
        {
            Error(issues, "acknowledgement.timeoutSeconds", "A timeout is at least one second.");
        }
    }

    private static void CheckSegments(Hl7Profile profile, List<ProfileIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProfileSegment segment in profile.Segments)
        {
            string where = $"segment {segment.Id}";

            if (segment.Id.Length != 3)
            {
                Error(issues, where, "A segment name is exactly three characters.");
                continue;
            }

            if (!seen.Add(segment.Id))
            {
                Error(issues, where, "The same segment is listed twice.");
            }

            if (segment.Min < 0)
            {
                Error(issues, where, "A minimum of less than zero means nothing.");
            }

            if (!segment.IsUnbounded && segment.Max < segment.Min)
            {
                Error(issues, where, $"The maximum ({segment.Max}) is below the minimum ({segment.Min}).");
            }

            if (segment.Usage == ProfileUsage.Required && segment.Min == 0)
            {
                Warn(issues, where,
                    "Marked required but allowed to occur zero times, which cannot both be " +
                    "true. The usage is what the validator will enforce.");
            }

            if (segment.Usage == ProfileUsage.NotSupported && segment.Min > 0)
            {
                Error(issues, where,
                    "Marked not supported and also required to occur. One of the two is wrong.");
            }
        }
    }

    private static void CheckFields(Hl7Profile profile, List<ProfileIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProfileField field in profile.Fields)
        {
            string where = $"field {field.Path}";

            if (!Hl7Path.TryParse(field.Path, out Hl7Path? path))
            {
                Error(issues, where,
                    "This is not an HL7 path. Expected something like PID-5.1.1 or OBX[2]-5.");
                continue;
            }

            if (!seen.Add(path.ToString()))
            {
                Error(issues, where, "The same field is described twice.");
            }

            if (field.MaxLength < 0)
            {
                Error(issues, where, "A maximum length of less than zero means nothing.");
            }

            if (profile.SegmentNamed(path.Segment) is { Usage: ProfileUsage.NotSupported })
            {
                Error(issues, where,
                    $"The {path.Segment} segment is marked as not supported, so describing " +
                    "one of its fields cannot be right.");
            }
        }
    }

    private static void CheckMappings(Hl7Profile profile, List<ProfileIssue> issues)
    {
        foreach (ProfileMapping mapping in profile.Mappings)
        {
            string where = $"mapping to {mapping.Target}";

            if (!Hl7Path.TryParse(mapping.Target, out Hl7Path? target))
            {
                Error(issues, where, "The target is not an HL7 path.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.Source) && string.IsNullOrEmpty(mapping.Value))
            {
                Error(issues, where,
                    "A mapping needs either a source to read from or a fixed value to write.");
            }

            if (!string.IsNullOrWhiteSpace(mapping.Source) && !string.IsNullOrEmpty(mapping.Value))
            {
                Warn(issues, where,
                    "Both a source and a fixed value are given. The source wins, and the " +
                    "fixed value is used only when the source is empty.");
            }

            if (!Hl7Transforms.TryValidate(mapping.Transform, out string transformError))
            {
                Error(issues, where, transformError);
            }

            if (!string.IsNullOrWhiteSpace(mapping.Table) && !profile.Tables.ContainsKey(mapping.Table))
            {
                Error(issues, where,
                    $"There is no code table named '{mapping.Table}' in this profile.");
            }

            // The check worth having most: a value configured into something the receiver
            // has said it will not accept.
            if (profile.SegmentNamed(target.Segment) is { Usage: ProfileUsage.NotSupported })
            {
                Error(issues, where,
                    $"The {target.Segment} segment is marked as not supported by this " +
                    "destination, so nothing may be mapped into it.");
            }

            if (profile.FieldAt(target.ToString()) is { Usage: ProfileUsage.NotSupported })
            {
                Error(issues, where,
                    "This field is marked as not supported by this destination, so nothing " +
                    "may be mapped into it.");
            }
        }
    }

    private static bool IsOneOf(string value, params string[] allowed) =>
        allowed.Contains(value?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    private static void Error(List<ProfileIssue> issues, string where, string message) =>
        issues.Add(new ProfileIssue(ProfileIssueSeverity.Error, where, message));

    private static void Warn(List<ProfileIssue> issues, string where, string message) =>
        issues.Add(new ProfileIssue(ProfileIssueSeverity.Warning, where, message));
}
