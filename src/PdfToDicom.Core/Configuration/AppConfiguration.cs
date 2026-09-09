using System.Text.Json.Serialization;

namespace PdfToDicom.Core.Configuration;

/// <summary>
/// Character set options offered to the administrator, mapped to the DICOM
/// Specific Character Set (0008,0005) defined terms.
/// </summary>
public enum DicomCharacterSet
{
    /// <summary>
    /// Unicode in UTF-8, defined term "ISO_IR 192". The recommended setting: it
    /// represents every accented and non Latin patient name correctly.
    /// </summary>
    Utf8 = 0,

    /// <summary>
    /// Latin alphabet number 1, defined term "ISO_IR 100". Provided for older archives
    /// that reject UTF-8. Characters outside Latin 1 cannot be represented.
    /// </summary>
    Latin1 = 1
}

/// <summary>
/// Strategy used to build new DICOM UIDs.
/// </summary>
public enum UidGenerationStrategy
{
    /// <summary>
    /// Derive the UID from a random UUID using the "2.25." prefix defined in
    /// DICOM PS3.5 Annex B.2. Requires no registered organisation root and is
    /// guaranteed unique without any central coordination.
    /// </summary>
    UuidDerived = 0,

    /// <summary>
    /// Build the UID beneath the organisation's own registered UID root. Preferred by
    /// sites that already own a root and want their objects attributable to them.
    /// </summary>
    RegisteredRoot = 1
}

/// <summary>
/// The complete, persisted application configuration.
/// </summary>
/// <remarks>
/// The file is stored per user under the roaming application data folder, which means
/// the application needs no administrator rights either to run or to save settings.
/// Every property has a sensible default so a missing or partially written file still
/// produces a working configuration.
/// </remarks>
public sealed class AppConfiguration
{
    /// <summary>Schema version of the configuration file, used for future migrations.</summary>
    public int ConfigurationVersion { get; set; } = 1;

    // ---------------------------------------------------------------- Institution

    /// <summary>Hospital name, shown in the application header.</summary>
    public string HospitalName { get; set; } = string.Empty;

    /// <summary>Value written to Institution Name (0008,0080).</summary>
    public string InstitutionName { get; set; } = string.Empty;

    /// <summary>Value written to Institutional Department Name (0008,1040). Optional.</summary>
    public string DepartmentName { get; set; } = string.Empty;

    /// <summary>Value written to Station Name (0008,1010). Defaults to the machine name at runtime.</summary>
    public string StationName { get; set; } = string.Empty;

    // ---------------------------------------------------------------- DICOM network

    /// <summary>Calling AE Title used for outgoing associations.</summary>
    public string CallingAeTitle { get; set; } = "PDF2DCM";

    /// <summary>All configured destinations. At least one is required to transmit.</summary>
    public List<PacsDestination> Destinations { get; set; } = new();

    /// <summary>
    /// Maximum number of recently used destinations retained for the quick pick list.
    /// </summary>
    public int RecentDestinationLimit { get; set; } = 8;

    // ---------------------------------------------------------------- DICOM content

    /// <summary>Specific Character Set used to encode text values.</summary>
    public DicomCharacterSet CharacterSet { get; set; } = DicomCharacterSet.Utf8;

    /// <summary>How new UIDs are constructed.</summary>
    public UidGenerationStrategy UidStrategy { get; set; } = UidGenerationStrategy.UuidDerived;

    /// <summary>
    /// Organisation UID root used when <see cref="UidStrategy"/> is
    /// <see cref="UidGenerationStrategy.RegisteredRoot"/>. Must be a valid UID prefix.
    /// </summary>
    public string OrganizationUidRoot { get; set; } = string.Empty;

    /// <summary>
    /// Value written to Modality (0008,0060).
    /// </summary>
    /// <remarks>
    /// "OT" (Other) is the default because it is accepted by essentially every archive
    /// in the field. The value defined by the current standard for encapsulated
    /// documents is "DOC"; sites whose PACS expects that value can select it here.
    /// </remarks>
    public string Modality { get; set; } = "OT";

    /// <summary>
    /// Value written to Conversion Type (0008,0064). "WSD" means Workstation, which
    /// correctly describes a document produced by another application on a computer.
    /// </summary>
    public string ConversionType { get; set; } = "WSD";

    /// <summary>Value written to Manufacturer (0008,0070).</summary>
    public string Manufacturer { get; set; } = "PDF to DICOM";

    /// <summary>
    /// Value written to Burned In Annotation (0028,0301). Clinical reports normally
    /// carry the patient name inside the document itself, so "YES" is the truthful
    /// default for this workflow.
    /// </summary>
    public bool BurnedInAnnotation { get; set; } = true;

