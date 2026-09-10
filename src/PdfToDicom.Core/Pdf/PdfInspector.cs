using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace PdfToDicom.Core.Pdf;

/// <summary>
/// Reads PDF documents using PdfPig, a fully managed parser.
/// </summary>
/// <remarks>
/// A managed parser is used deliberately. A native PDF component would have to be
/// shipped per architecture and would work against the requirement that the application
/// deploy as a single self contained executable with no additional runtime.
/// </remarks>
public sealed class PdfInspector : IPdfInspector
{
    /// <summary>The five byte signature that begins every PDF file.</summary>
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    private readonly IAppLogger _logger;

    /// <summary>Creates the inspector.</summary>
    public PdfInspector(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public PdfDocumentInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The PDF file was not found.", path);
        }

        int? pageCount = null;
        string? title = null;
        bool isEncrypted = false;
        string? version = ReadHeaderVersion(path);

        try
        {
            // PdfPig opens the document lazily, so this is inexpensive even for a large
            // report. Failure here is not fatal: the descriptive fields simply stay blank
            // and the structural check in Validate reports the problem to the operator.
            using PdfDocument document = PdfDocument.Open(path);
            pageCount = document.NumberOfPages;
            title = string.IsNullOrWhiteSpace(document.Information.Title) ? null : document.Information.Title.Trim();
        }
        catch (PdfDocumentEncryptedException)
        {
            isEncrypted = true;
            _logger.Warning($"The PDF '{file.Name}' is encrypted, so its page count could not be read.");
        }
        catch (Exception ex)
        {
            _logger.Warning($"The PDF '{file.Name}' could not be parsed for descriptive information: {ex.Message}");
        }

        return new PdfDocumentInfo(
            FullPath: file.FullName,
            FileName: file.Name,
            SizeInBytes: file.Length,
            CreatedUtc: file.CreationTimeUtc,
            ModifiedUtc: file.LastWriteTimeUtc,
            PageCount: pageCount,
            Title: title,
            IsEncrypted: isEncrypted,
            PdfVersion: version);
    }

    /// <inheritdoc />
    public ValidationResult Validate(string? path)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(path))
        {
            return result.AddError("PdfPath", "No PDF document has been selected.");
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return result.AddError("PdfPath", $"The path is not valid: {ex.Message}");
        }

        if (!file.Exists)
        {
            return result.AddError("PdfPath", "The selected PDF file does not exist.");
        }

        if (file.Length == 0)
        {
            return result.AddError("PdfPath", "The selected PDF file is empty.");
        }

        if (!HasPdfSignature(file.FullName))
        {
            result.AddError(
                "PdfPath",
                "The selected file does not begin with a PDF header, so it is not a PDF document.");
            return result;
        }

        try
        {
            using PdfDocument document = PdfDocument.Open(file.FullName);

            if (document.NumberOfPages == 0)
            {
                result.AddError("PdfPath", "The PDF document contains no pages.");
            }
        }
        catch (PdfDocumentEncryptedException)
        {
            // Encapsulating an encrypted PDF produces an object that no clinician can
            // open from the archive, which defeats the purpose of the workflow.
            result.AddError(
                "PdfPath",
                "The PDF is password protected. Save an unprotected copy of the document and select that instead.");
        }
        catch (Exception ex)
        {
            result.AddError(
                "PdfPath",
                $"The PDF could not be read and may be damaged or still being written: {ex.Message}");
        }

        return result;
    }

    /// <summary>Reads the version declared in the PDF header, for example "1.7".</summary>
    private static string? ReadHeaderVersion(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buffer = stackalloc byte[9];
            int read = stream.Read(buffer);

            if (read < 8 || !buffer[..5].SequenceEqual(PdfSignature))
            {
                return null;
            }

            return System.Text.Encoding.ASCII.GetString(buffer[5..read]).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Confirms that the file begins with the PDF signature.</summary>
    private static bool HasPdfSignature(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buffer = stackalloc byte[5];

            return stream.Read(buffer) == 5 && buffer.SequenceEqual(PdfSignature);
        }
        catch
        {
            return false;
        }
    }
}
