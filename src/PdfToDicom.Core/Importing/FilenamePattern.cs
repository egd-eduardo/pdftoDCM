using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfToDicom.Core.Importing;

/// <summary>
/// One filename layout, written as a template of tokens and literal separators.
/// </summary>
/// <remarks>
/// <para>
/// A template looks like <c>{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}</c>. Whatever
/// sits between the tokens is a literal: an underscore, a hyphen, a space, several
/// characters, anything. The extension is not part of the template and is removed before
/// matching.
/// </para>
/// <para>
/// <b>A template is compiled to a regular expression rather than split on separators.</b>
/// Splitting looks simpler and then fails on the first real filename, because the fields
/// themselves contain the separator: "SILVA ANA" holds a space and "MARIA DO CARMO" holds
/// two. A regular expression with a lazy match per token and anchors at both ends resolves
/// that the way a reader would.
/// </para>
/// <para>
/// <b>Why not just let a site write the regular expression.</b> Because the people who
/// will configure this are configuring a PACS, not writing code, and a template is
/// something they can get right from an example. A site that genuinely needs a regular
/// expression can still supply one, since a template with no tokens in it is rejected and
/// a pattern beginning <c>regex:</c> is taken verbatim.
/// </para>
/// </remarks>
public sealed class FilenamePattern
{
    /// <summary>Prefix that makes a pattern a regular expression rather than a template.</summary>
    public const string RegexPrefix = "regex:";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex _expression;

    private FilenamePattern(string template, Regex expression, IReadOnlyList<string> tokens)
    {
        Template = template;
        _expression = expression;
        Tokens = tokens;
    }

    /// <summary>The template or regular expression exactly as it was configured.</summary>
    public string Template { get; }

    /// <summary>The field names this pattern captures, in the order they appear.</summary>
    public IReadOnlyList<string> Tokens { get; }

    /// <summary>
    /// Compiles a template, or throws <see cref="FormatException"/> explaining what is
    /// wrong with it.
    /// </summary>
    /// <remarks>
    /// Throwing rather than returning null is deliberate: a mistyped pattern is a
    /// configuration fault that someone has to fix, and the message names the token they
    /// probably meant.
    /// </remarks>
    public static FilenamePattern Compile(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        string trimmed = template.Trim();

        if (trimmed.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return CompileRegex(trimmed);
        }

        return CompileTemplate(trimmed);
    }

