namespace PdfToDicom.Core.Models;

/// <summary>
/// What kind of file the operator selected, and therefore which DICOM object will be
/// produced from it.
/// </summary>
public enum SourceDocumentKind
{
    /// <summary>The file could not be recognised as anything this software can convert.</summary>
    Unsupported = 0,

    /// <summary>A PDF document, converted to an Encapsulated PDF object.</summary>
    Pdf = 1,

    /// <summary>An image, converted to one or more Secondary Capture image objects.</summary>
    Image = 2
}
