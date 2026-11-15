using System.Globalization;
using System.Text;

namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// The complete set of things a mapping may do to a value, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a closed list on purpose, and the obvious alternative was rejected.</b> An
/// expression language in the configuration would make any transformation possible without
/// a release, which sounds like exactly what "configurable without changing source code"
/// asks for. It is also an injection surface in software that handles patient data, and a
/// change path nobody reviews: a profile edited at a site could then alter behaviour in
/// ways no test covers and no log explains.
/// </para>
/// <para>
/// A fixed vocabulary keeps the promise honestly. Every transformation that runs is one
/// that was written here, read by somebody, and covered by a test. Where a site needs
/// something this list does not have, adding it is a small, tested change to a named list
/// rather than a new language, and the list grows by evidence rather than by anticipation.
/// </para>
/// <para>
/// Transforms chain with a semicolon: <c>trim;truncate:30;upper</c>. A vertical bar would
/// have been the natural choice and is avoided deliberately, because in a file about HL7 it
/// already means a field separator and would be read as one by anybody skimming.
/// </para>
/// </remarks>
public static class Hl7Transforms
{
    /// <summary>The names this build understands, for documentation and for error messages.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "trim", "upper", "lower", "digitsOnly",
        "truncate", "padLeft", "padRight", "substring",
        "hl7Date", "hl7DateTime", "hl7Time",
        "escapeNewlines", "base64"
    ];

    /// <summary>
    /// Checks a transform specification without running it, so that a profile with a
    /// mistake in it is refused when it is loaded rather than when a report is waiting.
    /// </summary>
    public static bool TryValidate(string? specification, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(specification))
        {
            return true;
        }

        foreach (string step in Split(specification))
        {
            string[] parts = step.Split(':');
            string name = parts[0];

            if (!Names.Contains(name, StringComparer.Ordinal))
            {
                error =
                    $"'{name}' is not a transform this build knows. The ones it knows are: " +
                    string.Join(", ", Names) + ".";
                return false;
            }

            if (!ArgumentsAreValid(name, parts, out error))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Applies a specification to a value.</summary>
    /// <exception cref="ArgumentException">The specification is not valid.</exception>
    public static string Apply(string? specification, string? value)
    {
        if (!TryValidate(specification, out string error))
        {
            throw new ArgumentException(error, nameof(specification));
        }

        string current = value ?? string.Empty;

        if (string.IsNullOrWhiteSpace(specification))
        {
            return current;
        }

        foreach (string step in Split(specification))
        {
            string[] parts = step.Split(':');
            current = ApplyOne(parts, current);
        }

        return current;
    }

    private static IEnumerable<string> Split(string specification) =>
        specification.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ArgumentsAreValid(string name, string[] parts, out string error)
    {
        error = string.Empty;

        switch (name)
        {
            case "truncate":
            case "padLeft":
            case "padRight":
                if (parts.Length < 2 || !int.TryParse(parts[1], out int length) || length < 1)
                {
                    error = $"'{name}' needs a length, for example '{name}:30'.";
                    return false;
                }

                if (name != "truncate" && parts.Length > 2 && parts[2].Length != 1)
                {
                    error = $"'{name}' pads with one character, for example '{name}:10:0'.";
                    return false;
                }

                return true;

            case "substring":
                if (parts.Length < 2 || !int.TryParse(parts[1], out int start) || start < 0)
                {
                    error = "'substring' needs a start, for example 'substring:0:4'.";
                    return false;
                }

                if (parts.Length > 2 && (!int.TryParse(parts[2], out int take) || take < 0))
                {
                    error = "'substring' takes a length that is not negative.";
                    return false;
                }

                return true;

            default:
                return true;
        }
    }

    private static string ApplyOne(string[] parts, string value)
    {
        string name = parts[0];
        string Argument(int index, string fallback) =>
            parts.Length > index ? parts[index] : fallback;

        return name switch
        {
            "trim" => value.Trim(),
            "upper" => value.ToUpperInvariant(),
            "lower" => value.ToLowerInvariant(),
            "digitsOnly" => new string(value.Where(char.IsDigit).ToArray()),

            // Truncation shortens; it never pads. A value shorter than the limit is already
            // acceptable to the receiver and lengthening it would invent content.
            "truncate" => Truncate(value, int.Parse(Argument(1, "0"))),

            "padLeft" => value.PadLeft(int.Parse(Argument(1, "0")), Argument(2, "0")[0]),
            "padRight" => value.PadRight(int.Parse(Argument(1, "0")), Argument(2, "0")[0]),
            "substring" => Substring(value, int.Parse(Argument(1, "0")), Argument(2, "")),

            "hl7Date" => FormatDate(value, "yyyyMMdd"),
            "hl7DateTime" => FormatDate(value, "yyyyMMddHHmmss"),
            "hl7Time" => FormatDate(value, "HHmmss"),

            "escapeNewlines" => EscapeNewlines(value),
            "base64" => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)),

            _ => value
        };
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private static string Substring(string value, int start, string take)
    {
        if (start >= value.Length)
        {
            return string.Empty;
        }

        string rest = value[start..];

        if (string.IsNullOrEmpty(take))
        {
            return rest;
        }

        int count = int.Parse(take);
        return rest.Length <= count ? rest : rest[..count];
    }

    /// <summary>
    /// Formats a date or time that arrived as text.
    /// </summary>
    /// <remarks>
    /// A value that cannot be read as a date is returned unchanged rather than replaced
    /// with today, with an empty string, or with anything else invented. The validator sees
    /// a value that does not match the field's rules and refuses the message, which puts a
    /// human in front of the problem. Substituting a plausible date would put a wrong one
    /// in a patient's record and nobody would ever know.
    /// </remarks>
    private static string FormatDate(string value, string format)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();

        if (DateTimeOffset.TryParse(
                trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
        {
            return parsed.ToString(format, CultureInfo.InvariantCulture);
        }

        // Already in an HL7 form, which is the ordinary case when a value is copied from one
        // message to another.
        foreach (string hl7Format in new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd" })
        {
            if (DateTime.TryParseExact(
                    trimmed, hl7Format, CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out DateTime exact))
            {
                return exact.ToString(format, CultureInfo.InvariantCulture);
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Turns line breaks into the HL7 formatting command for one, so that a report keeps
    /// its shape inside a single field.
    /// </summary>
    private static string EscapeNewlines(string value)
    {
        var builder = new StringBuilder(value.Length);

        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];

            if (current == '\r')
            {
                builder.Append("\\.br\\");

                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '\n')
            {
                builder.Append("\\.br\\");
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