    /// <summary>
    /// Tries a filename against this pattern.
    /// </summary>
    /// <param name="fileName">
    /// A file name or a full path; the directory and the extension are both ignored.
    /// </param>
    /// <returns>The captured fields, or null when the name does not match.</returns>
    public IReadOnlyDictionary<string, string>? Match(string? fileName)
    {
        string subject = StemOf(fileName);

        if (subject.Length == 0)
        {
            return null;
        }

        Match match;

        try
        {
            match = _expression.Match(subject);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological pattern must not hang a folder monitor. Treated as "does not
            // match", which falls through to the next pattern and then to the operator.
            return null;
        }

        if (!match.Success)
        {
            return null;
        }

        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in _expression.GetGroupNames())
        {
            // Numbered groups are not fields; only the named ones carry meaning.
            if (int.TryParse(name, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            Group group = match.Groups[name];

            if (group.Success && group.Value.Trim().Length > 0)
            {
                captured[name] = group.Value.Trim();
            }
        }

        return captured;
    }

    /// <summary>The file name without its directory or its extension.</summary>
    internal static string StemOf(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(fileName.Trim());

    // ------------------------------------------------------------------ Compilation

    private static FilenamePattern CompileRegex(string pattern)
    {
        string body = pattern[RegexPrefix.Length..].Trim();

        if (body.Length == 0)
        {
            throw new FormatException("A pattern beginning 'regex:' must be followed by an expression.");
        }

        try
        {
            var expression = new Regex(body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

            string[] names = expression.GetGroupNames()
                .Where(name => !int.TryParse(name, CultureInfo.InvariantCulture, out _))
                .ToArray();

            if (names.Length == 0)
            {
                throw new FormatException(
                    "That expression captures no named groups, so nothing would be read from the " +
                    "file name. Name the groups after the fields, for example (?<PatientId>[^_]+).");
            }

            RejectUnknown(names, pattern);

            return new FilenamePattern(pattern, expression, names);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"That is not a valid regular expression: {ex.Message}", ex);
        }
    }

    private static FilenamePattern CompileTemplate(string template)
    {
        // Parsed into segments first, because emitting a token needs to know what literal
        // FOLLOWS it. See BoundedPattern: that is what stops an identifier swallowing a
        // name.
        List<(string Literal, string? Token)> segments = Parse(template);

        var tokens = new List<string>();
        var builder = new StringBuilder("^");

        for (int index = 0; index < segments.Count; index++)
        {
            (string literal, string? token) = segments[index];

            builder.Append(Regex.Escape(literal));

            if (token is null)
            {
                continue;
            }

            string following = index + 1 < segments.Count ? segments[index + 1].Literal : string.Empty;

            AppendToken(builder, token, literal, following, tokens, template);
        }

        builder.Append('$');

        if (tokens.Count == 0)
        {
            throw new FormatException(
                $"'{template}' contains no fields, so nothing would be read from the file name. " +
                $"Known fields: {string.Join(", ", FilenameField.All)}.");
        }

        var expression = new Regex(
            builder.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout);

        return new FilenamePattern(template, expression, tokens);
    }

    /// <summary>
    /// Splits a template into literal text and the token that follows each piece.
    /// </summary>
    private static List<(string Literal, string? Token)> Parse(string template)
    {
        var segments = new List<(string, string?)>();
        int index = 0;

        while (index < template.Length)
        {
            int open = template.IndexOf('{', index);

            if (open < 0)
            {
                segments.Add((template[index..], null));
                break;
            }

            int close = template.IndexOf('}', open);

            // A second '{' before the '}' means the first was never closed. Checked
            // explicitly, because otherwise the whole run reads as one absurd token name
            // and the error talks about an unknown field instead of a missing brace.
            int nextOpen = template.IndexOf('{', open + 1);

            if (close < 0 || (nextOpen >= 0 && nextOpen < close))
            {
                throw new FormatException(
                    $"'{template}' has a '{{' that is never closed. Each field looks like {{PatientId}}.");
            }

            string token = template[(open + 1)..close].Trim();

            if (token.Length == 0)
            {
                throw new FormatException($"'{template}' contains an empty field, '{{}}'.");
            }

            segments.Add((template[index..open], token));
            index = close + 1;
        }

        return segments;
    }

    private static void AppendToken(
        StringBuilder builder,
        string token,
        string preceding,
        string following,
        List<string> tokens,
        string template)
    {
        // A placeholder for a part of the name that is present and not wanted. It takes no
        // group, so it can appear as often as a template needs it.
        if (token is "*" or "Ignore")
        {
            builder.Append(".*?");
            return;
        }

        string? field = FilenameField.Resolve(token);

        if (field is null)
        {
            throw new FormatException(
                $"'{{{token}}}' in '{template}' is not a field this reads. " +
                $"Known fields: {string.Join(", ", FilenameField.All)}, and {{*}} for a part to skip.");
        }

        if (tokens.Contains(field, StringComparer.OrdinalIgnoreCase))
        {
            // Two groups of one name is a regular expression error rather than a silent
            // last-one-wins, so it is caught here with a message that explains it.
            throw new FormatException(
                $"'{{{token}}}' appears more than once in '{template}'. Use {{*}} for a part to skip.");
        }

        tokens.Add(field);

        builder.Append("(?<").Append(field).Append('>')
               .Append(BoundedPattern(field, preceding, following))
               .Append(')');
    }

    /// <summary>
    /// What a field is allowed to match, given the separators around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An identifier may not contain the separator; a name may.</b> That single rule is
    /// what makes a template behave the way a reader expects, and getting it wrong is
    /// subtle rather than obvious. With every field matching lazily,
    /// <c>{PatientId} {Name} {AccessionNumber}</c> read "77 MARIA DO CARMO SILVA ACC9" as a
    /// patient called MARIA with the accession number "DO CARMO SILVA ACC9": each field
    /// took as little as it could, so the last one took everything left. Nothing fails, and
    /// the document is filed with a mangled name and a nonsense accession number.
    /// </para>
    /// <para>
    /// A patient identifier, an accession number and a date do not contain the separator
    /// that divides the fields; a name and a description routinely do. Bounding the
    /// identifiers to "anything but the separator" leaves the name free to take the rest,
    /// which is both what the sending software meant and what somebody reading the file
    /// name would conclude.
    /// </para>
    /// <para>
    /// Where the separator is more than one character, or the field ends the template with
    /// nothing after it, there is nothing to bound against and the field falls back to a
    /// lazy match. The result is then order dependent again, which is one reason
    /// FilenameMetadataReader prefers the pattern that reads the most fields.
    /// </para>
    /// </remarks>
    private static string BoundedPattern(string field, string preceding, string following)
    {
        bool holdsSeparators = field is FilenameField.Name or FilenameField.StudyDescription;

        if (holdsSeparators)
        {
            return ".+?";
        }

        // The character that ends this field: the first of the literal after it, or the
        // last of the literal before it when the field finishes the template.
        char? separator = following.Length > 0 ? following[0]
            : preceding.Length > 0 ? preceding[^1]
            : null;

        return separator is null
            ? ".+?"
            : $"[^{Regex.Escape(separator.Value.ToString())}]+";
    }

    private static void RejectUnknown(IEnumerable<string> names, string pattern)
    {
        string[] unknown = names.Where(name => FilenameField.Resolve(name) is null).ToArray();

        if (unknown.Length > 0)
        {
            throw new FormatException(
                $"'{pattern}' names group(s) this does not read: {string.Join(", ", unknown)}. " +
                $"Known fields: {string.Join(", ", FilenameField.All)}.");
        }
    }
}
