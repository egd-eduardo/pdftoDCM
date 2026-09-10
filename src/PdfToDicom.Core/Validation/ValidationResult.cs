namespace PdfToDicom.Core.Validation;

/// <summary>
/// An aggregate of validation findings together with a convenience flag indicating
/// whether the caller is allowed to proceed.
/// </summary>
public sealed class ValidationResult
{
    private readonly List<ValidationIssue> _issues = new();

    /// <summary>All findings, in the order they were raised.</summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>True when no error level finding was raised.</summary>
    public bool IsValid => _issues.All(i => i.Severity != ValidationSeverity.Error);

    /// <summary>Error level findings only.</summary>
    public IEnumerable<ValidationIssue> Errors =>
        _issues.Where(i => i.Severity == ValidationSeverity.Error);

    /// <summary>Warning level findings only.</summary>
    public IEnumerable<ValidationIssue> Warnings =>
        _issues.Where(i => i.Severity == ValidationSeverity.Warning);

    /// <summary>Adds a finding and returns this instance so calls can be chained.</summary>
    public ValidationResult Add(ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        _issues.Add(issue);
        return this;
    }

    /// <summary>Adds an error level finding.</summary>
    public ValidationResult AddError(string field, string message) =>
        Add(ValidationIssue.Error(field, message));

    /// <summary>Adds a warning level finding.</summary>
    public ValidationResult AddWarning(string field, string message) =>
        Add(ValidationIssue.Warning(field, message));

    /// <summary>Adds an informational finding.</summary>
    public ValidationResult AddInformation(string field, string message) =>
        Add(ValidationIssue.Information(field, message));

    /// <summary>Merges the findings of another result into this one.</summary>
    public ValidationResult Merge(ValidationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _issues.AddRange(other.Issues);
        return this;
    }

    /// <summary>
    /// Produces a single multi line string suitable for display in a message box or
    /// for writing to the log.
    /// </summary>
    public string ToDisplayString() =>
        _issues.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, _issues.Select(i => $"• {i.Message}"));
}