    /// <summary>Default Document Title (0042,0010) when the operator does not supply one.</summary>
    public string DefaultDocumentTitle { get; set; } = "Clinical Report";

    /// <summary>Default Study Description when the operator does not supply one.</summary>
    public string DefaultStudyDescription { get; set; } = "Encapsulated PDF Document";

    /// <summary>Default Series Description when the operator does not supply one.</summary>
    public string DefaultSeriesDescription { get; set; } = "PDF Document";

    /// <summary>Series Number (0020,0011) applied to generated series.</summary>
    public int SeriesNumber { get; set; } = 1;

    /// <summary>Instance Number (0020,0013) applied to generated instances.</summary>
    public int InstanceNumber { get; set; } = 1;

    /// <summary>
    /// Record the PDF page count in a correctly reserved private attribute. Disable this
    /// if a receiving system is intolerant of private data elements.
    /// </summary>
    public bool RecordPageCountInPrivateTag { get; set; } = true;

    /// <summary>
    /// Populate Concept Name Code Sequence (0040,A043) with a coded document title.
    /// Improves indexing on archives that use it, and is harmless on those that do not.
    /// </summary>
    public bool IncludeConceptNameCodeSequence { get; set; } = true;

    // ---------------------------------------------------------------- Folders

    /// <summary>Folder initially shown by the file picker.</summary>
    public string DefaultPdfFolder { get; set; } = string.Empty;

    /// <summary>Folder into which generated DICOM files are written.</summary>
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Folder into which log files are written.</summary>
    public string LogFolder { get; set; } = string.Empty;

    /// <summary>Delete the generated DICOM file once every enabled destination accepted it.</summary>
    public bool AutoDeleteAfterSuccessfulTransmission { get; set; }

    /// <summary>Keep a copy of every generated object on disk even when transmission succeeds.</summary>
    public bool AlwaysSaveDicomLocally { get; set; } = true;

    /// <summary>Number of days after which old log files are removed. Zero disables cleanup.</summary>
    public int LogRetentionDays { get; set; } = 90;

    // ---------------------------------------------------------------- Folder monitoring

    /// <summary>Watch <see cref="MonitoredFolder"/> for new PDF files.</summary>
    public bool EnableFolderMonitoring { get; set; }

    /// <summary>Folder watched when <see cref="EnableFolderMonitoring"/> is enabled.</summary>
    public string MonitoredFolder { get; set; } = string.Empty;

    /// <summary>
    /// Time in milliseconds to wait after a file appears before reading it, so that the
    /// producing application has finished writing.
    /// </summary>
    public int MonitorSettleDelayMilliseconds { get; set; } = 1500;

    // ---------------------------------------------------------------- Presentation

    /// <summary>Use the dark colour scheme.</summary>
    public bool DarkMode { get; set; }

    /// <summary>User interface language as a culture name, for example "en-US" or "pt-PT".</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>Show the advanced fields (Study Instance UID, Study ID) on the main form.</summary>
    public bool ShowAdvancedFields { get; set; }

    /// <summary>
    /// Require the operator to confirm the patient details in a summary dialog before
    /// anything is transmitted. Recommended in clinical use.
    /// </summary>
    public bool ConfirmBeforeSending { get; set; } = true;

    // ---------------------------------------------------------------- Licensing

    /// <summary>
    /// The licence code supplied to this site, entered in Settings.
    /// </summary>
    /// <remarks>
    /// Ignored entirely unless the build carries a publisher public key. A build without
    /// one behaves exactly as though this setting did not exist, so an existing
    /// installation is unaffected by the presence of the field.
    /// </remarks>
    public string LicenceCode { get; set; } = string.Empty;

    // ---------------------------------------------------------------- Templates

    /// <summary>Saved, non identifying form templates.</summary>
    public List<PatientTemplate> Templates { get; set; } = new();

    // ---------------------------------------------------------------- Derived helpers

    /// <summary>Destinations that are enabled and therefore participate in transmission.</summary>
    [JsonIgnore]
    public IReadOnlyList<PacsDestination> EnabledDestinations =>
        Destinations.Where(d => d.IsEnabled).ToList();

    /// <summary>The DICOM defined term for the configured character set.</summary>
    [JsonIgnore]
    public string SpecificCharacterSetValue =>
        CharacterSet == DicomCharacterSet.Latin1 ? "ISO_IR 100" : "ISO_IR 192";

    /// <summary>True when the configured character set can represent any Unicode text.</summary>
    [JsonIgnore]
    public bool CharacterSetSupportsUnicode => CharacterSet == DicomCharacterSet.Utf8;
}
