using System.Globalization;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;

namespace PdfToDicom.Core.Importing;

/// <summary>
/// What happened to a source file after an import.
/// </summary>
/// <param name="Moved">True when the file is no longer at its original path.</param>
/// <param name="NewPath">Where it is now, or the original path when nothing happened.</param>
/// <param name="Message">One line for the operator and the log.</param>
public sealed record FiledSourceFile(bool Moved, string NewPath, string Message);

/// <summary>
/// Puts a source file somewhere it will not be imported twice.
/// </summary>
/// <remarks>
/// <para>
/// The folder monitor remembers which paths it has already offered, but only in memory,
/// so every restart offers the whole folder again. For a folder that another system keeps
/// filling, that means the same document converted and sent repeatedly, each time as a
/// new object in the archive. Taking the file out of the watched set is the only durable
/// answer.
/// </para>
/// <para>
/// <b>Nothing here deletes anything.</b> Every path leaves the file on disk under a name
/// somebody can find: that is why it moves or renames rather than removing, and why a
/// collision gets a counter rather than an overwrite. A failed import goes to a folder of
/// its own, because leaving it in place would have the monitor retry it for ever and
/// filing it as imported would be untrue.
/// </para>
/// </remarks>
public sealed class ImportedFileFiler
{
    private readonly IAppLogger _logger;

    /// <summary>Creates the filer.</summary>
    public ImportedFileFiler(IAppLogger logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Files a source file according to the configuration.
    /// </summary>
    /// <param name="path">The file that was imported.</param>
    /// <param name="configuration">The import settings.</param>
    /// <param name="succeeded">
    /// Whether the import succeeded. A failure is filed separately rather than treated as
    /// done.
    /// </param>
    /// <remarks>
    /// Never throws. A document that reached the archive and then could not be tidied away
    /// is an untidy folder, not a clinical problem, and turning it into a failed conversion
    /// would misreport what happened. The outcome is returned and logged instead.
    /// </remarks>
    public FiledSourceFile File(string path, FilenameImportConfiguration configuration, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return new FiledSourceFile(false, path ?? string.Empty, string.Empty);
        }

        if (configuration.AfterImport == ImportedFileDisposition.Leave)
        {
            return new FiledSourceFile(false, path, string.Empty);
        }

        try
        {
            return configuration.AfterImport == ImportedFileDisposition.Rename && succeeded
                ? RenameInPlace(path, configuration)
                : MoveToSubfolder(path, configuration, succeeded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Reported, not thrown. The conversion already happened and its result stands.
            string message =
                $"'{Path.GetFileName(path)}' was imported but could not be moved out of the " +
                $"monitored folder: {ex.Message} It may be offered again.";

            _logger.Warning(message);

            return new FiledSourceFile(false, path, message);
        }
    }

    /// <summary>
    /// Moves the file into the imported or the failed subfolder.
    /// </summary>
    /// <remarks>
    /// A failed import is always moved, even when the configuration asks for renaming,
    /// because the point of the failed folder is that somebody can see what is in it.
    /// </remarks>
    private FiledSourceFile MoveToSubfolder(
        string path, FilenameImportConfiguration configuration, bool succeeded)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));

        if (string.IsNullOrEmpty(parent))
        {
            return new FiledSourceFile(false, path, string.Empty);
        }

        string folderName = succeeded
            ? Fallback(configuration.ImportedSubfolder, "Imported")
            : Fallback(configuration.FailedSubfolder, "Failed");

        string folder = Path.Combine(parent, folderName);
        Directory.CreateDirectory(folder);

        string target = Available(Path.Combine(folder, Path.GetFileName(path)));

        System.IO.File.Move(path, target);

        string message = succeeded
            ? $"Moved to {folderName}{Path.DirectorySeparatorChar}{Path.GetFileName(target)}."
            : $"Not imported, so moved to {folderName}{Path.DirectorySeparatorChar}{Path.GetFileName(target)}.";

        _logger.Information($"'{Path.GetFileName(path)}' was filed: {message}");

        return new FiledSourceFile(true, target, message);
    }

    /// <summary>
    /// Renames the file in place so the monitor's filter no longer matches it.
    /// </summary>
    /// <remarks>
    /// The suffix goes after the extension, giving "report.pdf.imported", so the name stops
    /// ending in .pdf. Putting it before the extension would leave a .pdf that the monitor
    /// would pick straight back up.
    /// </remarks>
    private FiledSourceFile RenameInPlace(string path, FilenameImportConfiguration configuration)
    {
        string suffix = Fallback(configuration.RenameSuffix, ".imported");
        string target = Available(path + suffix);

        System.IO.File.Move(path, target);

        string message = $"Renamed to {Path.GetFileName(target)}.";
        _logger.Information($"'{Path.GetFileName(path)}' was filed: {message}");

        return new FiledSourceFile(true, target, message);
    }

    /// <summary>
    /// A path that does not exist yet, by adding a counter before the final suffix.
    /// </summary>
    /// <remarks>
    /// Overwriting is not an option. Two documents of the same name are routine when a
    /// sending system reuses one, and silently replacing the first would destroy the only
    /// record of what was sent.
    /// </remarks>
    private static string Available(string preferred)
    {
        if (!System.IO.File.Exists(preferred))
        {
            return preferred;
        }

        string directory = Path.GetDirectoryName(preferred) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(preferred);
        string extension = Path.GetExtension(preferred);

        for (int counter = 2; counter < 10_000; counter++)
        {
            string candidate = Path.Combine(
                directory,
                $"{name} ({counter.ToString(CultureInfo.InvariantCulture)}){extension}");

            if (!System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Ten thousand files of one name is not a situation to invent more names for. A
        // timestamp ends it, and is still not an overwrite.
        return Path.Combine(
            directory,
            $"{name} ({DateTime.Now:yyyyMMddHHmmssfff}){extension}");
    }

    private static string Fallback(string? value, string whenEmpty) =>
        string.IsNullOrWhiteSpace(value) ? whenEmpty : value.Trim();
}
