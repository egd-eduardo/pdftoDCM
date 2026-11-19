using System.Globalization;
using System.Text;

namespace PdfToDicom.Core.Networking;

/// <summary>
/// Compares names and identifiers the way somebody at a desk expects, ignoring case and
/// accents.
/// </summary>
/// <remarks>
/// <para>
/// This exists because DICOM cannot do it. Matching is the server's decision: for Patient's
/// Name the standard permits an SCP to be case insensitive and does not require it, for
/// Patient ID it does not even permit it, and no DICOM matching of any kind folds accents.
/// So a worklist holding Gonçalves cannot be found by typing Goncalves, and on a strict
/// server not by typing gonçalves either, however the query is written.
/// </para>
/// <para>
/// Folding therefore happens here, on entries the server has already returned. It is used
/// only to decide which rows to show. **Nothing folded is ever stored or transmitted**: the
/// entry keeps the server's own spelling, and that is what reaches the DICOM object, because
/// the worklist is the authority on how a patient's name is written and this is only an
/// opinion about how to find it.
/// </para>
/// </remarks>
public static class WorklistFolding
{
    /// <summary>
    /// Letters that carry their mark inside the character rather than beside it.
    /// </summary>
    /// <remarks>
    /// Decomposition turns é into e plus a combining accent, which can then be dropped. It
    /// does nothing for these, because the stroke in ł and the slash in ø are part of the
    /// letter. Portuguese needs none of them; a Polish or Nordic name in a Portuguese
    /// hospital is common enough to be worth the eight lines.
    ///
    /// The HL7 engine carries a similar map for transliterating a name it cannot encode.
    /// They are deliberately not shared: Core does not reference the HL7 assembly, and
    /// making it do so to save a lookup table would couple the conversion path to the
    /// integration engine for no benefit.
    /// </remarks>
    private static readonly Dictionary<char, string> WithoutMarkOfItsOwn = new()
    {
        ['Ł'] = "L", ['ł'] = "l",
        ['Ø'] = "O", ['ø'] = "o",
        ['Đ'] = "D", ['đ'] = "d", ['Ð'] = "D", ['ð'] = "d",
        ['Þ'] = "TH", ['þ'] = "th",
        ['Æ'] = "AE", ['æ'] = "ae",
        ['Œ'] = "OE", ['œ'] = "oe",
        ['ß'] = "ss",
        ['Ħ'] = "H", ['ħ'] = "h",

        // The Turkish pair, which invariant upper casing does not touch: ı stays ı rather
        // than becoming I, so a name holding it would be unfindable by anybody typing an
        // ordinary i. Mapped rather than left alone because the whole point of folding
        // invariantly is that the answer does not depend on the machine.
        ['ı'] = "i", ['İ'] = "I"
    };

    /// <summary>
    /// The form two values are compared in: upper case, no accents, no surrounding space.
    /// </summary>
    /// <remarks>
    /// Invariant upper case rather than the current culture's. A Turkish workstation upper
    /// cases a dotless i differently, and a worklist lookup should not depend on where the
    /// machine is.
    /// </remarks>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var expanded = new StringBuilder(value.Length);

        foreach (char character in value.Trim())
        {
            if (WithoutMarkOfItsOwn.TryGetValue(character, out string? replacement))
            {
                expanded.Append(replacement);
            }
            else
            {
                expanded.Append(character);
            }
        }

        string decomposed = expanded.ToString().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);

        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(character);
            }
        }

        return folded.ToString().ToUpperInvariant();
    }

    /// <summary>
    /// Whether a value contains what was typed, ignoring case and accents.
    /// </summary>
    /// <remarks>
    /// Contains rather than equals, because the two boxes are labelled "contains" and
    /// because a surname is one component of Patient's Name: an operator typing a family
    /// name is looking inside a value that also holds the given name.
    /// </remarks>
    public static bool Contains(string? value, string? typed)
    {
        string needle = Fold(typed);

        return needle.Length == 0
            || Fold(value).Contains(needle, StringComparison.Ordinal);
    }
}
