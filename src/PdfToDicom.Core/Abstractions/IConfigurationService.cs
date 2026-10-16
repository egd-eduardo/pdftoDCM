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
    /// What went wrong with the configuration file on load, or empty when nothing did.
    /// </summary>
    /// <remarks>
    /// A damaged file is set aside and defaults take over, which is the right behaviour:
    /// the application always starts. But it used to do so in silence, and a site whose
    /// file had been hand edited into invalid JSON would find every setting back at its
    /// default with nothing anywhere to say why. The worklist would be gone, the
    /// destinations would be gone, and the only record was a text file beside the
    /// quarantined one that nobody would think to look for.
    /// </remarks>
    string LoadProblem { get; }

    /// <summary>
    /// Something worth telling the operator about the load that is not a problem, or empty.
    /// </summary>
    /// <remarks>
    /// Used when the file was written by an older version and has been upgraded in place.
    /// Nothing is lost and nothing needs doing, but the file on disk has just changed
    /// shape, and somebody who knows what was in it should be able to see that happened
    /// rather than find out later.
    /// </remarks>
    string LoadNotice { get; }

    /// <summary>
    /// True when the file was written by a NEWER version than this build understands.
    /// </summary>
    /// <remarks>
    /// The one direction that genuinely loses settings. This build does not model what the
    /// newer one added, so saving writes the file back without it. Callers should treat
    /// saving as destructive while this is true.
    /// </remarks>
    bool IsFromNewerVersion { get; }

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
