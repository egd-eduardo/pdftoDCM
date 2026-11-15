using PdfToDicom.Core.Models;
using PdfToDicom.Interop.Hl7V2.Profiles;
using PdfToDicom.Interop.Hl7V2.Transport;
using PdfToDicom.Interop.Routing;

namespace PdfToDicom.Integration.Configuration;

/// <summary>What the routing decided, and everything it could not do.</summary>
/// <param name="Plan">The deliveries, which is empty when nothing is to be sent.</param>
/// <param name="Problems">
/// Everything that stopped a delivery being planned, named rather than dropped.
/// </param>
/// <param name="Explanations">
/// One line per planned delivery, saying which rule decided it, for the log and for the
/// operator who asks why a message went where it did.
/// </param>
public sealed record Hl7PlanResult(
    Hl7Plan Plan,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Explanations)
{
    /// <summary>Whether anything is to be sent.</summary>
    public bool WillSend => Plan.Deliveries.Count > 0;

    /// <summary>A plan that sends nothing, for the ordinary case of no interface at all.</summary>
    public static Hl7PlanResult Nothing { get; } = new(Hl7Plan.None, [], []);
}

/// <summary>
/// Turns the configuration into a plan for one document.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam between what a site configured and what the engine does. It exists
/// separately from the dispatcher so that the question "where would this document go" can be
/// answered without sending anything, which is what the settings page needs and what
/// somebody commissioning an interface asks first.
/// </para>
/// <para>
/// Nothing here is silent. A rule naming a destination that is disabled, a profile that is
/// not installed, or a profile a destination refuses, each produces a problem and no
/// delivery. The alternative, skipping quietly, means a report that nobody was told about
/// and no record of the decision, which is the failure mode this whole engine is shaped to
/// avoid.
/// </para>
/// </remarks>
public static class Hl7PlanFactory
{
    /// <summary>The metadata keys a rule can match on, for the settings page to offer.</summary>
    /// <remarks>
    /// Published as data rather than written into the interface, so the page and the factory
    /// cannot offer and honour different sets.
    /// </remarks>
    public static IReadOnlyList<string> ConditionKeys { get; } =
    [
        "messageType",
        "triggerEvent",
        "modality",
        "studyDescription",
        "documentTitle",
        "hasAccessionNumber",
        "source"
    ];

