namespace PdfToDicom.Core.Networking;

/// <summary>
/// Turns what an operator typed into a DICOM matching key.
/// </summary>
/// <remarks>
/// <para>
/// DICOM has no "contains". A query either carries a value that must match exactly, or it
/// carries wildcards: <c>*</c> for any run of characters and <c>?</c> for one. The worklist
/// window has always been labelled "Patient ID contains" and "Surname contains" and has
/// always sent the typed text as an exact key, so it promised one thing and did another.
/// </para>
/// <para>
/// The surname box was the worse half. Patient's Name is <c>Family^Given</c>, so an exact
/// key of <c>Silva</c> does not match <c>Silva^Maria</c> on a conformant server: typing a
/// surname found nothing at all unless the record happened to carry no given name. That is
/// what made the whole feature look broken rather than merely strict.
/// </para>
/// <para>
/// Anything the operator wildcards themselves is left exactly as typed. The command line
/// documents wildcards and people use them; silently wrapping <c>12345*</c> into
/// <c>*12345**</c> would be rude and, on some servers, slower.
/// </para>
/// </remarks>
public static class WorklistMatch
{
    /// <summary>The wildcard characters DICOM defines for single value matching.</summary>
    private static readonly char[] Wildcards = ['*', '?'];

    /// <summary>
    /// A key matching anything that contains the typed text.
    /// </summary>
    /// <remarks>
    /// Empty stays empty, which is universal matching and means "every value", not "the
    /// empty value". A leading wildcard makes some servers scan rather than seek, which is
    /// the price of the label; an operator who wants the faster prefix search can type
    /// <c>Silva*</c> and get exactly that.
    /// </remarks>
    public static string Contains(string? typed)
    {
        string value = (typed ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value.IndexOfAny(Wildcards) >= 0 ? value : $"*{value}*";
    }

    /// <summary>Whether the operator has asked for one particular person.</summary>
    /// <remarks>
    /// Used to decide whether a date restriction still makes sense. Looking up one
    /// identifier is the "where is that old examination" case, and a date range turns it
    /// into a question nobody meant to ask.
    /// </remarks>
    public static bool IsPresent(string? typed) => !string.IsNullOrWhiteSpace(typed);
}
