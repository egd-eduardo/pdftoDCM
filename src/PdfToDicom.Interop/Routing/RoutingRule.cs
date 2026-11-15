namespace PdfToDicom.Interop.Routing;

/// <summary>
/// One rule deciding where a message goes.
/// </summary>
/// <remarks>
/// <para>
/// The conditions are matched against metadata rather than against a typed message,
/// which is what keeps routing independent of any protocol. HL7 supplies a message type
/// and a trigger event; something else would supply whatever it has. The engine never
/// learns what any of the keys mean.
/// </para>
/// <para>
/// Three forms of condition, and no more: an exact value, <c>*</c> for any value that is
/// present and not empty, and a comma separated list for alternatives. A pattern language
/// was rejected here for the same reason as in the transforms, with one addition: a routing
/// mistake does not produce a malformed message, it produces a patient's report arriving
/// somewhere nobody intended.
/// </para>
/// </remarks>
public sealed record RoutingRule
{
    /// <summary>What an administrator calls this rule.</summary>
    public required string Name { get; init; }

    /// <summary>Whether it is considered at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The conditions, all of which must hold. An empty set matches everything, which is
    /// how a catch all rule is written.
    /// </summary>
    public IReadOnlyDictionary<string, string> When { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The destinations this rule sends to, by identifier.</summary>
    public IReadOnlyList<string> Destinations { get; init; } = [];

    /// <summary>Which profile builds the message for those destinations.</summary>
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>
    /// Whether to stop once this rule has matched.
    /// </summary>
    /// <remarks>
    /// False by default, so that a message can legitimately reach several systems. A rule
    /// that is meant to be the last word says so.
    /// </remarks>
    public bool StopOnMatch { get; init; }

    /// <summary>Whether this rule applies to the supplied metadata.</summary>
    public bool Matches(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!Enabled)
        {
            return false;
        }

        foreach ((string key, string expected) in When)
        {
            metadata.TryGetValue(key, out string? actual);
            actual ??= string.Empty;

            if (!ConditionHolds(expected, actual))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConditionHolds(string expected, string actual)
    {
        if (expected == "*")
        {
            return !string.IsNullOrEmpty(actual);
        }

        if (expected.Contains(','))
        {
            return expected
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(option => string.Equals(option, actual, StringComparison.OrdinalIgnoreCase));
        }

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One place a message is to be sent, and what builds it.</summary>
/// <param name="DestinationId">Which destination.</param>
/// <param name="ProfileId">Which profile builds the message for it.</param>
/// <param name="RuleName">Which rule decided, so the decision can be explained.</param>
public sealed record RoutingDecision(string DestinationId, string ProfileId, string RuleName);
