using System.Text.Json;
using PdfToDicom.Core.Configuration;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Exporting the archives and worklist servers from one workstation and importing them on
/// another.
/// </summary>
/// <remarks>
/// <para>
/// The reason to test this closely is that the failure is quiet and clinical. An import
/// that adds a second entry pointing at the same archive leaves two identically named
/// servers in a list somebody picks from under pressure; one that carries a calling AE
/// Title across gives two workstations the same identity on the hospital network; one that
/// drops the file's own settings loses a connection agreed with another department. None of
/// those announces itself at the time.
/// </para>
/// <para>
/// Everything here runs against a real file in a temporary folder. The point of the feature
/// is a file carried between machines, so a test that never writes one would not be testing
/// the feature.
/// </para>
/// </remarks>
public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "PdfToDicomTransfer-" + Guid.NewGuid().ToString("N"));

    public SettingsTransferTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathFor(string name) => Path.Combine(_folder, name);

    private static AppConfiguration ConfiguredWorkstation() => new()
    {
        CallingAeTitle = "WARD3PC",
        InstitutionName = "Hospital de Exemplo",
        Destinations =
        {
            new PacsDestination
            {
                Id = "aaaa", Name = "Main PACS", CalledAeTitle = "PACS",
                Host = "192.168.10.20", Port = 104,
                LastSuccessfulUseUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            },
            new PacsDestination
            {
                Id = "bbbb", Name = "Cardiology archive", CalledAeTitle = "CARDIO",
                Host = "192.168.10.30", Port = 11112
            }
        },
        Worklists =
        {
            new WorklistConfiguration
            {
                Id = "cccc", Name = "Radiology", CalledAeTitle = "WORKLIST",
                Host = "192.168.10.40", Port = 105, IsEnabled = true
            }
        }
    };

    // ----------------------------------------------------------------- Round trip

    [Fact]
    public void BothKindsSurviveAFileRoundTrip()
    {
        AppConfiguration source = ConfiguredWorkstation();
        string file = PathFor("both" + SettingsTransfer.FileExtension);

        SettingsTransfer.Write(
            SettingsTransfer.CreateBundle(source, SettingsTransferParts.Both, "WARD3PC", "3.5.0"),
            file);

        SettingsBundleRead read = SettingsTransfer.Read(file);

        Assert.True(read.Succeeded, read.Problem);
        Assert.Equal(2, read.Bundle!.Destinations.Count);
        Assert.Single(read.Bundle.Worklists);
        Assert.Equal("Main PACS", read.Bundle.Destinations[0].Name);
        Assert.Equal(11112, read.Bundle.Destinations[1].Port);
        Assert.Equal("WORKLIST", read.Bundle.Worklists[0].CalledAeTitle);
        Assert.Equal("WARD3PC", read.Bundle.ExportedFrom);
        Assert.Equal("3.5.0", read.Bundle.ApplicationVersion);
    }

    [Fact]
    public void OnlyWhatWasAskedForIsExported()
    {
        AppConfiguration source = ConfiguredWorkstation();

        SettingsBundle destinationsOnly = SettingsTransfer.CreateBundle(
            source, SettingsTransferParts.Destinations, "PC", "3.5.0");
        SettingsBundle worklistsOnly = SettingsTransfer.CreateBundle(
            source, SettingsTransferParts.Worklists, "PC", "3.5.0");

        Assert.Equal(2, destinationsOnly.Destinations.Count);
        Assert.Empty(destinationsOnly.Worklists);
        Assert.Equal(SettingsTransferParts.Destinations, destinationsOnly.Parts);

        Assert.Empty(worklistsOnly.Destinations);
        Assert.Single(worklistsOnly.Worklists);
        Assert.Equal(SettingsTransferParts.Worklists, worklistsOnly.Parts);
    }

    [Fact]
    public void TheExportedFileCarriesNoLocalIdentityOrHistory()
    {
        // The calling AE Title is the one setting that must not travel: two workstations
        // sharing it is the complaint a PACS administrator will make, and it would have
        // happened silently. The last successful transmission is a record of what the
        // source machine did, not a setting.
        AppConfiguration source = ConfiguredWorkstation();
        string file = PathFor("identity" + SettingsTransfer.FileExtension);

        SettingsTransfer.Write(
            SettingsTransfer.CreateBundle(source, SettingsTransferParts.Both, "WARD3PC", "3.5.0"),
            file);

        string json = File.ReadAllText(file);

        Assert.DoesNotContain("CallingAeTitle\": \"WARD3PC", json);
        Assert.DoesNotContain("Hospital de Exemplo", json);
        Assert.All(
            SettingsTransfer.Read(file).Bundle!.Destinations,
            destination => Assert.Null(destination.LastSuccessfulUseUtc));
    }

    [Fact]
    public void BuildingABundleDoesNotTieItToTheLiveConfiguration()
    {
        AppConfiguration source = ConfiguredWorkstation();
        SettingsBundle bundle = SettingsTransfer.CreateBundle(
            source, SettingsTransferParts.Both, "PC", "3.5.0");

        source.Destinations[0].Host = "10.0.0.1";
        source.Worklists[0].Name = "Renamed after the export";

        Assert.Equal("192.168.10.20", bundle.Destinations[0].Host);
        Assert.Equal("Radiology", bundle.Worklists[0].Name);
    }

    // ----------------------------------------------------------- Adding to existing

    [Fact]
    public void AddingBringsInWhatIsMissingAndLeavesTheRest()
    {
        SettingsBundle bundle = SettingsTransfer.CreateBundle(
            ConfiguredWorkstation(), SettingsTransferParts.Both, "PC", "3.5.0");

        var target = new AppConfiguration
        {
            Destinations =
            {
                new PacsDestination
                {
                    Id = "local", Name = "The archive here", CalledAeTitle = "PACS",
                    Host = "192.168.10.20", Port = 104
                }
            }
        };

        SettingsImportReport report = SettingsTransfer.Apply(
            bundle, target, SettingsTransferParts.Both, SettingsImportMode.AddToExisting);

        Assert.Equal(1, report.DestinationsAdded);
        Assert.Equal(1, report.DestinationsAlreadyPresent);
        Assert.Equal(0, report.DestinationsRemoved);
        Assert.Equal(1, report.WorklistsAdded);
        Assert.False(report.ChangedNothing);

        // The entry that was already here keeps its own name and identifier.
        Assert.Equal(2, target.Destinations.Count);
        Assert.Equal("The archive here", target.Destinations[0].Name);
        Assert.Equal("local", target.Destinations[0].Id);
        Assert.Equal("Cardiology archive", target.Destinations[1].Name);
    }

    [Fact]
    public void TheSameServerIsRecognisedThroughCaseAndStrayPunctuationAndSpaces()
    {
        var bundle = new SettingsBundle
        {
            Destinations =
            {
                new PacsDestination { Name = "Main PACS", CalledAeTitle = "pacs", Host = " 192.168.10.20 ", Port = 104 }
            }
        };

        var target = new AppConfiguration
        {
            Destinations =
            {
                new PacsDestination { Name = "Main PACS", CalledAeTitle = "PACS", Host = "192.168.10.20", Port = 104 }
            }
        };

        SettingsImportReport report = SettingsTransfer.Apply(
            bundle, target, SettingsTransferParts.Destinations, SettingsImportMode.AddToExisting);

        Assert.Equal(0, report.DestinationsAdded);
        Assert.Equal(1, report.DestinationsAlreadyPresent);
        Assert.Single(target.Destinations);
    }

    [Fact]
    public void ADifferentPortIsADifferentServer()
    {
        var bundle = new SettingsBundle
        {
            Destinations =
            {
                new PacsDestination { Name = "Main PACS", CalledAeTitle = "PACS", Host = "192.168.10.20", Port = 11112 }
            }
        };

        var target = new AppConfiguration
        {
            Destinations =
            {
                new PacsDestination { Name = "Main PACS", CalledAeTitle = "PACS", Host = "192.168.10.20", Port = 104 }
            }
        };

        SettingsTransfer.Apply(bundle, target, SettingsTransferParts.Destinations, SettingsImportMode.AddToExisting);

        Assert.Equal(2, target.Destinations.Count);
        Assert.Equal(104, target.Destinations[0].Port);
        Assert.Equal(11112, target.Destinations[1].Port);
    }

    [Fact]
    public void AnImportedEntryWhoseNameIsTakenIsNumberedRatherThanLeftAmbiguous()
    {
        var bundle = new SettingsBundle
        {
            Destinations =
            {
                new PacsDestination { Name = "Main PACS", CalledAeTitle = "PACS2", Host = "10.1.1.1", Port = 104 }
            }
        };

        var target = new AppConfiguration
        {
            Destinations =
            {
                new PacsDestination { Name = "Main PACS", CalledAeTitle = "PACS", Host = "10.2.2.2", Port = 104 }
            }
        };

        SettingsTransfer.Apply(bundle, target, SettingsTransferParts.Destinations, SettingsImportMode.AddToExisting);

        Assert.Equal("Main PACS", target.Destinations[0].Name);
        Assert.Equal("Main PACS (2)", target.Destinations[1].Name);

        // And the address, which is what actually matters, is untouched.
        Assert.Equal("10.1.1.1", target.Destinations[1].Host);
    }

    [Fact]
    public void ImportingTheSameFileTwiceChangesNothingTheSecondTime()
    {
        // A file left on a shared drive gets imported again by the next person to set a
        // machine up. Doing it twice must not double the list.
        SettingsBundle bundle = SettingsTransfer.CreateBundle(
            ConfiguredWorkstation(), SettingsTransferParts.Both, "PC", "3.5.0");

        var target = new AppConfiguration();

        SettingsTransfer.Apply(bundle, target, SettingsTransferParts.Both, SettingsImportMode.AddToExisting);
        SettingsImportReport second = SettingsTransfer.Apply(
            bundle, target, SettingsTransferParts.Both, SettingsImportMode.AddToExisting);

        Assert.True(second.ChangedNothing);
        Assert.Equal(2, second.DestinationsAlreadyPresent);
        Assert.Equal(1, second.WorklistsAlreadyPresent);
        Assert.Equal(2, target.Destinations.Count);
        Assert.Single(target.Worklists);
    }

    [Fact]
    public void EveryImportedEntryGetsItsOwnIdentifier()
    {
        // Identifiers in the file belong to the machine it came from. Two entries sharing
        // one would make "the worklist used last" ambiguous.
        var bundle = new SettingsBundle
        {
            Worklists =
            {
                new WorklistConfiguration { Id = "same", Name = "A", CalledAeTitle = "WL1", Host = "10.1.1.1", Port = 104 },
                new WorklistConfiguration { Id = "same", Name = "B", CalledAeTitle = "WL2", Host = "10.1.1.2", Port = 104 }
            }
        };

        var target = new AppConfiguration();
        SettingsTransfer.Apply(bundle, target, SettingsTransferParts.Worklists, SettingsImportMode.AddToExisting);

        Assert.Equal(2, target.Worklists.Count);
        Assert.NotEqual(target.Worklists[0].Id, target.Worklists[1].Id);
        Assert.All(target.Worklists, worklist => Assert.NotEqual("same", worklist.Id));
    }

    // --------------------------------------------------------------- Replacing

    [Fact]
    public void ReplacingDiscardsWhatWasThereAndReportsHowMuch()
    {
        SettingsBundle bundle = SettingsTransfer.CreateBundle(
            ConfiguredWorkstation(), SettingsTransferParts.Destinations, "PC", "3.5.0");

        var target = new AppConfiguration
        {
            Destinations =
            {
                new PacsDestination { Name = "Old one", CalledAeTitle = "OLD", Host = "10.9.9.9", Port = 104 },
                new PacsDestination { Name = "Another old one", CalledAeTitle = "OLD2", Host = "10.9.9.8", Port = 104 }
            }
        };

        SettingsImportReport report = SettingsTransfer.Apply(
            bundle, target, SettingsTransferParts.Destinations, SettingsImportMode.ReplaceExisting);

        Assert.Equal(2, report.DestinationsRemoved);
        Assert.Equal(2, report.DestinationsAdded);
        Assert.Equal(2, target.Destinations.Count);
        Assert.DoesNotContain(target.Destinations, destination => destination.CalledAeTitle == "OLD");
    }

    [Fact]
    public void ReplacingOneKindLeavesTheOtherKindAlone()
    {
        SettingsBundle bundle = SettingsTransfer.CreateBundle(
            ConfiguredWorkstation(), SettingsTransferParts.Both, "PC", "3.5.0");

        var target = new AppConfiguration
        {
            Worklists =
            {
                new WorklistConfiguration { Id = "keep", Name = "Local worklist", CalledAeTitle = "WL", Host = "10.8.8.8", Port = 104 }
            }
        };

        SettingsTransfer.Apply(
            bundle, target, SettingsTransferParts.Destinations, SettingsImportMode.ReplaceExisting);

        Assert.Single(target.Worklists);
        Assert.Equal("keep", target.Worklists[0].Id);
        Assert.Equal(2, target.Destinations.Count);
    }

    [Fact]
    public void ReplacingWorklistsForgetsAServerThatIsNoLongerThere()
    {
        SettingsBundle bundle = SettingsTransfer.CreateBundle(
            ConfiguredWorkstation(), SettingsTransferParts.Worklists, "PC", "3.5.0");

        var target = new AppConfiguration
        {
            LastUsedWorklistId = "gone",
            Worklists =
            {
                new WorklistConfiguration { Id = "gone", Name = "Old worklist", CalledAeTitle = "WL", Host = "10.8.8.8", Port = 104 }
            }
        };

        SettingsTransfer.Apply(
            bundle, target, SettingsTransferParts.Worklists, SettingsImportMode.ReplaceExisting);

        Assert.Equal(string.Empty, target.LastUsedWorklistId);
    }

    [Fact]
    public void AddingWorklistsKeepsTheOneRememberedAsLastUsed()
    {
        SettingsBundle bundle = SettingsTransfer.CreateBundle(
            ConfiguredWorkstation(), SettingsTransferParts.Worklists, "PC", "3.5.0");

        var target = new AppConfiguration
        {
            LastUsedWorklistId = "mine",
            Worklists =
            {
                new WorklistConfiguration { Id = "mine", Name = "Mine", CalledAeTitle = "WL", Host = "10.8.8.8", Port = 104 }
            }
        };

        SettingsTransfer.Apply(
            bundle, target, SettingsTransferParts.Worklists, SettingsImportMode.AddToExisting);

        Assert.Equal("mine", target.LastUsedWorklistId);
    }

    // ------------------------------------------------------------ Bad files

    [Fact]
    public void AFileThatIsNotOursIsRefusedByName()
    {
        string file = PathFor("something-else.json");
        File.WriteAllText(file, """{ "Destinations": [ { "Name": "Looks plausible" } ] }""");

        SettingsBundleRead read = SettingsTransfer.Read(file);

        Assert.False(read.Succeeded);
        Assert.Contains("not exported by PDF to DICOM", read.Problem);
    }

    [Fact]
    public void AFileFromANewerVersionSaysSoRatherThanGuessing()
    {
        string file = PathFor("newer" + SettingsTransfer.FileExtension);
        File.WriteAllText(file, $$"""
        {
          "FileKind": "{{SettingsTransfer.FileKind}}",
          "FormatVersion": {{SettingsTransfer.CurrentFormatVersion + 1}},
          "Destinations": [ { "Name": "Main PACS", "CalledAeTitle": "PACS", "Host": "10.0.0.1", "Port": 104 } ]
        }
        """);

        SettingsBundleRead read = SettingsTransfer.Read(file);

        Assert.False(read.Succeeded);
        Assert.Contains("newer version", read.Problem);
    }

    [Fact]
    public void DamagedJsonIsReportedRatherThanThrown()
    {
        string file = PathFor("truncated" + SettingsTransfer.FileExtension);
        File.WriteAllText(file, """{ "FileKind": "PdfToDicom.Settings", "Destinations": [ {""");

        SettingsBundleRead read = SettingsTransfer.Read(file);

        Assert.False(read.Succeeded);
        Assert.Contains("not a valid settings file", read.Problem);
    }

    [Fact]
    public void AMissingFileIsReportedRatherThanThrown()
    {
        SettingsBundleRead read = SettingsTransfer.Read(PathFor("never-written.json"));

        Assert.False(read.Succeeded);
        Assert.Contains("no file at", read.Problem);
    }

    [Fact]
    public void AnEmptyExportIsRefusedWithAReason()
    {
        string file = PathFor("empty" + SettingsTransfer.FileExtension);
        SettingsTransfer.Write(
            SettingsTransfer.CreateBundle(new AppConfiguration(), SettingsTransferParts.Both, "PC", "3.5.0"),
            file);

        SettingsBundleRead read = SettingsTransfer.Read(file);

        Assert.False(read.Succeeded);
        Assert.Contains("nothing to import", read.Problem);
    }

    [Fact]
    public void AFileWithNumbersOutOfRangeIsBroughtInsideTheAllowedRange()
    {
        // A hand edited file, or one written by a script. The numbers are clamped as the
        // configuration service clamps them when saving, so nothing unusable is shown in
        // the settings dialog as though it would work.
        string file = PathFor("outofrange" + SettingsTransfer.FileExtension);
        File.WriteAllText(file, $$"""
        {
          "FileKind": "{{SettingsTransfer.FileKind}}",
          "FormatVersion": 1,
          "Destinations": [
            { "Name": "Odd", "CalledAeTitle": "PACS", "Host": "10.0.0.1", "Port": 0,
              "TimeoutSeconds": 99999, "RetryCount": -4, "RetryDelaySeconds": 9000 }
          ],
          "Worklists": [
            { "Name": "Odd", "CalledAeTitle": "WL", "Host": "10.0.0.2", "Port": 70000,
              "TimeoutSeconds": 0, "MaximumResults": 1000000 }
          ]
        }
        """);

        SettingsBundleRead read = SettingsTransfer.Read(file);
        Assert.True(read.Succeeded, read.Problem);

        var target = new AppConfiguration();
        SettingsTransfer.Apply(read.Bundle!, target, SettingsTransferParts.Both, SettingsImportMode.AddToExisting);

        Assert.Equal(1, target.Destinations[0].Port);
        Assert.Equal(600, target.Destinations[0].TimeoutSeconds);
        Assert.Equal(0, target.Destinations[0].RetryCount);
        Assert.Equal(120, target.Destinations[0].RetryDelaySeconds);

        Assert.Equal(65535, target.Worklists[0].Port);
        Assert.Equal(1, target.Worklists[0].TimeoutSeconds);
        Assert.Equal(5_000, target.Worklists[0].MaximumResults);
    }

    // ------------------------------------------------- Carrying every setting

    [Fact]
    public void EverySettingOfAnEntrySurvivesTheRoundTrip()
    {
        // The same guard the configuration clone has, and for the same reason: this once
        // dropped three settings because a copy was written property by property and a
        // later property was never added to it. Compared through the serializer so a
        // property added tomorrow is covered without anybody remembering this test.
        var destination = new PacsDestination
        {
            Id = "zzzz", Name = "Everything set", CalledAeTitle = "PACS", Host = "10.0.0.1",
            Port = 11112, CallingAeTitleOverride = "OVERRIDE", IsEnabled = false,
            VerifyBeforeSend = false, TimeoutSeconds = 45, RetryCount = 5,
            RetryDelaySeconds = 7, UseTls = true, AcceptAnyTlsCertificate = true
        };

        var worklist = new WorklistConfiguration
        {
            Id = "yyyy", Name = "Everything set", IsEnabled = true, CalledAeTitle = "WL",
            Host = "10.0.0.2", Port = 105, CallingAeTitleOverride = "OVERRIDE",
            Modality = "CR", ScheduledStationAeTitle = "ROOM1",
            DateRange = WorklistDateRange.Explicit,
            ExplicitFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExplicitTo = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            TimeoutSeconds = 55, UseTls = true, AcceptAnyTlsCertificate = true,
            MaximumResults = 250
        };

        var source = new AppConfiguration { Destinations = { destination }, Worklists = { worklist } };
        string file = PathFor("full" + SettingsTransfer.FileExtension);

        SettingsTransfer.Write(
            SettingsTransfer.CreateBundle(source, SettingsTransferParts.Both, "PC", "3.5.0"),
            file);

        var target = new AppConfiguration();
        SettingsTransfer.Apply(
            SettingsTransfer.Read(file).Bundle!, target, SettingsTransferParts.Both,
            SettingsImportMode.AddToExisting);

        // The identifier is deliberately regenerated, and the name is only numbered when
        // it collides, so both are normalised away before the comparison.
        destination.Id = target.Destinations[0].Id;
        worklist.Id = target.Worklists[0].Id;

        Assert.Equal(Json(destination), Json(target.Destinations[0]));
        Assert.Equal(Json(worklist), Json(target.Worklists[0]));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(
        value,
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = null });
}
