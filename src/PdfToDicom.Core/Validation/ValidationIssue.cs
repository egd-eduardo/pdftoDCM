namespace PdfToDicom.Core.Validation;

/// <summary>
/// Severity of a validation finding.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>Informational only. Does not block the workflow.</summary>
    Information = 0,

    /// <summary>
    /// The workflow may proceed but the result may be less interoperable, or a
    /// non mandatory expectation was not met.
    /// </summary>
    Warning = 1,

    /// <summary>The workflow must not proceed until the issue is resolved.</summary>
    Error = 2
}

/// <summary>
/// A single validation finding, expressed in terms the operator can act upon.
/// </summary>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Field">
/// Logical field the finding relates to, for example "PatientId". Used by the user
/// interface to highlight the offending input control.
/// </param>
/// <param name="Message">Operator facing description of the problem.</param>
public sealed record ValidationIssue(ValidationSeverity Severity, string Field, string Message)
{
    /// <summary>Creates an error level finding.</summary>
    public static ValidationIssue Error(string field, string message) =>
        new(ValidationSeverity.Error, field, message);

    /// <summary>Creates a warning level finding.</summary>
    public static ValidationIssue Warning(string field, string message) =>
        new(ValidationSeverity.Warning, field, message);

    /// <summary>Creates an informational finding.</summary>
    public static ValidationIssue Information(string field, string message) =>
        new(ValidationSeverity.Information, field, message);

    /// <inheritdoc />
    public override string ToString() => $"[{Severity}] {Field}: {Message}";
}
