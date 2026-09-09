namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Produces DICOM Unique Identifiers.
/// </summary>
/// <remarks>
/// Every call must return a value that has never been produced before, by this or any
/// other system. Reusing a UID causes an archive to overwrite or reject an object, which
/// in a clinical setting means a report can be lost or attached to the wrong patient.
/// </remarks>
public interface IUidGenerator
{
    /// <summary>Generates a new Study Instance UID.</summary>
    string NewStudyInstanceUid();

    /// <summary>Generates a new Series Instance UID.</summary>
    string NewSeriesInstanceUid();

    /// <summary>Generates a new SOP Instance UID.</summary>
    string NewSopInstanceUid();

    /// <summary>
    /// The Implementation Class UID that identifies this software in the DICOM file meta
    /// information and in association negotiation. It is a constant, not a new value per
    /// object.
    /// </summary>
    string ImplementationClassUid { get; }

    /// <summary>Implementation Version Name, limited to 16 characters by the standard.</summary>
    string ImplementationVersionName { get; }
}
