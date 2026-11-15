using PdfToDicom.Interop.Hl7V2.Model;

namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>One thing wrong with a message, or worth saying about it.</summary>
/// <param name="Severity">Whether it stops the message being sent.</param>
/// <param name="Location">Where, as an HL7 path or a segment name.</param>
/// <param name="Message">What is wrong, in terms an operator can act on.</param>
public sealed record Hl7MessageIssue(
    ProfileIssueSeverity Severity,
    string Location,
    string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Severity}: {Location}: {Message}";
}

/// <summary>A built message and everything found while building it.</summary>
/// <param name="Message">
/// The message, built as far as it could be. Present even when there are errors, because
/// the preview an administrator uses to find a missing mandatory field has to show them
/// something.
/// </param>
/// <param name="Issues">Errors and warnings together.</param>
public sealed record Hl7BuildResult(Hl7Message? Message, IReadOnlyList<Hl7MessageIssue> Issues)
{
    /// <summary>
    /// True only when there is a message and nothing wrong with it.
    /// </summary>
    /// <remarks>
    /// The transmitting code asks this, never whether a message exists. A message exists
    /// even when a mandatory value was missing, precisely so it can be looked at; sending
    /// it would be the mistake this product refuses to make.
    /// </remarks>
    public bool IsSendable =>
        Message is not null && !Issues.Any(i => i.Severity == ProfileIssueSeverity.Error);

    /// <summary>The errors on their own.</summary>
    public IReadOnlyList<Hl7MessageIssue> Errors =>
        Issues.Where(i => i.Severity == ProfileIssueSeverity.Error).ToList();
}
