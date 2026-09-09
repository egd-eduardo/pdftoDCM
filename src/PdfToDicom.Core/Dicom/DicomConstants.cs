namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Fixed DICOM values used when building Encapsulated PDF objects.
/// </summary>
/// <remarks>
/// These are collected in one place so that a reviewer can confirm the values against
/// the standard without reading through the builder, and so that nothing is written as a
/// magic string at the point of use.
/// </remarks>
public static class DicomConstants
{
    /// <summary>
    /// Encapsulated PDF Storage SOP Class UID, defined in DICOM PS3.4 Annex B.5 and
    /// listed in PS3.6 as 1.2.840.10008.5.1.4.1.1.104.1.
    /// </summary>
    public const string EncapsulatedPdfStorageSopClassUid = "1.2.840.10008.5.1.4.1.1.104.1";

    /// <summary>Verification SOP Class UID, used for C-ECHO.</summary>
    public const string VerificationSopClassUid = "1.2.840.10008.1.1";

    /// <summary>
    /// Explicit VR Little Endian. This is the transfer syntax used for both the stored
    /// file and the negotiated presentation context. It is the most broadly supported
    /// non compressed syntax and every conformant archive accepts it.
    /// </summary>
    public const string ExplicitVrLittleEndianUid = "1.2.840.10008.1.2.1";

    /// <summary>Implicit VR Little Endian, offered as a fallback presentation context.</summary>
    public const string ImplicitVrLittleEndianUid = "1.2.840.10008.1.2";

    /// <summary>
    /// UID prefix reserved by DICOM PS3.5 Annex B.2 for identifiers derived from a UUID.
    /// Using it removes any need for the deploying organisation to register a UID root.
    /// </summary>
    public const string UuidDerivedUidPrefix = "2.25.";

    /// <summary>MIME type recorded in MIME Type of Encapsulated Document (0042,0012).</summary>
    public const string PdfMimeType = "application/pdf";

    /// <summary>Value written to Modality (0008,0060) when the site keeps the default.</summary>
    public const string DefaultModality = "OT";

    /// <summary>
    /// Value defined by the current standard for encapsulated document series. Offered as
    /// an alternative for archives that expect it.
    /// </summary>
    public const string DocumentModality = "DOC";

    /// <summary>Private creator identifier used to reserve this application's private block.</summary>
    public const string PrivateCreator = "PDFTODICOM";

    /// <summary>
    /// Private group used for the small number of application specific attributes.
    /// Odd numbered groups are reserved for private data by PS3.5 section 7.8.
    /// </summary>
    public const ushort PrivateGroup = 0x7771;

    /// <summary>Coding scheme designator for DICOM controlled terminology.</summary>
    public const string DcmCodingScheme = "DCM";

    /// <summary>
    /// Code value from the DICOM controlled terminology used as a generic document title
    /// when the site does not supply a more specific one. 121181 is "Document Title".
    /// </summary>
    public const string DocumentTitleCodeValue = "121181";

    /// <summary>Human readable meaning that accompanies <see cref="DocumentTitleCodeValue"/>.</summary>
    public const string DocumentTitleCodeMeaning = "Document Title";
}
