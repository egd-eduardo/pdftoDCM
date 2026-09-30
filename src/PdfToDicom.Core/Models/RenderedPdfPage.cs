namespace PdfToDicom.Core.Models;

/// <summary>
/// One page of a PDF, rendered to pixels and ready to become a Secondary Capture image.
/// </summary>
/// <param name="PageNumber">One based page number, used for Instance Number.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="IsColour">
/// True when the rendered page actually carries colour. Decided from the pixels, not
/// assumed: most reports are black text on white, and storing those as greyscale is a
/// third of the size for no loss whatsoever.
/// </param>
/// <param name="Pixels">
/// Interleaved eight bit samples, three per pixel when <paramref name="IsColour"/> is
/// true and one per pixel otherwise. This is the layout DICOM expects for Planar
/// Configuration 0, so no rearrangement happens later.
/// </param>
public sealed record RenderedPdfPage(
    int PageNumber,
    int Width,
    int Height,
    bool IsColour,
    byte[] Pixels);
