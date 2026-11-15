using System.Reflection;
using System.Text.Json;
using PdfToDicom.Core.Models;
using PdfToDicom.Integration.Configuration;
using PdfToDicom.Interop.Hl7V2.Transport;
using PdfToDicom.Interop.Routing;
using Xunit;

namespace PdfToDicom.Integration.Tests;

/// <summary>
/// The settings that configure an interface: what they carry across to the engine, what
/// they refuse, and what they say rather than doing quietly.
/// </summary>
public sealed class Hl7SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "pdftodicom-hl7-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "integration.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // ------------------------------------------------------- The mirror and the engine type

    /// <summary>
    /// The editable destination and the engine's own must describe the same thing.
    /// </summary>
    /// <remarks>
    /// This is the test that earns the duplicated shape its place. A property added to one
    /// and not the other is a setting that is either editable and ignored, or honoured and
    /// uneditable, and both are silent. Comparing the names catches it on the day it
    /// happens; the test below catches a name that is carried across wrongly.
    /// </remarks>
    [Fact]
    public void TheEditableDestinationAndTheEngineDestinationDescribeTheSameThing()
    {
        HashSet<string> settings = PropertyNames(typeof(Hl7DestinationSettings));
        HashSet<string> engine = PropertyNames(typeof(Hl7Destination));

        // Each type is allowed its own conveniences, which are computed rather than stored.
        settings.Remove(nameof(Hl7DestinationSettings.DisplaySummary));
        engine.Remove(nameof(Hl7Destination.DisplaySummary));

        Assert.Equal(engine.OrderBy(n => n), settings.OrderBy(n => n));
    }

    [Fact]
    public void EveryDestinationSettingReachesTheEngine()
    {
        var settings = new Hl7DestinationSettings
        {
            Id = "dest-1",
            Name = "St Bartholomew's RIS",
            Host = "ris.hospital.local",
            Port = 2576,
            IsEnabled = false,
            SendingApplication = "PDFTODICOM",
            SendingFacility = "RADIOLOGY",
            ReceivingApplication = "RIS",
            ReceivingFacility = "HOSPITAL",
            ConnectTimeoutSeconds = 11,
            ReceiveTimeoutSeconds = 44,
            RetryCount = 2,
            RetryDelaySeconds = 7,
            UseTls = true,
            AcceptAnyTlsCertificate = true,
            ProfileIds = ["one", "two"],
            LastSuccessfulUseUtc = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero)
        };

        Hl7Destination engine = settings.ToEngine();

        Assert.Equal("dest-1", engine.Id);
        Assert.Equal("St Bartholomew's RIS", engine.Name);
        Assert.Equal("ris.hospital.local", engine.Host);
        Assert.Equal(2576, engine.Port);
        Assert.False(engine.IsEnabled);
        Assert.Equal("PDFTODICOM", engine.SendingApplication);
        Assert.Equal("RADIOLOGY", engine.SendingFacility);
        Assert.Equal("RIS", engine.ReceivingApplication);
        Assert.Equal("HOSPITAL", engine.ReceivingFacility);
        Assert.Equal(11, engine.ConnectTimeoutSeconds);
        Assert.Equal(44, engine.ReceiveTimeoutSeconds);
        Assert.Equal(2, engine.RetryCount);
        Assert.Equal(7, engine.RetryDelaySeconds);
        Assert.True(engine.UseTls);
        Assert.True(engine.AcceptAnyTlsCertificate);
        Assert.Equal(["one", "two"], engine.ProfileIds);
        Assert.Equal(settings.LastSuccessfulUseUtc, engine.LastSuccessfulUseUtc);

        // And back, so an imported destination can be edited rather than retyped.
        Hl7DestinationSettings returned = Hl7DestinationSettings.FromEngine(engine);
        Assert.Equal(
            JsonSerializer.Serialize(settings), JsonSerializer.Serialize(returned));
    }

    [Fact]
    public void TheEditableRuleAndTheEngineRuleDescribeTheSameThing()
    {
        HashSet<string> settings = PropertyNames(typeof(Hl7RoutingRuleSettings));
        HashSet<string> engine = PropertyNames(typeof(RoutingRule));

        settings.Remove(nameof(Hl7RoutingRuleSettings.DisplayConditions));

        Assert.Equal(engine.OrderBy(n => n), settings.OrderBy(n => n));
    }

    [Fact]
    public void EveryRuleSettingReachesTheEngine()
    {
        var settings = new Hl7RoutingRuleSettings
        {
            Name = "Reports to the RIS",
            Enabled = true,
            When =
            [
                new Hl7ConditionSettings { Key = "messageType", Value = "ORU" },
                new Hl7ConditionSettings { Key = "triggerEvent", Value = "R01" }
            ],
            Destinations = ["dest-1"],
            ProfileId = "sample-oru-r01",
            StopOnMatch = true
        };

        RoutingRule engine = settings.ToEngine();

        Assert.Equal("Reports to the RIS", engine.Name);
        Assert.True(engine.Enabled);
        Assert.Equal("ORU", engine.When["messageType"]);
        Assert.Equal("R01", engine.When["triggerEvent"]);
        Assert.Equal(["dest-1"], engine.Destinations);
        Assert.Equal("sample-oru-r01", engine.ProfileId);
        Assert.True(engine.StopOnMatch);
    }

    [Fact]
    public void AConditionWithNoKeyIsNotCarriedAcross()
    {
        // A half typed row in the grid must not become a condition that matches an empty
        // key, which would match nothing and make the rule look broken for no visible
        // reason. It is reported by Check instead.
        var settings = new Hl7RoutingRuleSettings
        {
            When = [new Hl7ConditionSettings { Key = "  ", Value = "ORU" }]
        };

        Assert.Empty(settings.ToEngine().When);
        Assert.Contains(settings.Check(), p => p.Contains("nothing to match it against"));
    }

    [Fact]
    public void AKeyMatchedTwiceIsReportedRatherThanSilentlyResolved()
    {
        var settings = new Hl7RoutingRuleSettings
        {
            Name = "Twice",
            When =
            [
                new Hl7ConditionSettings { Key = "messageType", Value = "ORU" },
                new Hl7ConditionSettings { Key = "messageType", Value = "ADT" }
            ]
        };

        Assert.Single(settings.ToEngine().When);
        Assert.Contains(settings.Check(), p => p.Contains("more than once"));
    }

    // ------------------------------------------------------------------------ The clone

    [Fact]
    public void ACloneCarriesEverythingAndIsSeparate()
    {
        Hl7IntegrationSettings original = Configured();
        Hl7IntegrationSettings copy = original.Clone();

        // Equal in every respect the serializer can see, which is the whole of the file.
        Assert.Equal(
            JsonSerializer.Serialize(original), JsonSerializer.Serialize(copy));

        copy.Destinations[0].Name = "Changed";
        copy.Rules[0].When[0].Value = "ADT";
        copy.Destinations.Add(new Hl7DestinationSettings());

        Assert.Equal("The RIS", original.Destinations[0].Name);
        Assert.Equal("ORU", original.Rules[0].When[0].Value);
        Assert.Single(original.Destinations);
    }

    // ----------------------------------------------------------------------- The checks

    [Fact]
    public void ARuleNamingSomethingThatDoesNotExistIsReported()
    {
        Hl7IntegrationSettings settings = Configured();
        settings.Rules[0].Destinations = ["nowhere"];

        IReadOnlyList<string> problems = settings.Check(["sample-oru-r01"]);

        Assert.Contains(problems, p => p.Contains("not a configured destination"));
    }

    [Fact]
    public void ARuleUsingAProfileThatIsNotInstalledIsReported()
    {
        Hl7IntegrationSettings settings = Configured();

        Assert.Contains(settings.Check([]), p => p.Contains("does not exist"));
    }

    [Fact]
    public void TwoDestinationsSharingAnIdentifierAreReported()
    {
        Hl7IntegrationSettings settings = Configured();
        settings.Destinations.Add(new Hl7DestinationSettings
        {
            Id = settings.Destinations[0].Id,
            Name = "A second one",
            Host = "elsewhere.local"
        });

        Assert.Contains(settings.Check(["sample-oru-r01"]), p => p.Contains("share the identifier"));
    }

    [Fact]
    public void AnnouncingWithNothingEnabledIsReported()
    {
        // The worst configuration is the one that looks finished and sends nothing, because
        // nobody goes looking for a message they believe was sent.
        Hl7IntegrationSettings settings = Configured();
        settings.AnnounceConversions = true;
        settings.Rules[0].Enabled = false;

        Assert.Contains(settings.Check(["sample-oru-r01"]), p => p.Contains("nothing would be sent"));
    }

    [Fact]
    public void ADestinationRestrictedToOtherProfilesIsReported()
    {
        Hl7IntegrationSettings settings = Configured();
        settings.Destinations[0].ProfileIds = ["some-other-profile"];

        Assert.Contains(
            settings.Check(["sample-oru-r01"]), p => p.Contains("restricted to other profiles"));
    }

    [Fact]
    public void AcceptingAnyCertificateIsSaidOutLoudEveryTime()
    {
        var destination = new Hl7DestinationSettings
        {
            Name = "Commissioning box",
            Host = "test.local",
            UseTls = true,
            AcceptAnyTlsCertificate = true
        };

        Assert.Contains(destination.Check(), p => p.Contains("far end is not authenticated"));
    }

    // ---------------------------------------------------------------------- The file

    [Fact]
    public void AnAdvisoryIsNotTreatedAsAReasonNotToSend()
    {
        // The TLS advisory is said every time and must not stop a message. Told apart by
        // which method reports it, never by reading its wording: a reworded sentence would
        // otherwise change what gets sent.
        var destination = new Hl7DestinationSettings
        {
            Name = "Commissioning box",
            Host = "test.local",
            UseTls = true,
            AcceptAnyTlsCertificate = true
        };

        Assert.Empty(destination.CheckBlocking());
        Assert.Single(destination.Check());
    }

    [Fact]
    public void ADestinationWithNoHostIsAReasonNotToSend()
    {
        var destination = new Hl7DestinationSettings { Name = "Nowhere", Host = "  " };

        Assert.NotEmpty(destination.CheckBlocking());
    }

    [Fact]
    public void NoFileMeansNothingIsConfiguredAndThatIsNotAProblem()
    {
        var service = new Hl7SettingsService(SettingsPath);
        Hl7IntegrationSettings settings = service.Load();

        Assert.False(settings.IsConfigured);
        Assert.False(settings.AnnounceConversions);
        Assert.Empty(service.LoadProblem);
    }

    [Fact]
    public void WhatIsSavedIsWhatIsReadBack()
    {
        var service = new Hl7SettingsService(SettingsPath);
        Hl7IntegrationSettings saved = Configured();
        saved.AnnounceConversions = true;
        saved.Sequence = IntegrationSequence.DicomThenHl7;

        service.Save(saved);

        var reopened = new Hl7SettingsService(SettingsPath);
        Hl7IntegrationSettings read = reopened.Load();

        Assert.True(read.AnnounceConversions);
        Assert.Equal(IntegrationSequence.DicomThenHl7, read.Sequence);
        Assert.Equal("The RIS", read.Destinations[0].Name);
        Assert.Equal("ORU", read.Rules[0].When[0].Value);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(read));
    }

    [Fact]
    public void AFileThatCannotBeReadIsReportedAndNotTreatedAsEmpty()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var service = new Hl7SettingsService(SettingsPath);
        service.Load();

        Assert.Contains("could not be read", service.LoadProblem);
    }

    [Fact]
    public void AFileFromANewerVersionIsNotOverwritten()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, """
        { "SettingsVersion": 99, "Destinations": [], "Rules": [] }
        """);

        var service = new Hl7SettingsService(SettingsPath);
        service.Load();

        Assert.True(service.IsFromNewerVersion);
        Assert.Throws<InvalidOperationException>(() => service.Save(new Hl7IntegrationSettings()));

        // And the file is still the newer one, untouched.
        Assert.Contains("99", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void ForgettingRemovesTheFileRatherThanLeavingAnEmptyOne()
    {
        var service = new Hl7SettingsService(SettingsPath);
        service.Save(Configured());

        Assert.True(File.Exists(SettingsPath));

        service.Forget();

        Assert.False(File.Exists(SettingsPath));
        Assert.False(service.Current.IsConfigured);
    }

    // ------------------------------------------------------------------- The profiles

    [Fact]
    public void AUsableProfileIsInstalledUnderItsOwnIdentifier()
    {
        var service = new Hl7SettingsService(SettingsPath);
        string source = WriteProfile("anywhere.json", SampleProfile);

        ProfileImportResult result = service.Profiles.Import(source);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("sample-oru-r01", result.ProfileId);

        StoredProfile stored = Assert.Single(service.Profiles.List());
        Assert.Equal("sample-oru-r01", stored.ProfileId);
        Assert.Equal("ORU^R01", stored.MessageType);
        Assert.True(stored.IsUsable);
        Assert.EndsWith("sample-oru-r01.json", stored.Path);
    }

    [Fact]
    public void AProfileThatCannotBeUsedIsRefusedWithTheReason()
    {
        var service = new Hl7SettingsService(SettingsPath);

        // No message type, which the reader objects to.
        string source = WriteProfile("broken.json", """
        { "profileId": "broken", "segments": [], "fields": [], "mappings": [] }
        """);

        ProfileImportResult result = service.Profiles.Import(source);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Detail);
        Assert.Empty(service.Profiles.List());
    }

    [Fact]
    public void ImportingTheSameProfileTwiceNeedsSayingSo()
    {
        var service = new Hl7SettingsService(SettingsPath);
        string source = WriteProfile("anywhere.json", SampleProfile);

        Assert.True(service.Profiles.Import(source).Succeeded);

        ProfileImportResult second = service.Profiles.Import(source);
        Assert.False(second.Succeeded);
        Assert.Contains("already installed", second.Detail);

        Assert.True(service.Profiles.Import(source, overwrite: true).Succeeded);
        Assert.Single(service.Profiles.List());
    }

    [Fact]
    public void AProfileBrokenByHandIsListedWithItsObjectionRatherThanHidden()
    {
        var service = new Hl7SettingsService(SettingsPath);
        Directory.CreateDirectory(service.Profiles.Folder);
        File.WriteAllText(Path.Combine(service.Profiles.Folder, "edited.json"), "{ not json");

        StoredProfile stored = Assert.Single(service.Profiles.List());

        Assert.False(stored.IsUsable);
        Assert.NotEmpty(stored.Issues);
    }

    [Fact]
    public void AProfileCanBeFoundByIdentifierAndRemoved()
    {
        var service = new Hl7SettingsService(SettingsPath);
        service.Profiles.Import(WriteProfile("anywhere.json", SampleProfile));

        Assert.NotNull(service.Profiles.Find("sample-oru-r01"));
        Assert.Null(service.Profiles.Find("not-installed"));

        Assert.True(service.Profiles.Remove(service.Profiles.List()[0]));
        Assert.Empty(service.Profiles.List());
    }

    // ----------------------------------------------------------------------- The plan

    [Fact]
    public void NothingIsPlannedUntilSomebodyTurnsItOn()
    {
        var service = new Hl7SettingsService(SettingsPath);
        service.Profiles.Import(WriteProfile("p.json", SampleProfile));

        Hl7IntegrationSettings settings = Configured();
        settings.AnnounceConversions = false;

        Hl7PlanResult result = Hl7PlanFactory.Build(
            settings, service.Profiles, Demographics());

        Assert.False(result.WillSend);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void AMatchingRuleProducesOneDeliveryAndSaysWhichRuleDecided()
    {
        var service = new Hl7SettingsService(SettingsPath);
        service.Profiles.Import(WriteProfile("p.json", SampleProfile));

        Hl7IntegrationSettings settings = Configured();
        settings.AnnounceConversions = true;

        Hl7PlanResult result = Hl7PlanFactory.Build(
            settings, service.Profiles, Demographics());

        Assert.Empty(result.Problems);
        Hl7Delivery delivery = Assert.Single(result.Plan.Deliveries);
        Assert.Equal("The RIS", delivery.Destination.Name);
        Assert.Equal("sample-oru-r01", delivery.Profile.ProfileId);
        Assert.Contains(result.Explanations, e => e.Contains("Reports to the RIS"));
    }

    [Fact]
    public void ARuleWhoseConditionDoesNotHoldSendsNothingAndComplainsAboutNothing()
    {
        var service = new Hl7SettingsService(SettingsPath);
        service.Profiles.Import(WriteProfile("p.json", SampleProfile));

        Hl7IntegrationSettings settings = Configured();
        settings.AnnounceConversions = true;
        settings.Rules[0].When = [new Hl7ConditionSettings { Key = "modality", Value = "CT" }];

        Hl7PlanResult result = Hl7PlanFactory.Build(
            settings, service.Profiles, Demographics(), modality: "OT");

        Assert.False(result.WillSend);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ADisabledDestinationIsReportedRatherThanSkippedQuietly()
    {
        var service = new Hl7SettingsService(SettingsPath);
        service.Profiles.Import(WriteProfile("p.json", SampleProfile));

        Hl7IntegrationSettings settings = Configured();
        settings.AnnounceConversions = true;
        settings.Destinations[0].IsEnabled = false;

        Hl7PlanResult result = Hl7PlanFactory.Build(
            settings, service.Profiles, Demographics());

        Assert.False(result.WillSend);
        Assert.Contains(result.Problems, p => p.Contains("which is disabled"));
    }

    [Fact]
    public void AMissingProfileIsReportedRatherThanSkippedQuietly()
    {
        var service = new Hl7SettingsService(SettingsPath);

        Hl7IntegrationSettings settings = Configured();
        settings.AnnounceConversions = true;

        Hl7PlanResult result = Hl7PlanFactory.Build(
            settings, service.Profiles, Demographics());

        Assert.False(result.WillSend);
        Assert.Contains(result.Problems, p => p.Contains("not installed"));
    }

    [Fact]
    public void TwoRulesNamingTheSameDestinationDeliverOnce()
    {
        // A duplicate in a patient's record, not a tidiness problem. Two overlapping rules
        // are easy to write by accident and must never mean two messages.
        var service = new Hl7SettingsService(SettingsPath);
        service.Profiles.Import(WriteProfile("p.json", SampleProfile));

        Hl7IntegrationSettings settings = Configured();
        settings.AnnounceConversions = true;
        settings.Rules.Add(new Hl7RoutingRuleSettings
        {
            Name = "Everything else",
            Destinations = [settings.Destinations[0].Id],
            ProfileId = "sample-oru-r01"
        });

        Hl7PlanResult result = Hl7PlanFactory.Build(
            settings, service.Profiles, Demographics());

        Assert.Single(result.Plan.Deliveries);
        Assert.Contains(result.Explanations, e => e.Contains("Reports to the RIS"));
    }

    [Fact]
    public void TheMetadataARuleMatchesOnCarriesNoPatientIdentifier()
    {
        // Routing on a patient identifier is not routing. It is one patient's reports going
        // somewhere different from everybody else's, which is a mistake nobody should be
        // able to configure.
        var service = new Hl7SettingsService(SettingsPath);
        service.Profiles.Import(WriteProfile("p.json", SampleProfile));

        IReadOnlyDictionary<string, string> metadata = Hl7PlanFactory.MetadataFor(
            Demographics(), service.Profiles.Find("sample-oru-r01")!);

        Assert.DoesNotContain("0000000077", metadata.Values);
        Assert.Equal("ORU", metadata["messageType"]);
        Assert.Equal("R01", metadata["triggerEvent"]);
        Assert.Equal("yes", metadata["hasAccessionNumber"]);
        Assert.All(Hl7PlanFactory.ConditionKeys, key => Assert.True(metadata.ContainsKey(key)));
    }

    [Fact]
    public void TheSequenceTheSiteChoseReachesThePlan()
    {
        var service = new Hl7SettingsService(SettingsPath);
        service.Profiles.Import(WriteProfile("p.json", SampleProfile));

        Hl7IntegrationSettings settings = Configured();
        settings.AnnounceConversions = true;
        settings.Sequence = IntegrationSequence.Hl7ThenDicom;

        Hl7PlanResult result = Hl7PlanFactory.Build(
            settings, service.Profiles, Demographics());

        Assert.Equal(IntegrationSequence.Hl7ThenDicom, result.Plan.Sequence);
    }

    // ------------------------------------------------------------------------ Fixtures

    private static HashSet<string> PropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static PatientDemographics Demographics() => new()
    {
        PatientId = "0000000077",
        FamilyName = "Silva",
        GivenName = "Maria",
        AccessionNumber = "ACC001",
        StudyDescription = "Chest radiograph",
        DocumentTitle = "Report"
    };

    private static Hl7IntegrationSettings Configured()
    {
        var destination = new Hl7DestinationSettings
        {
            Id = "dest-1",
            Name = "The RIS",
            Host = "ris.hospital.local",
            Port = 2575,
            ReceivingApplication = "RIS",
            ReceivingFacility = "HOSPITAL"
        };

        return new Hl7IntegrationSettings
        {
            Destinations = [destination],
            Rules =
            [
                new Hl7RoutingRuleSettings
                {
                    Name = "Reports to the RIS",
                    When = [new Hl7ConditionSettings { Key = "messageType", Value = "ORU" }],
                    Destinations = [destination.Id],
                    ProfileId = "sample-oru-r01"
                }
            ]
        };
    }

    private string WriteProfile(string name, string json)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, json);
        return path;
    }

    private const string SampleProfile = """
    {
      "profileId": "sample-oru-r01",
      "displayName": "Sample ORU",
      "revision": 1,
      "messageType": "ORU",
      "triggerEvent": "R01",
      "messageStructure": "ORU_R01",
      "segments": [
        { "id": "MSH", "usage": "R", "min": 1, "max": 1 },
        { "id": "PID", "usage": "R", "min": 1, "max": 1 }
      ],
      "fields": [
        { "path": "PID-3.1", "name": "Patient identifier", "usage": "R" }
      ],
      "mappings": [
        { "target": "PID-3.1", "source": "patient.id", "required": true }
      ]
    }
    """;
}
