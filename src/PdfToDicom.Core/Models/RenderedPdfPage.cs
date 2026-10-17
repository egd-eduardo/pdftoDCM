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
/// <param name="DotsPerInch">
/// The resolution this page was actually rendered at, which is not always the resolution
/// that was asked for: a document whose pages would not fit the configured pixel data
/// allowance is rendered whole at a lower resolution. Carried here so that what is logged
/// is what happened.
/// </param>
public sealed record RenderedPdfPage(
    int PageNumber,
    int Width,
    int Height,
    bool IsColour,
    byte[] Pixels,
    int DotsPerInch);
