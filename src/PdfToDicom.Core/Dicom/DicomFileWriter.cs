using FellowOakDicom;
using PdfToDicom.Core.Abstractions;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Writes generated objects to the output folder.
/// </summary>
/// <remarks>
/// Writing is done through a temporary file that is then moved into place. A crash or a
/// power loss part way through therefore leaves no half written .dcm file that a watching
/// process might pick up and forward to the archive.
/// </remarks>
public sealed class DicomFileWriter
{
    private readonly IAppLogger _logger;

    /// <summary>Creates the writer.</summary>
    public DicomFileWriter(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Writes the supplied file into the output folder.
    /// </summary>
    /// <param name="file">The object to write.</param>
    /// <param name="outputFolder">Destination folder, created if it does not exist.</param>
    /// <param name="fileName">File name to use within the folder.</param>
    /// <returns>The full path of the file that was written.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    public string Write(DicomFile file, string outputFolder, string fileName)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        Directory.CreateDirectory(outputFolder);

        string finalPath = MakeUniquePath(Path.Combine(outputFolder, fileName));
        string temporaryPath = finalPath + ".writing";

        try
        {
            file.Save(temporaryPath);
            File.Move(temporaryPath, finalPath, overwrite: false);

            _logger.Information($"The DICOM object was written to '{finalPath}'.");
            return finalPath;
        }
        catch (Exception ex)
        {
            TryDelete(temporaryPath);
            _logger.Error($"The DICOM object could not be written to '{finalPath}'.", ex);
            throw;
        }
    }

    /// <summary>
    /// Deletes a generated file, used when the site has chosen to remove local copies
    /// after a successful transmission.
    /// </summary>
    /// <returns>True when the file was deleted or was already absent.</returns>
    public bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.Information($"The generated file '{path}' was deleted after a successful transmission.");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"The generated file '{path}' could not be deleted: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Appends a numeric discriminator when a file of the same name already exists, so an
    /// existing object is never silently overwritten.
    /// </summary>
    private static string MakeUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        string directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(desiredPath);
        string extension = Path.GetExtension(desiredPath);

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = Path.Combine(directory, $"{stem}_{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Falling back to a timestamp guarantees termination even in a pathological case.
        return Path.Combine(directory, $"{stem}_{DateTime.Now:yyyyMMddHHmmssfff}{extension}");
    }
}