    /// <summary>
    /// Builds the metadata a rule is matched against.
    /// </summary>
    /// <remarks>
    /// Strings only, and nothing clinical beyond what a routing decision needs. A patient
    /// identifier is deliberately absent: routing on one would mean a rule that sends one
    /// patient's reports somewhere different, which is not routing but a mistake waiting to
    /// be made.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> MetadataFor(
        PatientDemographics demographics,
        Hl7Profile profile,
        string modality = "",
        string source = "")
    {
        ArgumentNullException.ThrowIfNull(demographics);
        ArgumentNullException.ThrowIfNull(profile);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["messageType"] = profile.MessageType,
            ["triggerEvent"] = profile.TriggerEvent,
            ["modality"] = modality,
            ["studyDescription"] = demographics.StudyDescription,
            ["documentTitle"] = demographics.DocumentTitle,
            ["hasAccessionNumber"] =
                string.IsNullOrWhiteSpace(demographics.AccessionNumber) ? "no" : "yes",
            ["source"] = source
        };
    }

    /// <summary>
    /// Decides what one document's conversion should announce, and to whom.
    /// </summary>
    /// <param name="settings">The configuration.</param>
    /// <param name="profiles">The profiles installed on this machine.</param>
    /// <param name="demographics">The patient and order information.</param>
    /// <param name="modality">The modality, for a rule that matches on it.</param>
    /// <param name="source">Where the document came from, for a rule that matches on it.</param>
    public static Hl7PlanResult Build(
        Hl7IntegrationSettings settings,
        Hl7ProfileStore profiles,
        PatientDemographics demographics,
        string modality = "",
        string source = "")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(demographics);

        if (!settings.AnnounceConversions)
        {
            // Not a problem and not worth a word. A site with no interface, or one whose
            // interface is deliberately switched off while it is being commissioned, is the
            // ordinary case.
            return Hl7PlanResult.Nothing;
        }

        var problems = new List<string>();
        var explanations = new List<string>();
        var deliveries = new List<Hl7Delivery>();

        // Read once. A rule and the profile it names are resolved against the same view of
        // the folder, so a profile deleted half way through planning cannot produce a plan
        // that refers to it.
        List<StoredProfile> installed = profiles.List().Where(p => p.IsUsable).ToList();

        var engine = new RoutingEngine(settings.Rules.Select(r => r.ToEngine()).ToList());

        foreach (Hl7RoutingRuleSettings rule in settings.Rules.Where(r => r.Enabled))
        {
            if (!installed.Any(p => string.Equals(p.ProfileId, rule.ProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add(
                    $"'{rule.Name}' uses profile '{rule.ProfileId}', which is not installed.");
            }
        }

        // The metadata needs a message type, which comes from the profile, and the profile
        // comes from the rule. So each candidate rule is matched against the metadata its
        // own profile implies, rather than against one set built in advance.
        foreach (RoutingDecision decision in Decisions(engine, settings, installed, profiles, demographics, modality, source))
        {
            Hl7DestinationSettings? destination = settings.Destinations
                .FirstOrDefault(d => string.Equals(d.Id, decision.DestinationId, StringComparison.OrdinalIgnoreCase));

            if (destination is null)
            {
                problems.Add(
                    $"'{decision.RuleName}' sends to a destination that is not configured.");
                continue;
            }

            if (!destination.IsEnabled)
            {
                problems.Add(
                    $"'{decision.RuleName}' sends to '{destination.Name}', which is disabled.");
                continue;
            }

            Hl7Profile? profile = profiles.Find(decision.ProfileId);

            if (profile is null)
            {
                problems.Add(
                    $"'{decision.RuleName}' uses profile '{decision.ProfileId}', which is not installed.");
                continue;
            }

            if (destination.ProfileIds.Count > 0 &&
                !destination.ProfileIds.Contains(decision.ProfileId, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"'{decision.RuleName}' sends profile '{decision.ProfileId}' to " +
                    $"'{destination.Name}', which is restricted to other profiles.");
                continue;
            }

            IReadOnlyList<string> destinationProblems = destination.CheckBlocking();

            if (destinationProblems.Count > 0)
            {
                problems.AddRange(destinationProblems);
                continue;
            }

            deliveries.Add(new Hl7Delivery(destination.ToEngine(), profile));
            explanations.Add(
                $"'{decision.RuleName}' sends {decision.ProfileId} to {destination.Name} " +
                $"({destination.Host}:{destination.Port}).");
        }

        return new Hl7PlanResult(
            new Hl7Plan(deliveries, settings.Sequence), problems, explanations);
    }

    private static IEnumerable<RoutingDecision> Decisions(
        RoutingEngine engine,
        Hl7IntegrationSettings settings,
        IReadOnlyList<StoredProfile> installed,
        Hl7ProfileStore profiles,
        PatientDemographics demographics,
        string modality,
        string source)
    {
        // One pass per distinct profile named by a rule, because the message type a rule
        // matches on is a property of the profile it names. The results are then merged by
        // the engine's own rule, that the first decision for a destination wins it, which is
        // what stops a document reaching the same system twice.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> profileIds = settings.Rules
            .Where(r => r.Enabled)
            .Select(r => r.ProfileId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string profileId in profileIds)
        {
            StoredProfile? stored = installed
                .FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

            if (stored is null)
            {
                continue;
            }

            Hl7Profile? profile = profiles.Find(profileId);

            if (profile is null)
            {
                continue;
            }

            IReadOnlyDictionary<string, string> metadata =
                MetadataFor(demographics, profile, modality, source);

            foreach (RoutingDecision decision in engine.Route(metadata))
            {
                if (!string.Equals(decision.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (claimed.Add(decision.DestinationId))
                {
                    yield return decision;
                }
            }
        }
    }
}
