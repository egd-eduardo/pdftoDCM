using System.Reflection;

namespace PdfToDicom.Integration.Configuration;

/// <summary>
/// A worked example of each part of an interface, for somebody who has not configured one
/// before.
/// </summary>
/// <remarks>
/// <para>
/// An empty page with eleven fields on it tells a newcomer nothing about what belongs in
/// them. These produce one of each thing, filled in, pointing at a receiver on this machine
/// rather than at anything real, so the whole chain can be made to work before a hospital's
/// interface engine is involved at all.
/// </para>
/// <para>
/// Deliberately pointed at 127.0.0.1. An example that defaulted to a plausible looking
/// hospital address would eventually be saved unchanged, and the first anybody would know of
/// it is a message arriving somewhere nobody expected.
/// </para>
/// </remarks>
public static class Hl7ExampleSetup
{
    /// <summary>The profile identifier of the example, as the file itself declares it.</summary>
    public const string ExampleProfileId = "sample-oru-r01";

    /// <summary>Where the example destination points, which is this machine.</summary>
    public const string ExampleHost = "127.0.0.1";

    /// <summary>The port the example destination and the test receiver both use.</summary>
    public const int ExamplePort = 2575;

    /// <summary>A destination pointing at a test receiver on this machine.</summary>
    public static Hl7DestinationSettings Destination() =>
        new()
        {
            Name = "Example, a test receiver on this machine",
            Host = ExampleHost,
            Port = ExamplePort,
            IsEnabled = true,

            // The four names a receiver reads from the header. Filled in because leaving
            // them empty is the commonest reason a first message is rejected, and somebody
            // seeing them filled in learns that they matter.
            SendingApplication = "PDFTODICOM",
            SendingFacility = "RADIOLOGY",
            ReceivingApplication = "TESTRECEIVER",
            ReceivingFacility = "HOSPITAL",

            ConnectTimeoutSeconds = 10,
            ReceiveTimeoutSeconds = 30,
            RetryCount = 0
        };

    /// <summary>A rule sending every report to one destination under one profile.</summary>
    /// <param name="destinationId">The destination it should name.</param>
    /// <param name="profileId">The profile that builds the message.</param>
    public static Hl7RoutingRuleSettings Rule(
        string destinationId, string profileId = ExampleProfileId)
    {
        var rule = new Hl7RoutingRuleSettings
        {
            Name = "Example, send every report",
            Enabled = true,
            ProfileId = profileId,
            Destinations = string.IsNullOrWhiteSpace(destinationId) ? [] : [destinationId]
        };

        // One condition, not none. A rule with no conditions matches every message of every
        // kind, and somebody learning from an example should start from the habit of saying
        // what a rule is for.
        rule.When.Add(new Hl7ConditionSettings { Key = "messageType", Value = "ORU" });

        return rule;
    }

    /// <summary>
    /// The example profile, as JSON, read from inside the application.
    /// </summary>
    /// <remarks>
    /// Embedded rather than read from beside the executable. The file exists in three places
    /// depending on how the application arrived, the installer's docs folder, the handover
    /// package's Samples folder, or a source checkout, and a button that works in one and
    /// not the others is worse than no button.
    /// </remarks>
    public static string ProfileJson()
    {
        Assembly assembly = typeof(Hl7ExampleSetup).Assembly;

        string? name = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith("hl7-oru-r01.sample.json", StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new InvalidOperationException(
                "The example HL7 profile is missing from this build.");
        }

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Installs the example profile into a store, replacing an earlier copy of it.
    /// </summary>
    public static ProfileImportResult InstallProfile(Hl7ProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        string temporary = Path.Combine(
            Path.GetTempPath(), $"pdftodicom-example-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(temporary, ProfileJson());
            return store.Import(temporary, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // A temporary file left behind is not worth failing an import for.
            }
        }
    }
}
