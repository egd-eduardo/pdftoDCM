using System.Text.Json;
using System.Text.Json.Serialization;
using PdfToDicom.Core.Abstractions;

namespace PdfToDicom.Core.Configuration;

/// <summary>
/// JSON backed implementation of <see cref="IConfigurationService"/>.
/// </summary>
/// <remarks>
/// Writes are atomic: the new content is written to a temporary file which then replaces
/// the original. A power loss or a crash mid save therefore leaves the previous, valid
/// configuration in place rather than a truncated file.
/// </remarks>
public sealed class ConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _saveLock = new();
    private readonly string _configurationFilePath;
    private AppConfiguration _current = new();

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="configurationFilePath">
    /// Optional explicit path. When omitted the standard per user location is used.
    /// The tests and the command line tool use this to work against a temporary file.
    /// </param>
    public ConfigurationService(string? configurationFilePath = null)
    {
        _configurationFilePath = string.IsNullOrWhiteSpace(configurationFilePath)
            ? ConfigurationPaths.ConfigurationFilePath
            : Path.GetFullPath(configurationFilePath);
    }

    /// <inheritdoc />
    public AppConfiguration Current => _current;

    /// <inheritdoc />
    public string ConfigurationFilePath => _configurationFilePath;

    /// <inheritdoc />
    public event EventHandler<AppConfiguration>? ConfigurationChanged;

    /// <inheritdoc />
    public AppConfiguration Load()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_configurationFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(_configurationFilePath))
            {
                // First run. Create a fully populated default file so the administrator
                // has something concrete to edit rather than an empty document.
                _current = CreateDefaults();
                Save(_current);
                return _current;
            }

            string json = File.ReadAllText(_configurationFilePath);
            AppConfiguration? loaded = JsonSerializer.Deserialize<AppConfiguration>(json, SerializerOptions);

            _current = loaded ?? CreateDefaults();
            ApplyFallbacks(_current);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged file must never prevent the application from starting. The bad
            // file is kept for investigation and a working default takes its place.
            QuarantineCorruptFile(ex);
            _current = CreateDefaults();
            TrySaveQuietly(_current);
        }

        ConfigurationChanged?.Invoke(this, _current);
        return _current;
    }

    /// <inheritdoc />
    public void Save(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ApplyFallbacks(configuration);

        lock (_saveLock)
        {
            string? directory = Path.GetDirectoryName(_configurationFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(configuration, SerializerOptions);
            string temporaryPath = _configurationFilePath + ".tmp";

            File.WriteAllText(temporaryPath, json);

            // File.Move with overwrite is atomic enough for this purpose on both NTFS
            // and the file systems used by the developer platforms.
            File.Move(temporaryPath, _configurationFilePath, overwrite: true);
        }

        _current = configuration;
        ConfigurationChanged?.Invoke(this, _current);
    }

    /// <inheritdoc />
    public void Save() => Save(_current);

    /// <summary>
    /// Builds a configuration containing defaults appropriate for a first run, including
    /// one placeholder destination so the settings dialog is not empty.
    /// </summary>
    public static AppConfiguration CreateDefaults()
    {
        var configuration = new AppConfiguration
        {
            HospitalName = string.Empty,
            InstitutionName = string.Empty,
            StationName = SafeMachineName(),
            CallingAeTitle = "PDF2DCM",
            Destinations =
            {
                new PacsDestination
                {
                    Name = "Main PACS",
                    CalledAeTitle = "PACS",
                    Host = "127.0.0.1",
                    Port = 104,
                    IsEnabled = true,
                    VerifyBeforeSend = true
                }
            }
        };

        ApplyFallbacks(configuration);
        return configuration;
    }

    /// <summary>
    /// Fills in any location that the administrator left blank and repairs values that
    /// are outside their supported range, so that a hand edited file cannot put the
    /// application into an unusable state.
    /// </summary>
    private static void ApplyFallbacks(AppConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.LogFolder))
        {
            configuration.LogFolder = ConfigurationPaths.DefaultLogFolder;
        }

        if (string.IsNullOrWhiteSpace(configuration.OutputFolder))
        {
            configuration.OutputFolder = ConfigurationPaths.DefaultOutputFolder;
        }

        if (string.IsNullOrWhiteSpace(configuration.DefaultPdfFolder))
        {
            configuration.DefaultPdfFolder = ConfigurationPaths.DefaultPdfFolder;
        }

        if (string.IsNullOrWhiteSpace(configuration.StationName))
        {
            configuration.StationName = SafeMachineName();
        }

        if (string.IsNullOrWhiteSpace(configuration.CallingAeTitle))
        {
            configuration.CallingAeTitle = "PDF2DCM";
        }

        if (string.IsNullOrWhiteSpace(configuration.Modality))
        {
            configuration.Modality = "OT";
        }

        if (string.IsNullOrWhiteSpace(configuration.ConversionType))
        {
            configuration.ConversionType = "WSD";
        }

        if (string.IsNullOrWhiteSpace(configuration.Language))
        {
            configuration.Language = "en-US";
        }

        configuration.SeriesNumber = Math.Max(0, configuration.SeriesNumber);
        configuration.InstanceNumber = Math.Max(0, configuration.InstanceNumber);
        configuration.LogRetentionDays = Math.Max(0, configuration.LogRetentionDays);
        configuration.RecentDestinationLimit = Math.Clamp(configuration.RecentDestinationLimit, 1, 50);
        configuration.MonitorSettleDelayMilliseconds =
            Math.Clamp(configuration.MonitorSettleDelayMilliseconds, 100, 60_000);

        configuration.Destinations ??= new List<PacsDestination>();
        configuration.Templates ??= new List<PatientTemplate>();

        foreach (PacsDestination destination in configuration.Destinations)
        {
            if (string.IsNullOrWhiteSpace(destination.Id))
            {
                destination.Id = Guid.NewGuid().ToString("N");
            }

            destination.Port = Math.Clamp(destination.Port, 1, 65535);
            destination.TimeoutSeconds = Math.Clamp(destination.TimeoutSeconds, 1, 600);
            destination.RetryCount = Math.Clamp(destination.RetryCount, 0, 10);
            destination.RetryDelaySeconds = Math.Clamp(destination.RetryDelaySeconds, 0, 120);
        }
    }

    /// <summary>
    /// Renames a configuration file that could not be parsed so that it is available for
    /// investigation without blocking startup.
    /// </summary>
    private void QuarantineCorruptFile(Exception cause)
    {
        try
        {
            if (!File.Exists(_configurationFilePath))
            {
                return;
            }

            string quarantinePath =
                $"{_configurationFilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_configurationFilePath, quarantinePath, overwrite: true);

            // The logger is not necessarily constructed yet at this point, so a marker
            // file records what happened next to the quarantined content.
            File.WriteAllText(
                quarantinePath + ".txt",
                $"The configuration file could not be read and was replaced with defaults.{Environment.NewLine}" +
                $"Reason: {cause.GetType().Name}: {cause.Message}{Environment.NewLine}");
        }
        catch
        {
            // Quarantining is best effort. Failing to rename must not prevent startup.
        }
    }

    private void TrySaveQuietly(AppConfiguration configuration)
    {
        try
        {
            Save(configuration);
        }
        catch
        {
            // The application can still run entirely from in memory defaults.
        }
    }

    /// <summary>
    /// Returns the machine name, truncated to the DICOM Short String limit that applies
    /// to Station Name (0008,1010).
    /// </summary>
    private static string SafeMachineName()
    {
        try
        {
            string name = Environment.MachineName;
            return name.Length > 16 ? name[..16] : name;
        }
        catch
        {
            return "WORKSTATION";
        }
    }
}
