using System.Text.Json;
using PdfToDicom.Core.Configuration;

namespace PdfToDicom.Integration.Configuration;

/// <summary>
/// Reads and writes the HL7 settings file, and says where the profiles are.
/// </summary>
/// <remarks>
/// Deliberately shaped like <c>ConfigurationService</c>, including the write to a temporary
/// file followed by a move: a settings file truncated by a power cut is a site that cannot
/// start in the morning, and the two files should fail the same way or not at all.
/// </remarks>
public sealed class Hl7SettingsService
{
    private readonly object _saveLock = new();
    private readonly string _settingsFilePath;
    private Hl7IntegrationSettings _current = new();

    /// <summary>Creates the service.</summary>
    /// <param name="settingsFilePath">
    /// An explicit path, used by the tests and by the command line tool. The standard per
    /// user location is used when it is omitted.
    /// </param>
    public Hl7SettingsService(string? settingsFilePath = null)
    {
        _settingsFilePath = string.IsNullOrWhiteSpace(settingsFilePath)
            ? Path.Combine(ConfigurationPaths.ApplicationDataFolder, "hl7", "integration.json")
            : settingsFilePath;

        Profiles = new Hl7ProfileStore(
            Path.Combine(Path.GetDirectoryName(_settingsFilePath) ?? ".", "profiles"));
    }

    /// <summary>The settings as last read or saved.</summary>
    public Hl7IntegrationSettings Current => _current;

    /// <summary>Where the settings file is, which the page shows so it can be found.</summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>The profiles installed beside it.</summary>
    public Hl7ProfileStore Profiles { get; }

    /// <summary>What went wrong reading the file, where something did.</summary>
    public string LoadProblem { get; private set; } = string.Empty;

    /// <summary>Whether the file was written by a newer build than this one.</summary>
    public bool IsFromNewerVersion { get; private set; }

    /// <summary>
    /// Reads the file, or returns defaults when there is none.
    /// </summary>
    /// <remarks>
    /// No file is the normal case and not a problem: most sites have no HL7 interface, and
    /// an absent file is how they say so. A file that exists and cannot be read is a
    /// problem, and it does not become an empty configuration, because silently forgetting
    /// where a hospital's messages go and then overwriting the file that said so is the
    /// worst available outcome.
    /// </remarks>
    public Hl7IntegrationSettings Load()
    {
        LoadProblem = string.Empty;
        IsFromNewerVersion = false;

        if (!File.Exists(_settingsFilePath))
        {
            _current = new Hl7IntegrationSettings();
            return _current;
        }

        try
        {
            string json = File.ReadAllText(_settingsFilePath);

            Hl7IntegrationSettings? loaded =
                JsonSerializer.Deserialize<Hl7IntegrationSettings>(json, Hl7SettingsSerializer.Options);

            if (loaded is null)
            {
                LoadProblem = $"'{_settingsFilePath}' is empty or holds nothing readable.";
                return _current;
            }

            if (loaded.SettingsVersion > Hl7IntegrationSettings.CurrentVersion)
            {
                IsFromNewerVersion = true;
                LoadProblem =
                    $"'{_settingsFilePath}' was written by a newer version " +
                    $"(version {loaded.SettingsVersion}, this build understands " +
                    $"{Hl7IntegrationSettings.CurrentVersion}). It is being used as read and will " +
                    "not be saved over, so nothing it holds is lost.";
            }

            _current = loaded;
            return _current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadProblem = $"'{_settingsFilePath}' could not be read: {ex.Message}";
            return _current;
        }
    }

    /// <summary>
    /// Writes the settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file on disk was written by a newer build. Refused rather than merged: this build
    /// cannot know what the newer one added, and a save would drop it.
    /// </exception>
    public void Save(Hl7IntegrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (IsFromNewerVersion)
        {
            throw new InvalidOperationException(
                "These settings were written by a newer version of the application and will " +
                "not be overwritten by this one.");
        }

        settings.SettingsVersion = Hl7IntegrationSettings.CurrentVersion;

        lock (_saveLock)
        {
            string? directory = Path.GetDirectoryName(_settingsFilePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, Hl7SettingsSerializer.Options);
            string temporaryPath = _settingsFilePath + ".tmp";

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsFilePath, overwrite: true);
        }

        _current = settings;
    }

    /// <summary>Deletes the settings file, which is how a site turns the whole thing off.</summary>
    /// <remarks>
    /// Offered because disabling every rule and leaving the file in place looks identical,
    /// from the outside, to an interface that is configured and simply not working. An
    /// absent file cannot be misread.
    /// </remarks>
    public void Forget()
    {
        lock (_saveLock)
        {
            if (File.Exists(_settingsFilePath))
            {
                File.Delete(_settingsFilePath);
            }
        }

        _current = new Hl7IntegrationSettings();
    }
}
