using System.IO.Compression;
using PdfToDicom.Core.Abstractions;

namespace PdfToDicom.Core.Logging;

/// <summary>
/// Packages the diagnostic and audit logs, plus the effective configuration, into a
/// single archive that an operator can attach to a support request.
/// </summary>
public sealed class LogExporter
{
    private readonly IAppLogger _logger;

    /// <summary>Creates the exporter.</summary>
    public LogExporter(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates a zip archive containing the supplied folders and files.
    /// </summary>
    /// <param name="destinationZipPath">Full path of the archive to create.</param>
    /// <param name="folders">Folders whose contents are added, each under its own entry prefix.</param>
    /// <param name="additionalFiles">Individual files to include, for example the configuration file.</param>
    /// <returns>The number of entries written.</returns>
    /// <exception cref="IOException">The archive could not be created.</exception>
    public int Export(
        string destinationZipPath,
        IEnumerable<string> folders,
        IEnumerable<string>? additionalFiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);
        ArgumentNullException.ThrowIfNull(folders);

        string? parent = Path.GetDirectoryName(Path.GetFullPath(destinationZipPath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(destinationZipPath))
        {
            File.Delete(destinationZipPath);
        }

        int entryCount = 0;

        using (var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create))
        {
            foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                string prefix = new DirectoryInfo(folder).Name;

                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    if (TryAddFile(archive, file, $"{prefix}/{relative}"))
                    {
                        entryCount++;
                    }
                }
            }

            foreach (string file in additionalFiles ?? Enumerable.Empty<string>())
            {
                if (File.Exists(file) && TryAddFile(archive, file, Path.GetFileName(file)))
                {
                    entryCount++;
                }
            }
        }

        _logger.Information($"Exported {entryCount} log file(s) to '{destinationZipPath}'.");
        return entryCount;
    }

    /// <summary>
    /// Adds one file to the archive, copying through a shared read stream so that a log
    /// file currently held open by the logger can still be included.
    /// </summary>
    private bool TryAddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        try
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            using FileStream source = new(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream target = entry.Open();
            source.CopyTo(target);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not add '{sourcePath}' to the log export: {ex.Message}");
            return false;
        }
    }
}
