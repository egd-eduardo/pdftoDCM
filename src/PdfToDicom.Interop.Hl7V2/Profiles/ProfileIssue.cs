namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>How serious a problem with a profile is.</summary>
public enum ProfileIssueSeverity
{
    /// <summary>Worth knowing, but the profile can be used.</summary>
    Warning = 0,

    /// <summary>The profile cannot be used.</summary>
    Error = 1
}

/// <summary>One thing wrong with a profile.</summary>
/// <param name="Severity">Whether it stops the profile being used.</param>
/// <param name="Location">Where in the profile, in terms an administrator can act on.</param>
/// <param name="Message">What is wrong, and where possible what to do about it.</param>
public sealed record ProfileIssue(
    ProfileIssueSeverity Severity,
    string Location,
    string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Severity}: {Location}: {Message}";
}

/// <summary>The outcome of checking a profile.</summary>
/// <param name="Profile">The profile, when it could be read at all.</param>
/// <param name="Issues">Everything found, errors and warnings together.</param>
public sealed record ProfileReadResult(Hl7Profile? Profile, IReadOnlyList<ProfileIssue> Issues)
{
    /// <summary>True when the profile was read and nothing stops it being used.</summary>
    public bool IsUsable =>
        Profile is not null && !Issues.Any(i => i.Severity == ProfileIssueSeverity.Error);

    /// <summary>The errors on their own.</summary>
    public IReadOnlyList<ProfileIssue> Errors =>
        Issues.Where(i => i.Severity == ProfileIssueSeverity.Error).ToList();
}
