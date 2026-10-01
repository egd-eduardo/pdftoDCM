using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Renders the pages of a PDF to pixels, so that the report can also be sent as images.
/// </summary>
/// <remarks>
/// <para>
/// This exists for one reason: many PACS viewers will only open a study that contains an
/// image. An Encapsulated PDF is not an image SOP Class, so such a viewer refuses to
/// launch and the report cannot be read at all, however correct the object is.
/// </para>
/// <para>
/// Rendering the real pages is preferred over sending a blank placeholder image. A blank
/// image in a patient's record is actively misleading: a clinician scanning a worklist
/// sees an empty thumbnail and reasonably concludes the study is empty. Rendering the
/// pages means the viewer launches and the report is legible in it, while the
/// encapsulated PDF remains alongside for fidelity, text selection and printing.
/// </para>
/// </remarks>
public interface IPdfPageRenderer
{
    /// <summary>
    /// True when the renderer can actually run on this machine.
    /// </summary>
    /// <remarks>
    /// The renderer depends on a native library. If it cannot be loaded, conversion must
    /// continue and produce the encapsulated PDF regardless: losing the images is a
    /// degraded result, whereas losing the report is a failure.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Renders every page of the supplied PDF.
    /// </summary>
    /// <param name="pdfPath">Absolute path of the PDF.</param>
    /// <param name="dotsPerInch">Rendering resolution.</param>
    /// <returns>
    /// One entry per page in page order, or an empty list when the document could not be
    /// rendered. Never a partial document: dropping pages silently would leave a reader
    /// believing they had seen the whole report.
    /// </returns>
    IReadOnlyList<RenderedPdfPage> Render(string pdfPath, int dotsPerInch);
}
