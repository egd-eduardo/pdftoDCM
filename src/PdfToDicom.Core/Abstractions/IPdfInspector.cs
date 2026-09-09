using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Reads a PDF file to gather the descriptive information shown to the operator and to
/// confirm that the file is structurally usable before it is encapsulated.
/// </summary>
public interface IPdfInspector
{
    /// <summary>
    /// Gathers file system and document level information about the supplied PDF.
    /// </summary>
    /// <param name="path">Absolute path of the file.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    PdfDocumentInfo Inspect(string path);

    /// <summary>
    /// Validates that the supplied path refers to a readable, non empty PDF document.
    /// </summary>
    ValidationResult Validate(string? path);
}
