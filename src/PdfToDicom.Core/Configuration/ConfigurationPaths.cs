namespace PdfToDicom.Core.Configuration;

/// <summary>
/// Resolves every file system location the application uses.
/// </summary>
/// <remarks>
/// All writable locations live beneath the current user's application data folder.
/// Nothing is written to Program Files or to the registry, which is what allows the
/// application to run without administrator rights after installation.
/// </remarks>
public static class ConfigurationPaths
{
    /// <summary>Folder name used beneath the application data root.</summary>
    public const string ApplicationFolderName = "PdfToDicom";

    /// <summary>Name of the configuration file.</summary>
    public const string ConfigurationFileName = "config.json";

    /// <summary>
    /// Root folder for all per user data: configuration, logs and generated objects.
    /// On Windows this resolves beneath %APPDATA%. On macOS and Linux, where the
    /// developer tooling and the command line front end run, it resolves beneath the
    /// XDG configuration directory.
    /// </summary>
    public static string ApplicationDataFolder =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            ApplicationFolderName);

    /// <summary>Full path of the configuration file.</summary>
    public static string ConfigurationFilePath =>
        Path.Combine(ApplicationDataFolder, ConfigurationFileName);

    /// <summary>Default folder for log files.</summary>
    public static string DefaultLogFolder =>
        Path.Combine(ApplicationDataFolder, "Logs");

    /// <summary>Default folder for generated DICOM objects.</summary>
    public static string DefaultOutputFolder =>
        Path.Combine(ApplicationDataFolder, "Output");

    /// <summary>Default folder offered by the PDF file picker.</summary>
    public static string DefaultPdfFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// Creates a directory if it does not already exist and returns its path.
    /// Any failure is surfaced to the caller so it can be reported rather than
    /// silently producing an application that cannot write its logs.
    /// </summary>
    public static string EnsureDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A folder path must be supplied.", nameof(path));
        }

        Directory.CreateDirectory(path);
        return path;
    }
}
