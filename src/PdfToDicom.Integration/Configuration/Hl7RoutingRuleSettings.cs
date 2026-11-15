using System.Collections.ObjectModel;
using PdfToDicom.Interop.Routing;

namespace PdfToDicom.Integration.Configuration;

/// <summary>One condition of a routing rule, in a form a grid can edit.</summary>
/// <remarks>
/// The engine holds the conditions as a dictionary, which nothing in WPF can edit in place.
/// A list of pairs can be, and it keeps a key the editor does not recognise instead of
/// dropping it on save, which a fixed set of boxes for the two common keys would not.
/// </remarks>
public sealed class Hl7ConditionSettings
{
    /// <summary>What is being matched, for example <c>messageType</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// What it must be: a value, <c>*</c> for anything present, or a comma separated list.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// One routing rule as it is stored and edited.
/// </summary>
/// <remarks>
/// A mutable mirror of <see cref="RoutingRule"/>, for the reasons given on
/// <see cref="Hl7DestinationSettings"/>, and guarded by the same pair of tests.
/// </remarks>
public sealed class Hl7RoutingRuleSettings
{
    /// <summary>What an administrator calls this rule.</summary>
    public string Name { get; set; } = "New rule";

    /// <summary>Whether it is considered at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The conditions, all of which must hold. An empty list matches everything, which is
    /// how a catch all rule is written.
    /// </summary>
    public ObservableCollection<Hl7ConditionSettings> When { get; set; } = new();

    /// <summary>The destinations this rule sends to, by identifier.</summary>
    public List<string> Destinations { get; set; } = new();

    /// <summary>Which profile builds the message for them.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Whether to stop considering rules once this one has matched.</summary>
    public bool StopOnMatch { get; set; }

    /// <summary>A one line description of the conditions, for a list.</summary>
    public string DisplayConditions =>
        When.Count == 0
            ? "any message"
            : string.Join(", ", When.Select(c => $"{c.Key}={c.Value}"));

    /// <summary>Turns this into the engine's own rule.</summary>
    public RoutingRule ToEngine() =>
        new()
        {
            Name = Name,
            Enabled = Enabled,

            // Last one wins on a duplicated key, which is what a dictionary would do anyway
            // and is reported separately by Check so nobody has to deduce it.
            When = When
                .Where(c => !string.IsNullOrWhiteSpace(c.Key))
                .GroupBy(c => c.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase),
            Destinations = Destinations.ToList(),
            ProfileId = ProfileId,
            StopOnMatch = StopOnMatch
        };

    /// <summary>Builds the editable form of an engine rule.</summary>
    public static Hl7RoutingRuleSettings FromEngine(RoutingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new Hl7RoutingRuleSettings
        {
            Name = rule.Name,
            Enabled = rule.Enabled,
            When = new ObservableCollection<Hl7ConditionSettings>(rule.When
                .Select(pair => new Hl7ConditionSettings { Key = pair.Key, Value = pair.Value })),
            Destinations = rule.Destinations.ToList(),
            ProfileId = rule.ProfileId,
            StopOnMatch = rule.StopOnMatch
        };
    }

    /// <summary>Checks this rule on its own, before it is checked against the rest.</summary>
    public IReadOnlyList<string> Check()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("A rule needs a name, so the log can say which one decided.");
        }

        if (When.Any(c => string.IsNullOrWhiteSpace(c.Key) && !string.IsNullOrWhiteSpace(c.Value)))
        {
            problems.Add($"'{Name}' has a condition with a value and nothing to match it against.");
        }

        IEnumerable<string> duplicated = When
            .Where(c => !string.IsNullOrWhiteSpace(c.Key))
            .GroupBy(c => c.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (string key in duplicated)
        {
            problems.Add(
                $"'{Name}' matches '{key}' more than once. Only the last is used; a value " +
                "and its alternatives belong in one condition, separated by commas.");
        }

        return problems;
    }
}
