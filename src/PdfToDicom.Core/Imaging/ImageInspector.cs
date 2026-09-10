using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace PdfToDicom.Core.Imaging;

/// <summary>
/// Reads image files using ImageSharp, which decodes JPEG, PNG, TIFF, BMP, GIF, WebP,
/// TGA and PBM in pure managed code.
/// </summary>
/// <remarks>
/// <para>
/// Pure managed matters here. It means no native codec libraries to ship, so the
/// application stays a single self contained executable, behaves identically on ARM64 as
/// on x64, and does not depend on any imaging component installed on the workstation.
/// </para>
/// <para>
/// Every decision is made from the file's content rather than its name. A scanner that
/// writes a TIFF and gives it a .jpg extension is a real and common thing, and so is an
/// operator renaming a file. The extension is used only to populate a file dialog.
/// </para>
/// </remarks>
public sealed class ImageInspector : IImageInspector
{
    private readonly IAppLogger _logger;

    /// <summary>Creates the inspector.</summary>
    public ImageInspector(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions { get; } = new[]
    {
        "jpg", "jpeg", "png", "tif", "tiff", "bmp", "gif", "webp", "tga"
    };

    /// <inheritdoc />
    public ImageDocumentInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The image to convert was not found.", path);
        }

        using Image image = Image.Load(path);
        IImageFormat format = Image.DetectFormat(path);

        return new ImageDocumentInfo(
            FullPath: file.FullName,
            FileName: file.Name,
            SizeInBytes: file.Length,
            ModifiedUtc: file.LastWriteTimeUtc,
            FormatName: format.Name,
            Width: image.Width,
            Height: image.Height,
            PageCount: image.Frames.Count,
            IsColour: IsColour(image));
    }

    /// <inheritdoc />
    public ValidationResult Validate(string? path)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(path))
        {
            return result.AddError("ImagePath", "No image has been selected.");
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return result.AddError("ImagePath", $"The path is not valid: {ex.Message}");
        }

        if (!file.Exists)
        {
            return result.AddError("ImagePath", "The selected image does not exist.");
        }

        if (file.Length == 0)
        {
            return result.AddError("ImagePath", "The selected image is empty.");
        }

        try
        {
            using Image image = Image.Load(path);

            if (image.Width <= 0 || image.Height <= 0)
            {
                return result.AddError("ImagePath", "The image has no pixels.");
            }

            // A single object must stay within what an archive will accept over one
            // association. Uncompressed pixel data grows quickly, and it is far better to
            // say so here than to have a C-STORE rejected after a long transfer.
            long pixelBytes = (long)image.Width * image.Height * (IsColour(image) ? 3 : 1);

            if (pixelBytes > MaximumPixelBytesPerInstance)
            {
                result.AddWarning(
                    "ImagePath",
                    $"This image produces {pixelBytes / (1024 * 1024)} MB of uncompressed pixel data per page. " +
                    "Some archives refuse objects of that size. Consider reducing the resolution first.");
            }
        }
        catch (UnknownImageFormatException)
        {
            return result.AddError(
                "ImagePath",
                "The file is not an image in a supported format. JPEG, PNG, TIFF, BMP, GIF, WebP and TGA can be converted.");
        }
        catch (InvalidImageContentException ex)
        {
            return result.AddError("ImagePath", $"The image is damaged and could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"The image '{file.Name}' could not be read: {ex.Message}");
            return result.AddError("ImagePath", $"The image could not be read: {ex.Message}");
        }

        return result;
    }

    /// <inheritdoc />
    public SourceDocumentKind DetectKind(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return SourceDocumentKind.Unsupported;
        }

        try
        {
            if (IsPdf(path))
            {
                return SourceDocumentKind.Pdf;
            }

            // DetectFormat reads only the leading bytes, so this is cheap even for a very
            // large file, and it returns null rather than throwing on an unknown format.
            return Image.DetectFormat(path) is not null
                ? SourceDocumentKind.Image
                : SourceDocumentKind.Unsupported;
        }
        catch (Exception ex)
        {
            _logger.Warning($"The type of '{Path.GetFileName(path)}' could not be determined: {ex.Message}");
            return SourceDocumentKind.Unsupported;
        }
    }

    /// <summary>
    /// Roughly 400 megapixels of colour data. Beyond this the operator is warned rather
    /// than blocked, because a site may well have an archive that copes.
    /// </summary>
    private const long MaximumPixelBytesPerInstance = 1_200L * 1024 * 1024;

    /// <summary>
    /// True when a PDF header is present. PS 32000 requires "%PDF-" at the start, though
    /// some producers leave junk in front of it, so a short window is searched.
    /// </summary>
    private static bool IsPdf(string path)
    {
        Span<byte> header = stackalloc byte[1024];

        using FileStream stream = File.OpenRead(path);
        int read = stream.Read(header);

        if (read < 5)
        {
            return false;
        }

        ReadOnlySpan<byte> marker = "%PDF-"u8;
        return header[..read].IndexOf(marker) >= 0;
    }

    /// <summary>
    /// Decides whether the source carries colour by looking at the pixels themselves.
    /// See <see cref="ImagePixelAnalyzer"/> for why the decoder's own pixel format is not
    /// a usable answer.
    /// </summary>
    private static bool IsColour(Image image) => ImagePixelAnalyzer.IsColour(image);
}
