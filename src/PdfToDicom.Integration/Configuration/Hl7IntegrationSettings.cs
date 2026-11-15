using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfToDicom.Interop.Routing;

namespace PdfToDicom.Integration.Configuration;

/// <summary>
/// Everything a site configures about its HL7 interfaces.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of <c>AppConfiguration</c> and stored in a file of its own. The reason is the
/// one the architecture document gives for the assembly layout: <c>PdfToDicom.Core</c> knows
/// nothing about HL7, and putting these in its configuration would have given it a reference
/// to the engine, a reason to be rebuilt whenever the engine changes, and a shape its 291
/// tests would have to cover. A separate file also means a build with no HL7 interface has
/// no file at all, which is the plainest possible answer to "is anything configured here".
/// </para>
/// <para>
/// The cost is that the settings transfer in <c>SettingsTransfer</c> does not carry these
/// yet, so a second workstation is set up by copying one more file. That is a smaller price
/// than the coupling, and the file is a file somebody can copy.
/// </para>
/// </remarks>
public sealed class Hl7IntegrationSettings
{
    /// <summary>
    /// Schema version written and understood by this build.
    /// </summary>
    /// <remarks>
    /// Raised only for a change an older build would get <i>wrong</i>. An older build
    /// ignoring a setting it does not know leaves a default in place, which is harmless; one
    /// reading a name that now means something else is not.
    /// </remarks>
    public const int CurrentVersion = 1;

    /// <summary>The version of the file this was read from.</summary>
    public int SettingsVersion { get; set; } = CurrentVersion;

    /// <summary>
    /// Whether a conversion announces itself at all. Off until somebody turns it on.
    /// </summary>
    /// <remarks>
    /// Separate from the destinations being configured, and off by default, because
    /// commissioning an interface means sending test messages from a machine that also does
    /// real work. A destination that exists and is not yet trusted must be able to sit there
    /// without anything reaching it.
    /// </remarks>
    public bool AnnounceConversions { get; set; }

    /// <summary>The order the conversion and the message go in.</summary>
    public IntegrationSequence Sequence { get; set; } = IntegrationSequence.Independent;

    /// <summary>The configured destinations.</summary>
    /// <remarks>
    /// Observable because the settings page binds to it directly. Copying it into a
    /// collection the editor owns and back again on save would work, and is the step
    /// somebody eventually forgets.
    /// </remarks>
    public ObservableCollection<Hl7DestinationSettings> Destinations { get; set; } = new();

    /// <summary>The routing rules, in the order they are considered.</summary>
    public ObservableCollection<Hl7RoutingRuleSettings> Rules { get; set; } = new();

    /// <summary>Whether anything at all is configured.</summary>
    [JsonIgnore]
    public bool IsConfigured => Destinations.Count > 0 || Rules.Count > 0;

    /// <summary>The destinations that may actually be used.</summary>
    [JsonIgnore]
    public IReadOnlyList<Hl7DestinationSettings> EnabledDestinations =>
        Destinations.Where(d => d.IsEnabled).ToList();

    /// <summary>
    /// A deep copy, made through the serializer.
    /// </summary>
    /// <remarks>
    /// Not written property by property. That has already cost this product one release: a
    /// hand written copy is correct on the day it is written and silently loses the next
    /// setting somebody adds. A serializer round trip cannot, because it is driven by the
    /// shape of the type rather than by a list somebody maintains.
    /// </remarks>
    public Hl7IntegrationSettings Clone()
    {
        string json = JsonSerializer.Serialize(this, Hl7SettingsSerializer.Options);

        return JsonSerializer.Deserialize<Hl7IntegrationSettings>(json, Hl7SettingsSerializer.Options)
            ?? new Hl7IntegrationSettings();
    }

    /// <summary>
    /// Checks the whole configuration, as the settings page does before saving it.
    /// </summary>
    /// <param name="knownProfileIds">
    /// The profiles available on this machine, so a rule naming one that is not there is
    /// caught while somebody is looking at the screen.
    /// </param>
    public IReadOnlyList<string> Check(IReadOnlyCollection<string>? knownProfileIds = null)
    {
        knownProfileIds ??= [];

        var problems = new List<string>();

        foreach (Hl7DestinationSettings destination in Destinations)
        {
            problems.AddRange(destination.Check());
        }

        foreach (IGrouping<string, Hl7DestinationSettings> group in Destinations
                     .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            problems.Add(
                $"Two destinations share the identifier '{group.Key}', so a rule naming it " +
                "could reach either.");
        }

        foreach (Hl7RoutingRuleSettings rule in Rules)
        {
            problems.AddRange(rule.Check());
        }

        var engine = new RoutingEngine(Rules.Select(r => r.ToEngine()).ToList());
        problems.AddRange(engine.Check(
            Destinations.Select(d => d.Id).ToList(),
            knownProfileIds.ToList()));

        // A rule sending a profile a destination does not list. Reported rather than
        // enforced in silence: a restriction that quietly sends nothing is harder to notice
        // than a report saying the two disagree.
        foreach (Hl7RoutingRuleSettings rule in Rules.Where(r => r.Enabled))
        {
            foreach (string destinationId in rule.Destinations)
            {
                Hl7DestinationSettings? destination = Destinations
                    .FirstOrDefault(d => string.Equals(d.Id, destinationId, StringComparison.OrdinalIgnoreCase));

                if (destination is null || destination.ProfileIds.Count == 0)
                {
                    continue;
                }

                if (!destination.ProfileIds.Contains(rule.ProfileId, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"'{rule.Name}' sends profile '{rule.ProfileId}' to '{destination.Name}', " +
                        "which is restricted to other profiles.");
                }
            }
        }

        if (AnnounceConversions && Rules.All(r => !r.Enabled))
        {
            problems.Add(
                "Conversions are set to announce themselves and no rule is enabled, so " +
                "nothing would be sent.");
        }

        if (AnnounceConversions && Destinations.All(d => !d.IsEnabled) && Destinations.Count > 0)
        {
            problems.Add(
                "Conversions are set to announce themselves and every destination is " +
                "disabled, so nothing would be sent.");
        }

        return problems;
    }
}

/// <summary>The one set of serializer options these settings are read and written with.</summary>
/// <remarks>
/// Shared so that a clone, a save and a load cannot disagree about naming or about enums. A
/// clone that round trips differently from a save is the kind of defect that only shows up
/// on the machine where it matters.
/// </remarks>
internal static class Hl7SettingsSerializer
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
