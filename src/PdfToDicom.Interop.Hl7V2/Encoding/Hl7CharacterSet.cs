using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PdfToDicom.Interop.Hl7V2.Encoding;

/// <summary>
/// What happens to a value that the chosen character set cannot represent.
/// </summary>
public enum UnrepresentableCharacterPolicy
{
    /// <summary>
    /// Refuse to encode the message and say which character and which value caused it.
    /// </summary>
    Refuse = 0,

    /// <summary>
    /// Replace the character with its closest unaccented equivalent where one exists, and
    /// with a question mark where none does. A deliberate choice, never a default.
    /// </summary>
    Transliterate = 1
}

/// <summary>
/// The character sets of HL7 table 0211, and the rule that a name is never quietly
/// changed on its way out.
/// </summary>
/// <remarks>
/// <para>
/// This matters more here than in most products. The DICOM objects this application
/// produces declare <c>ISO_IR 192</c>, which is UTF-8, specifically so that an accented
/// Portuguese name survives, and the interoperability suite tests exactly that. A receiving
/// system confined to a single byte code page may have no representation for the same name.
/// </para>
/// <para>
/// Sending a correct name to the archive and a mangled one to the record system is the
/// class of fault this product exists to avoid, so the default is to refuse and say so.
/// Transliteration exists, because a site may decide an unaccented name is better than no
/// message, but it is a decision somebody makes rather than something that happens.
/// </para>
/// </remarks>
public static class Hl7CharacterSet
{
    /// <summary>
    /// Resolves an HL7 table 0211 value, as it appears in MSH-18, to an encoding.
    /// </summary>
    /// <remarks>
    /// Only the sets that .NET supports without an additional code page provider are
    /// listed. An unlisted one is refused rather than approximated, because approximating
    /// a character set is how the fault above happens.
    /// </remarks>
    public static bool TryResolve(string? table0211Value, [NotNullWhen(true)] out System.Text.Encoding? encoding)
    {
        encoding = (table0211Value ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            // An absent MSH-18 means the default, which the standard fixes as ASCII.
            "" or "ASCII" => System.Text.Encoding.ASCII,
            "8859/1" => System.Text.Encoding.Latin1,
            "UNICODE UTF-8" or "UTF-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            _ => null
        };

        return encoding is not null;
    }

    /// <summary>
    /// Resolves a table 0211 value or throws, naming what was asked for.
    /// </summary>
    public static System.Text.Encoding Resolve(string? table0211Value)
    {
        if (TryResolve(table0211Value, out System.Text.Encoding? encoding))
        {
            return encoding;
        }

        throw new NotSupportedException(
            $"The character set '{table0211Value}' is not supported. " +
            "This build supports ASCII, 8859/1 and UNICODE UTF-8.");
    }

    /// <summary>
    /// Finds the first character of a value that the encoding cannot represent, or null
    /// when every character survives.
    /// </summary>
    /// <remarks>
    /// Asked before a message is built rather than after, so that the operator is told
    /// which value is the problem while it is still on the form in front of them.
    /// </remarks>
    public static char? FindUnrepresentable(string? value, System.Text.Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // A round trip is used rather than the encoder's fallback, because it answers the
        // question actually being asked: would the receiving system see what was typed?
        System.Text.Encoding strict = (System.Text.Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ReplacementFallback;
        strict.DecoderFallback = DecoderFallback.ReplacementFallback;

        string roundTripped = strict.GetString(strict.GetBytes(value));

        if (roundTripped == value)
        {
            return null;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (index >= roundTripped.Length || roundTripped[index] != value[index])
            {
                return value[index];
            }
        }

        return value[^1];
    }

    /// <summary>
    /// Encodes a complete message for transmission, applying the policy for anything the
    /// character set cannot carry.
    /// </summary>
    /// <exception cref="Hl7CharacterSetException">
    /// The policy is <see cref="UnrepresentableCharacterPolicy.Refuse"/> and the message
    /// holds a character the set cannot represent.
    /// </exception>
    public static byte[] Encode(
        string message,
        System.Text.Encoding encoding,
        UnrepresentableCharacterPolicy policy = UnrepresentableCharacterPolicy.Refuse)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(encoding);

        char? offending = FindUnrepresentable(message, encoding);

        if (offending is null)
        {
            return encoding.GetBytes(message);
        }

        if (policy == UnrepresentableCharacterPolicy.Refuse)
        {
            throw new Hl7CharacterSetException(offending.Value, encoding.WebName);
        }

        return encoding.GetBytes(ToRepresentable(message, encoding));
    }

