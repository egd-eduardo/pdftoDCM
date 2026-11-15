namespace PdfToDicom.Interop.Routing;

/// <summary>
/// Works through the rules in order and says where a message goes.
/// </summary>
public sealed class RoutingEngine
{
    private readonly IReadOnlyList<RoutingRule> _rules;

    /// <summary>Creates an engine over an ordered list of rules.</summary>
    public RoutingEngine(IReadOnlyList<RoutingRule> rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>
    /// Decides where one message goes, at most once per destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules are considered in order, and the first rule to name a destination wins it. A
    /// later rule naming the same destination is ignored for that destination and still
    /// applies to any others it names.
    /// </para>
    /// <para>
    /// That rule exists for one reason. Two overlapping conditions are easy to write by
    /// accident, and without this the result would be the same report arriving twice at the
    /// same system, which is a duplicate in a patient's record rather than a tidiness
    /// problem. Sending twice is never what somebody meant by writing two rules.
    /// </para>
    /// </remarks>
    public IReadOnlyList<RoutingDecision> Route(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var decisions = new List<RoutingDecision>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RoutingRule rule in _rules)
        {
            if (!rule.Matches(metadata))
            {
                continue;
            }

            foreach (string destination in rule.Destinations)
            {
                if (string.IsNullOrWhiteSpace(destination) || !claimed.Add(destination))
                {
                    continue;
                }

                decisions.Add(new RoutingDecision(destination, rule.ProfileId, rule.Name));
            }

            if (rule.StopOnMatch)
            {
                break;
            }
        }

        return decisions;
    }

    /// <summary>
    /// Checks the rules themselves, so a mistake is found while somebody is editing them.
    /// </summary>
    public IReadOnlyList<string> Check(
        IReadOnlyCollection<string> knownDestinations, IReadOnlyCollection<string> knownProfiles)
    {
        ArgumentNullException.ThrowIfNull(knownDestinations);
        ArgumentNullException.ThrowIfNull(knownProfiles);

        var problems = new List<string>();

        foreach (RoutingRule rule in _rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                problems.Add("A rule has no name, so nothing can refer to it.");
            }

            if (rule.Destinations.Count == 0)
            {
                problems.Add($"'{rule.Name}' names no destination, so it can never do anything.");
            }

            foreach (string destination in rule.Destinations)
            {
                if (!knownDestinations.Contains(destination, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"'{rule.Name}' sends to '{destination}', which is not a configured " +
                        "destination.");
                }
            }

            if (string.IsNullOrWhiteSpace(rule.ProfileId))
            {
                problems.Add($"'{rule.Name}' names no profile, so nothing would build the message.");
            }
            else if (!knownProfiles.Contains(rule.ProfileId, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"'{rule.Name}' uses profile '{rule.ProfileId}', which does not exist.");
            }
        }

        return problems;
    }
}
