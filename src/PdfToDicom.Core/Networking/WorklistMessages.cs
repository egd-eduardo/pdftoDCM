namespace PdfToDicom.Core.Networking;

/// <summary>
/// The two sentences a worklist query reports, and the reason their wording is fixed.
/// </summary>
/// <remarks>
/// <para>
/// These read like prose and behave like an interface. <c>build/verify-against-dcmtk.sh</c>
/// drives the command line front end against DCMTK's own worklist server and decides whether
/// five of its checks passed by looking for these exact phrases, so rewording them breaks the
/// only test that answers to an independent implementation. That happened once: a clearer
/// sentence turned five passing checks red and the behaviour underneath was never at fault.
/// </para>
/// <para>
/// So the phrases live here, with a test that asserts the substrings the script looks for.
/// Anybody rewording them now gets a failing unit test in a second rather than a red build
/// four minutes later, and the test says what depends on it.
/// </para>
/// </remarks>
public static class WorklistMessages
{
    /// <summary>The phrase the verification script looks for in an empty result.</summary>
    public const string NothingFound = "no scheduled procedures";

    /// <summary>What is reported when the server answered with nothing.</summary>
    public static string Nothing(string criteria) =>
        $"The worklist returned {NothingFound} for this query. Asked for {criteria}.";

    /// <summary>What is reported when the server answered with entries.</summary>
    /// <remarks>
    /// The count and the full stop are what the script matches, so the criteria follow as a
    /// second sentence rather than being worked into the first.
    /// </remarks>
    public static string Found(int count, string criteria) =>
        $"{count} scheduled procedure(s) found. Asked for {criteria}.";
}