    /// <summary>
    /// Strips the accents from a value, leaving the letters beneath them.
    /// </summary>
    /// <remarks>
    /// Decomposing and dropping the combining marks turns "Conceicao" with its cedilla and
    /// tilde into plain letters, which is the behaviour a site choosing transliteration
    /// expects. A character with no decomposition, such as one in a non Latin script, has
    /// no unaccented form to fall back to and becomes a question mark, which is visibly
    /// wrong rather than subtly wrong.
    /// </remarks>
    public static string Transliterate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (char c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // Decomposition handles a letter carrying a mark, which is most of them. It
            // does nothing for a letter that is its own character: a Polish crossed l is not
            // an l with something added, so it survives the step above untouched and would
            // leave a name no better off than before. These are the European ones, and the
            // list is short because it is the whole of the problem rather than a sample.
            if (WithoutMarkOfItsOwn.TryGetValue(c, out string? replacement))
            {
                builder.Append(replacement);
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly IReadOnlyDictionary<char, string> WithoutMarkOfItsOwn =
        new Dictionary<char, string>
        {
            ['\u0141'] = "L", ['\u0142'] = "l",   // crossed L, Polish
            ['\u00d8'] = "O", ['\u00f8'] = "o",   // slashed O, Scandinavian
            ['\u0110'] = "D", ['\u0111'] = "d",   // barred D, Croatian
            ['\u00d0'] = "D", ['\u00f0'] = "d",   // eth, Icelandic
            ['\u00de'] = "Th", ['\u00fe'] = "th", // thorn, Icelandic
            ['\u00c6'] = "AE", ['\u00e6'] = "ae",
            ['\u0152'] = "OE", ['\u0153'] = "oe",
            ['\u00df'] = "ss"                      // sharp s, German
        };

    /// <summary>
    /// Produces a value the encoding can certainly carry, stripping what it can and
    /// replacing what it cannot.
    /// </summary>
    /// <remarks>
    /// The safety net behind <see cref="Transliterate"/>. Stripping accents is not a
    /// guarantee: a name in a script with no Latin form has nothing to strip, and before
    /// this existed such a value could be transliterated, still be unrepresentable, and be
    /// written anyway. Anything that survives both steps becomes a question mark, which is
    /// visibly wrong rather than subtly wrong, and the caller is told what it did.
    /// </remarks>
    public static string ToRepresentable(string? value, System.Text.Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        if (string.IsNullOrEmpty(value) || FindUnrepresentable(value, encoding) is null)
        {
            return value ?? string.Empty;
        }

        // Character by character rather than over the whole value, which matters more than
        // it sounds. Stripping the lot would take the cedilla and the tilde out of a
        // Portuguese name that the destination could carry perfectly well, because one
        // letter elsewhere in it could not. Only what must change is changed.
        var builder = new StringBuilder(value.Length);

        foreach (char c in value)
        {
            string single = c.ToString();

            if (FindUnrepresentable(single, encoding) is null)
            {
                builder.Append(c);
                continue;
            }

            string stripped = Transliterate(single);

            builder.Append(
                stripped.Length > 0 && FindUnrepresentable(stripped, encoding) is null
                    ? stripped
                    : '?');
        }

        return builder.ToString();
    }
}

/// <summary>
/// Raised when a message holds a character the destination's character set cannot carry
/// and the configured policy is to refuse.
/// </summary>
public sealed class Hl7CharacterSetException : Exception
{
    /// <summary>Creates the exception.</summary>
    public Hl7CharacterSetException(char character, string characterSet)
        : base($"The character '{character}' (U+{(int)character:X4}) cannot be represented " +
               $"in '{characterSet}', which this destination requires. The message was not " +
               "sent, because changing a patient's details on the way out is worse than " +
               "not sending them.")
    {
        Character = character;
        CharacterSet = characterSet;
    }

    /// <summary>The first character that could not be represented.</summary>
    public char Character { get; }

    /// <summary>The character set that could not represent it.</summary>
    public string CharacterSet { get; }
}
