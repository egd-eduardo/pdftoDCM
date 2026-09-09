using System.Text;
using System.Text.RegularExpressions;

namespace PdfToDicom.Core.Validation;

/// <summary>
/// Low level checks and sanitisers for the DICOM Value Representations used by this
/// application, as specified in DICOM PS3.5 section 6.2.
/// </summary>
/// <remarks>
/// These rules are applied before the dataset is handed to the DICOM toolkit. Catching
/// an over long or illegal value here produces a clear message pointing at the offending
/// form field, which is far more useful to a hospital operator than a toolkit exception
/// raised deep inside the encoder.
/// </remarks>
public static partial class DicomValueRules
{
    /// <summary>Maximum length of an Application Entity (AE) value.</summary>
    public const int MaxAeTitleLength = 16;

    /// <summary>Maximum length of a Long String (LO) value.</summary>
    public const int MaxLongStringLength = 64;

    /// <summary>Maximum length of a Short String (SH) value.</summary>
    public const int MaxShortStringLength = 16;

    /// <summary>Maximum length of a Short Text (ST) value.</summary>
    public const int MaxShortTextLength = 1024;

    /// <summary>Maximum length of a Long Text (LT) value.</summary>
    public const int MaxLongTextLength = 10240;

    /// <summary>Maximum length of a Unique Identifier (UI) value.</summary>
    public const int MaxUidLength = 64;

    /// <summary>
    /// Maximum length of a single component group of a Person Name (PN) value.
    /// PN allows three component groups, each limited to 64 characters.
    /// </summary>
    public const int MaxPersonNameGroupLength = 64;

    /// <summary>
    /// A syntactically well formed UID: dot separated numeric components, no component
    /// with a redundant leading zero, and no trailing dot.
    /// </summary>
    [GeneratedRegex(@"^(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))*$", RegexOptions.CultureInvariant)]
    private static partial Regex UidPattern();

    /// <summary>
    /// Characters that are never acceptable in the string VRs used here. The backslash is
    /// the DICOM multi value delimiter and control characters break the encoding.
    /// </summary>
    private static bool IsIllegalStringCharacter(char c) =>
        c == '\\' || char.IsControl(c);

    /// <summary>
    /// Determines whether the supplied value is a valid Application Entity Title.
    /// An AE Title is 1 to 16 characters of the default repertoire, must not consist
    /// solely of spaces, and must not contain a backslash or control character.
    /// </summary>
    /// <param name="value">Candidate AE Title.</param>
    /// <param name="error">Set to a human readable reason when the value is rejected.</param>
    public static bool IsValidAeTitle(string? value, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The AE Title must not be empty.";
            return false;
        }

        if (value.Length > MaxAeTitleLength)
        {
            error = $"The AE Title must not exceed {MaxAeTitleLength} characters (supplied value has {value.Length}).";
            return false;
        }

        if (value != value.Trim())
        {
            error = "The AE Title must not begin or end with a space.";
            return false;
        }

        foreach (char c in value)
        {
            // The AE VR uses the default character repertoire, which is 7 bit ASCII
            // excluding control characters and the backslash.
            if (c < 0x20 || c > 0x7E || c == '\\')
            {
                error = $"The AE Title contains the unsupported character '{c}'. Use letters, digits and basic punctuation only.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Determines whether the supplied value is a syntactically valid DICOM UID.
    /// </summary>
    /// <param name="value">Candidate UID.</param>
    /// <param name="error">Set to a human readable reason when the value is rejected.</param>
    public static bool IsValidUid(string? value, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The UID must not be empty.";
            return false;
        }

        if (value.Length > MaxUidLength)
        {
            error = $"The UID must not exceed {MaxUidLength} characters (supplied value has {value.Length}).";
            return false;
        }

        if (!UidPattern().IsMatch(value))
        {
            error = "The UID must consist of dot separated numbers, without a leading zero in any component and without a trailing dot.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Removes characters that cannot be encoded in a DICOM string VR and trims the
    /// result to the supplied maximum length.
    /// </summary>
    /// <param name="value">Raw operator input.</param>
    /// <param name="maxLength">Maximum length permitted by the target VR.</param>
    /// <returns>A value safe to place into the dataset. Never null.</returns>
    public static string Sanitise(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!IsIllegalStringCharacter(c))
            {
                builder.Append(c);
            }
        }

        // DICOM string values are padded to an even length by the encoder, so trailing
        // whitespace carries no meaning and is removed here for cleanliness.
        string cleaned = builder.ToString().Trim();

        return cleaned.Length <= maxLength
            ? cleaned
            : cleaned[..maxLength];
    }

    /// <summary>
    /// Determines whether the supplied string can be represented using the ISO IR 100
    /// (Latin 1) repertoire. Used to warn the operator when an accented or non Latin
    /// name would be lost if the configured character set is not UTF-8.
    /// </summary>
    public static bool IsRepresentableInLatin1(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (char c in value)
        {
            if (c > 0xFF)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether the supplied string contains only characters from the DICOM
    /// default repertoire (7 bit ASCII). Values outside this range require an explicit
    /// Specific Character Set.
    /// </summary>
    public static bool IsPureAscii(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (char c in value)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }
}
