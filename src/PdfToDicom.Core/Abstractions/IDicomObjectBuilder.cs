using FellowOakDicom;
using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Builds an Encapsulated PDF Storage SOP Instance from a PDF file and the operator
/// supplied patient information.
/// </summary>
public interface IDicomObjectBuilder
{
    /// <summary>
    /// Creates a complete, standard conformant DICOM file in memory.
    /// </summary>
    /// <param name="pdfPath">Absolute path of the source PDF.</param>
    /// <param name="demographics">Validated patient and order information.</param>
    /// <param name="pdfInfo">
    /// Previously gathered document information. Supplying it avoids reading the file
    /// twice; pass null to have the builder inspect the document itself.
    /// </param>
    /// <returns>The generated file together with the identifiers that were assigned.</returns>
    BuiltDicomObject Build(string pdfPath, PatientDemographics demographics, PdfDocumentInfo? pdfInfo = null);
}

/// <summary>
/// A generated DICOM object together with the identifiers assigned to it.
/// </summary>
/// <param name="File">The in memory DICOM file, ready to be written or transmitted.</param>
/// <param name="StudyInstanceUid">Study Instance UID that was used.</param>
/// <param name="SeriesInstanceUid">Series Instance UID that was generated.</param>
/// <param name="SopInstanceUid">SOP Instance UID that was generated.</param>
/// <param name="SuggestedFileName">
/// A file name derived from the accession number and SOP Instance UID, safe for use on
/// a Windows file system.
/// </param>
public sealed record BuiltDicomObject(
    DicomFile File,
    string StudyInstanceUid,
    string SeriesInstanceUid,
    string SopInstanceUid,
    string SuggestedFileName);
