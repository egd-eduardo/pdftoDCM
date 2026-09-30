using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Builds Secondary Capture Image Storage SOP Instances from an image file and the
/// operator supplied patient information.
/// </summary>
public interface IImageDicomObjectBuilder
{
    /// <summary>
    /// Creates one complete, standard conformant DICOM file per page of the source.
    /// </summary>
    /// <param name="imagePath">Absolute path of the source image.</param>
    /// <param name="demographics">Validated patient and order information.</param>
    /// <param name="studyInstanceUid">
    /// Study to file the image under. Pass null for a new study. This is how several
    /// files are gathered into one study.
    /// </param>
    /// <param name="imageInfo">
    /// Previously gathered image information. Supplying it avoids reading the file twice;
    /// pass null to have the builder inspect the image itself.
    /// </param>
    /// <returns>
    /// One object per page, in page order, all sharing a single Series Instance UID. A
    /// single page image therefore yields a list of one.
    /// </returns>
    IReadOnlyList<BuiltDicomObject> Build(
        string imagePath,
        PatientDemographics demographics,
        string? studyInstanceUid = null,
        ImageDocumentInfo? imageInfo = null);

    /// <summary>
    /// Creates one Secondary Capture object per rendered PDF page, all in a single new
    /// series within an existing study.
    /// </summary>
    /// <remarks>
    /// This is what makes a report visible in a PACS viewer that will only open a study
    /// containing an image. The encapsulated PDF stays in its own series in the same
    /// study, so nothing is lost: the images are for reading on screen, the document for
    /// fidelity, text selection and printing.
    /// </remarks>
    /// <param name="pages">Rendered pages, in page order.</param>
    /// <param name="demographics">Validated patient and order information.</param>
    /// <param name="studyInstanceUid">
    /// The study the encapsulated PDF was filed under. Required, because the point is for
    /// the images and the document to arrive together.
    /// </param>
    /// <param name="sourceFileName">Name of the PDF, recorded in the derivation description.</param>
    IReadOnlyList<BuiltDicomObject> BuildFromRenderedPages(
        IReadOnlyList<RenderedPdfPage> pages,
        PatientDemographics demographics,
        string studyInstanceUid,
        string sourceFileName);
}
