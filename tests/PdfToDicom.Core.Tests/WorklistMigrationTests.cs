using System.Text.Json;
using PdfToDicom.Core.Configuration;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// That a site which configured one worklist server before this version keeps it.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the real <see cref="ConfigurationService.Load"/> rather than the
/// migration method on its own, because the parts that could go wrong are the ones around
/// it: whether the identifier and name get filled in afterwards, and whether the file is
/// written back so the old key stops being carried.
/// </para>
/// <para>
/// This matters more than most migrations. A worklist connection is agreed with another
/// department and tested against a live server; losing it silently means somebody retypes
/// an AE Title from memory, and a wrong AE Title offers the wrong patients.
/// </para>
/// </remarks>
public sealed class WorklistMigrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "PdfToDicomMigrate-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_folder, "config.json");

    public WorklistMigrationTests() => Directory.CreateDirectory(_folder);

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

    [Fact]
    public void ASingleWorklistBecomesTheFirstEntryInTheList()
    {
        GivenConfiguration("""
        {
          "CallingAeTitle": "PDF2DCM",
          "Worklist": {
            "IsEnabled": true,
            "CalledAeTitle": "RADWL",
            "Host": "192.168.10.40",
            "Port": 1041,
            "Modality": "OT",
            "DateRange": "LastSevenDays",
            "MaximumResults": 250
          }
        }
        """);

        AppConfiguration loaded = new ConfigurationService(ConfigPath).Load();

        WorklistConfiguration migrated = Assert.Single(loaded.Worklists);

        Assert.True(migrated.IsEnabled);
        Assert.Equal("RADWL", migrated.CalledAeTitle);
        Assert.Equal("192.168.10.40", migrated.Host);
        Assert.Equal(1041, migrated.Port);
        Assert.Equal("OT", migrated.Modality);
        Assert.Equal(WorklistDateRange.LastSevenDays, migrated.DateRange);
        Assert.Equal(250, migrated.MaximumResults);
    }

    [Fact]
    public void TheMigratedServerIsUsableAsAListEntry()
    {
        // An entry with no identifier cannot be remembered as the one last used, and one
        // with no name cannot be picked on purpose.
        GivenConfiguration("""
        { "Worklist": { "IsEnabled": true, "CalledAeTitle": "RADWL", "Host": "10.0.0.5" } }
        """);

        WorklistConfiguration migrated = Assert.Single(new ConfigurationService(ConfigPath).Load().Worklists);

        Assert.NotEmpty(migrated.Id);
        Assert.NotEmpty(migrated.Name);
    }

    [Fact]
    public void TheMigrationMakesTheWorklistAvailableAgain()
    {
        // The whole point. Before the write-back was in place this was the symptom: the
        // server was configured, and the button was not there.
        GivenConfiguration("""
        { "Worklist": { "IsEnabled": true, "CalledAeTitle": "RADWL", "Host": "10.0.0.5" } }
        """);

        Assert.True(new ConfigurationService(ConfigPath).Load().IsWorklistAvailable);
    }

    [Fact]
    public void TheOldKeyIsNotCarriedOnceItHasBeenMigrated()
    {
        GivenConfiguration("""
        { "Worklist": { "IsEnabled": true, "CalledAeTitle": "RADWL", "Host": "10.0.0.5" } }
        """);

        new ConfigurationService(ConfigPath).Load();

        using JsonDocument written = JsonDocument.Parse(File.ReadAllText(ConfigPath));

        Assert.False(written.RootElement.TryGetProperty("Worklist", out _));
        Assert.True(written.RootElement.TryGetProperty("Worklists", out JsonElement list));
        Assert.Equal(1, list.GetArrayLength());
    }

    [Fact]
    public void MigratingTwiceDoesNotProduceTwoCopies()
    {
        GivenConfiguration("""
        { "Worklist": { "IsEnabled": true, "CalledAeTitle": "RADWL", "Host": "10.0.0.5" } }
        """);

        new ConfigurationService(ConfigPath).Load();
        AppConfiguration second = new ConfigurationService(ConfigPath).Load();

        Assert.Single(second.Worklists);
    }

    [Fact]
    public void AStaleOldKeyBesideANewListIsIgnored()
    {
        // A file written by this version that still carries the old key: importing it would
        // resurrect a server somebody had deleted.
        GivenConfiguration("""
        {
          "Worklists": [ { "Id": "a", "Name": "Cardiology", "IsEnabled": true, "Host": "10.0.0.9" } ],
          "Worklist": { "IsEnabled": true, "CalledAeTitle": "DELETED", "Host": "10.0.0.5" }
        }
        """);

        WorklistConfiguration only = Assert.Single(new ConfigurationService(ConfigPath).Load().Worklists);

        Assert.Equal("Cardiology", only.Name);
    }

    [Fact]
    public void AFileWithNoWorklistAtAllMigratesToNothing()
    {
        GivenConfiguration("""{ "CallingAeTitle": "PDF2DCM" }""");

        AppConfiguration loaded = new ConfigurationService(ConfigPath).Load();

        Assert.Empty(loaded.Worklists);
        Assert.False(loaded.IsWorklistAvailable);
    }

    [Fact]
    public void ANonsenseOldKeyIsNotFatal()
    {
        // The configuration is valid JSON, so it is not quarantined; the legacy node is
        // simply not an object. Refusing to start over an unusable worklist would be absurd.
        GivenConfiguration("""{ "Worklist": "RADWL" }""");

        AppConfiguration loaded = new ConfigurationService(ConfigPath).Load();

        Assert.Empty(loaded.Worklists);
    }

    [Fact]
    public void TheMigratedServerSurvivesACopyOfTheConfiguration()
    {
        // The settings dialog edits a copy, so a server that does not survive copying is a
        // server that disappears the first time somebody presses Save.
        GivenConfiguration("""
        { "Worklist": { "IsEnabled": true, "CalledAeTitle": "RADWL", "Host": "10.0.0.5", "Port": 1041 } }
        """);

        AppConfiguration copy = new ConfigurationService(ConfigPath).Load().Clone();
        WorklistConfiguration server = Assert.Single(copy.Worklists);

        Assert.Equal("RADWL", server.CalledAeTitle);
        Assert.Equal(1041, server.Port);
        Assert.True(copy.IsWorklistAvailable);
    }

    [Fact]
    public void ADamagedFileStartsOnDefaultsAndSaysSo()
    {
        // The behaviour is right: the application always starts. What was missing is the
        // saying so. A site whose file had been hand edited into invalid JSON would find
        // every destination and every worklist gone, with nothing anywhere to explain it.
        GivenConfiguration("{ \"Worklists\": [ { \"Name\": \"Radiology\" ");

        var service = new ConfigurationService(ConfigPath);
        AppConfiguration loaded = service.Load();

        Assert.Empty(loaded.Worklists);
        Assert.NotEmpty(service.LoadProblem);
        Assert.Contains("worklist", service.LoadProblem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADamagedFileIsKeptRatherThanDiscarded()
    {
        GivenConfiguration("{ not json at all ");

        var service = new ConfigurationService(ConfigPath);
        service.Load();

        string[] kept = Directory.GetFiles(_folder, "*.corrupt-*");

        Assert.NotEmpty(kept);
        Assert.Contains(Path.GetFileName(kept.First(f => !f.EndsWith(".txt", StringComparison.Ordinal))),
            service.LoadProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatReadsCleanlyReportsNoProblem()
    {
        GivenConfiguration("""{ "CallingAeTitle": "PDF2DCM" }""");

        var service = new ConfigurationService(ConfigPath);
        service.Load();

        Assert.Empty(service.LoadProblem);
    }

    // ------------------------------------------------------------- Version reporting
    //
    // The file is not "incompatible" when it comes from an older version: it is upgraded,
    // and nothing is lost. It IS a problem the other way round, and these pin which is
    // which, because getting that backwards would have people throwing away working
    // configurations.

    [Fact]
    public void AnUpgradedFileSaysSoWithoutCallingItAProblem()
    {
        GivenConfiguration("""
        {
          "ConfigurationVersion": 1,
          "Worklist": { "IsEnabled": true, "CalledAeTitle": "RADWL", "Host": "10.0.0.5" }
        }
        """);

        var service = new ConfigurationService(ConfigPath);
        service.Load();

        Assert.Empty(service.LoadProblem);
        Assert.NotEmpty(service.LoadNotice);
        Assert.False(service.IsFromNewerVersion);
        Assert.Contains("worklist", service.LoadNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUpgradedFileIsStampedWithTheNewVersionAndNotReAnnounced()
    {
        GivenConfiguration("""
        { "ConfigurationVersion": 1, "Worklist": { "IsEnabled": true, "Host": "10.0.0.5" } }
        """);

        new ConfigurationService(ConfigPath).Load();

        var second = new ConfigurationService(ConfigPath);
        AppConfiguration loaded = second.Load();

        Assert.Equal(AppConfiguration.CurrentVersion, loaded.ConfigurationVersion);
        Assert.Empty(second.LoadNotice);
    }

    [Fact]
    public void AFileFromANewerVersionIsReportedAsAProblem()
    {
        // The one direction that genuinely loses settings: this build cannot model what a
        // newer one added, so saving would write the file back without it.
        GivenConfiguration("""
        { "ConfigurationVersion": 99, "CallingAeTitle": "PDF2DCM" }
        """);

        var service = new ConfigurationService(ConfigPath);
        service.Load();

        Assert.True(service.IsFromNewerVersion);
        Assert.NotEmpty(service.LoadProblem);
        Assert.Empty(service.LoadNotice);
    }

    [Fact]
    public void AFileFromANewerVersionIsNotWrittenBackOverOnLoad()
    {
        // Loading must not be what destroys it. The warning is only useful if the file is
        // still there to go back to.
        GivenConfiguration("""
        { "ConfigurationVersion": 99, "SomethingThisBuildHasNeverHeardOf": true }
        """);

        new ConfigurationService(ConfigPath).Load();

        Assert.Contains(
            "SomethingThisBuildHasNeverHeardOf",
            File.ReadAllText(ConfigPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAlreadyAtTheCurrentVersionSaysNothingAtAll()
    {
        GivenConfiguration(
            "{ \"ConfigurationVersion\": " + AppConfiguration.CurrentVersion + ", \"Worklists\": [] }");

        var service = new ConfigurationService(ConfigPath);
        service.Load();

        Assert.Empty(service.LoadProblem);
        Assert.Empty(service.LoadNotice);
        Assert.False(service.IsFromNewerVersion);
    }

    private void GivenConfiguration(string json) => File.WriteAllText(ConfigPath, json);
}
