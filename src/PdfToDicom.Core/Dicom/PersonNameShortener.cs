using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// The result of fitting a name into what DICOM allows.
/// </summary>
/// <param name="FamilyName">Family name as it will be sent.</param>
/// <param name="GivenName">Given name as it will be sent.</param>
/// <param name="WasShortened">True when anything had to be dropped.</param>
/// <param name="OriginalLength">Length the Person Name would have had, unshortened.</param>
public readonly record struct ShortenedPersonName(
    string FamilyName,
    string GivenName,
    bool WasShortened,
    int OriginalLength);

/// <summary>
/// Fits a patient name from a worklist into the sixty four characters a DICOM Person Name
/// component group allows.
/// </summary>
/// <remarks>
/// <para>
/// The limit is real and not negotiable. PS3.5 allows sixty four characters per component
/// group, where the group is the whole "Family^Given^Middle^Prefix^Suffix" string, and the
/// toolkit refuses to build an object that breaks it. The figure of roughly one hundred
/// and ninety sometimes quoted is three component groups holding the SAME name in
/// different scripts, Latin and ideographic and phonetic, which is not a way to store a
/// longer Latin name and would be displayed wrongly by a viewer.
/// </para>
/// <para>
/// Portuguese names regularly exceed sixty four characters once a surname and given names
/// are joined, so this is an everyday case rather than an edge one.
/// </para>
/// <para>
/// The policy is deliberate: <b>the surname is kept whole and the given name is shortened
/// to fit</b>. The surname is what an archive indexes and what a clinician searches, so
/// losing any of it is the more damaging outcome. Shortening stops at a word boundary
/// where one exists, because "MARIA DO CARMO" reads as a name and "MARIA DO CARMO FERN"
/// reads as a defect.
/// </para>
/// <para>
/// It happens without interrupting the operator, which is what the department asked for.
/// It is still written to the audit trail by the caller: that costs the operator nothing
/// and means a later question about why a name differs from the worklist has an answer.
/// </para>
/// </remarks>
public static class PersonNameShortener
{
    /// <summary>
    /// Shortens a family and given name so the resulting Person Name fits.
    /// </summary>
    public static ShortenedPersonName Fit(string? familyName, string? givenName)
    {
        string family = (familyName ?? string.Empty).Trim();
        string given = (givenName ?? string.Empty).Trim();

        int limit = DicomValueRules.MaxPersonNameGroupLength;
        int originalLength = ComposedLength(family, given);

        if (originalLength <= limit)
        {
            return new ShortenedPersonName(family, given, false, originalLength);
        }

        // A surname that fills the limit on its own leaves no room for anything else. It
        // is still kept in preference to the given name, and trimmed only if it alone is
        // too long to encode at all.
        if (family.Length >= limit)
        {
            return new ShortenedPersonName(
                TrimAtWordBoundary(family, limit), string.Empty, true, originalLength);
        }

        // One character of the budget goes to the caret separating the two components.
        int budget = limit - family.Length - 1;

        string shortened = budget <= 0
            ? string.Empty
            : TrimAtWordBoundary(given, budget);

        return new ShortenedPersonName(family, shortened, true, originalLength);
    }

    /// <summary>
    /// Length of the Person Name that these components produce.
    /// </summary>
    /// <remarks>
    /// The separator only exists when there is something after it, which matches how the
    /// formatter composes the value and avoids reserving a character that is never used.
    /// </remarks>
    private static int ComposedLength(string family, string given) =>
        given.Length == 0 ? family.Length : family.Length + 1 + given.Length;

    /// <summary>
    /// Cuts a value to at most <paramref name="limit"/> characters, at the last space
    /// inside the limit when there is one.
    /// </summary>
    /// <remarks>
    /// Falling back to a hard cut matters: a single name longer than the whole budget has
    /// no space to break at, and returning nothing would be worse than returning its
    /// beginning.
    /// </remarks>
    private static string TrimAtWordBoundary(string value, int limit)
    {
        if (value.Length <= limit)
        {
            return value;
        }

        string cut = value[..limit];
        int lastSpace = cut.LastIndexOf(' ');

        return lastSpace > 0 ? cut[..lastSpace].TrimEnd() : cut.TrimEnd();
    }
}
