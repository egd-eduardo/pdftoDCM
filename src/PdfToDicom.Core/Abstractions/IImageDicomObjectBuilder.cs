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
}
