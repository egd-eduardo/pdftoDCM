using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Reads an image file to gather what the operator is shown, and to confirm the file is
/// something this software can actually convert before it tries.
/// </summary>
public interface IImageInspector
{
    /// <summary>
    /// Gathers file system and image level information about the supplied file.
    /// </summary>
    /// <param name="path">Absolute path of the file.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The file is not a supported image.</exception>
    ImageDocumentInfo Inspect(string path);

    /// <summary>
    /// Validates that the supplied path refers to a readable image in a supported format.
    /// </summary>
    ValidationResult Validate(string? path);

    /// <summary>
    /// Decides what the file is by looking at its content.
    /// </summary>
    /// <remarks>
    /// The extension is not consulted. A scanner that writes a TIFF and calls it .jpg is
    /// commonplace, and a file the operator renamed is not evidence of anything. Reading
    /// the first bytes is both more reliable and cheaper than guessing.
    /// </remarks>
    SourceDocumentKind DetectKind(string? path);

    /// <summary>
    /// The image formats that can be converted, for display and for file dialog filters.
    /// Lower case, without the leading dot, for example "jpg".
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }
}
