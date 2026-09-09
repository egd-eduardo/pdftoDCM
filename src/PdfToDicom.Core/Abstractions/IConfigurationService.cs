using PdfToDicom.Core.Configuration;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Loads and persists the application configuration.
/// </summary>
public interface IConfigurationService
{
    /// <summary>The configuration currently in effect. Never null.</summary>
    AppConfiguration Current { get; }

    /// <summary>Full path of the configuration file backing <see cref="Current"/>.</summary>
    string ConfigurationFilePath { get; }

    /// <summary>
    /// Raised after the configuration has been reloaded or saved, so that the user
    /// interface can react to changes such as a theme or language switch.
    /// </summary>
    event EventHandler<AppConfiguration>? ConfigurationChanged;

    /// <summary>
    /// Loads the configuration from disk. A missing file results in defaults being
    /// created and written. A corrupt file is preserved alongside a fresh default file
    /// rather than being discarded.
    /// </summary>
    AppConfiguration Load();

    /// <summary>Writes the supplied configuration to disk atomically.</summary>
    void Save(AppConfiguration configuration);

    /// <summary>Writes <see cref="Current"/> to disk.</summary>
    void Save();
}
