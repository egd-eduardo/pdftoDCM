using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WorklistExplorer.Core.Text;

/// <summary>
/// Folding for the local filter only. Never applied to a value sent to a server or shown
/// on screen.
/// </summary>
/// <remarks>
/// Filtering is done here rather than on the server because PS3.4 C.2.2.2 permits case
/// insensitive matching for PN and requires it of nobody, and no DICOM matching folds
/// accents. A server side name key would therefore behave differently on every server.
/// </remarks>
public static class NameFolding
{
    // Letters whose distinguishing mark is not a combining character, so FormD leaves
    // them intact. The dotless i is included because ToUpperInvariant does not map it.
    private static readonly Dictionary<char, string> Special = new()
    {
        ['Ł'] = "L", ['ł'] = "L",
        ['Ø'] = "O", ['ø'] = "O",
        ['Đ'] = "D", ['đ'] = "D",
        ['Þ'] = "TH", ['þ'] = "TH",
        ['Æ'] = "AE", ['æ'] = "AE",
        ['Œ'] = "OE", ['œ'] = "OE",
        ['ß'] = "SS", ['ẞ'] = "SS",
        ['Ħ'] = "H", ['ħ'] = "H",
        ['ı'] = "I", ['İ'] = "I",
    };

    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (Special.TryGetValue(c, out var replacement)) sb.Append(replacement);
            else sb.Append(c);
        }
        return sb.ToString().ToUpperInvariant();
    }
}

/// <summary>
/// A local filter over the fetched rows. Plain text is a contains match; if the user typed
/// * or ? the text is used as a whole value wildcard exactly as typed, so a deliberate
/// pattern is never rewritten.
/// </summary>
public sealed class LocalFilter
{
    private readonly Regex? _pattern;
    private readonly string _contains = "";

    public LocalFilter(string? text)
    {
        Text = text?.Trim() ?? "";
        var folded = NameFolding.Fold(Text);
        if (folded.Contains('*') || folded.Contains('?'))
        {
            var regex = "^" + Regex.Escape(folded).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            _pattern = new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }
        else
        {
            _contains = folded;
        }
    }

    public string Text { get; }
    public bool IsEmpty => Text.Length == 0;

    public bool Matches(string? value)
    {
        if (IsEmpty) return true;
        var folded = NameFolding.Fold(value);
        return _pattern?.IsMatch(folded) ?? folded.Contains(_contains, StringComparison.Ordinal);
    }
}
