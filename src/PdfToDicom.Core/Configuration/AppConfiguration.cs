using System.Text.Json;
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
/// How several converted files are gathered into DICOM studies.
/// </summary>
public enum StudyGroupingMode
{
    /// <summary>
    /// Every file becomes its own study. This is the behaviour of every version before
    /// 2.0 and remains the default, because it is what a site expects unless it has asked
    /// for something else.
    /// </summary>
    SeparateStudy = 0,

    /// <summary>
    /// Files converted for the same Patient ID join one study, each in its own series.
    /// The association lasts until the patient changes or the application is closed; it
    /// is never written to disk, so a patient's later examination cannot join an old
    /// study by accident.
    /// </summary>
    GroupByPatient = 1
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
    /// Whether files converted for the same patient are gathered into one study.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="StudyGroupingMode.SeparateStudy"/>, which is what every
    /// earlier version did. A site that turns grouping on gets one study per patient per
    /// session, with each file in its own series.
    /// </remarks>
    public StudyGroupingMode StudyGrouping { get; set; } = StudyGroupingMode.SeparateStudy;

    /// <summary>
    /// Obsolete and ignored. The registered UID root is fixed in the software.
    /// </summary>
    /// <remarks>
    /// Retained only so that a configuration file written by an earlier version still
    /// deserialises without error. Whatever it holds has no effect: the registered root
    /// identifies the publisher rather than the site, so it is not a site's to set. See
    /// <c>UidGenerator.RegisteredUidRoot</c>.
    /// </remarks>
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
    /// Value written to Modality (0008,0060) for a converted image.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Modality"/> because the two are not interchangeable.
    /// A site that sets the document modality to "DOC" would be labelling a scanned
    /// photograph as an encapsulated document, which is wrong and can make an archive
    /// route it oddly. "OT" (Other) suits any converted image; "XC" is the defined term
    /// for external camera photography.
    /// </remarks>
    public string ImageModality { get; set; } = "OT";

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

    /// <summary>Default Series Description for a converted image.</summary>
    public string DefaultImageSeriesDescription { get; set; } = "Imported Image";

    /// <summary>
    /// Whether every page of a converted PDF is also sent as a Secondary Capture image,
    /// in its own series within the same study.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, because many PACS viewers will only open a study that contains an
    /// image. An Encapsulated PDF is not an image SOP Class, so without this such a viewer
    /// refuses to launch and the report cannot be read at all, however correct the object
    /// is. With it, the viewer opens and the report is legible on screen.
    /// </para>
    /// <para>
    /// Turn it off at a site whose viewer displays encapsulated documents properly. The
    /// images roughly double what is stored and transmitted, and there is no reason to pay
    /// that where the document alone already works.
    /// </para>
    /// </remarks>
    public bool RenderPdfPagesAsImages { get; set; } = true;

    /// <summary>
    /// How patient information is read out of the names of files arriving in the monitored
    /// folder.
    /// </summary>
    /// <remarks>
    /// Sits beside the folder monitoring settings because it is what makes them useful: a
    /// document whose name already carries the identifiers needs nothing retyped.
    /// </remarks>
    public FilenameImportConfiguration FilenameImport { get; set; } = new();

    /// <summary>
    /// The Modality Worklist servers that may be queried, in the order they are offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Typing a patient identifier by hand is the likeliest way to file a document against
    /// the wrong patient. Taking it from the worklist that scheduled the examination
    /// removes the step entirely, which is why this is here.
    /// </para>
    /// <para>
    /// A list rather than one server, because a hospital does not have one worklist. A
    /// department converting documents for radiology and for cardiology queries two
    /// different systems, and before this it could be pointed at only one of them at a
    /// time. The operator chooses which to ask when they open the worklist.
    /// </para>
    /// <para>
    /// A configuration written by an earlier version holds a single <c>Worklist</c> object
    /// instead. It is migrated into this list on load, keeping its settings, so nobody has
    /// to retype a connection that already worked.
    /// </para>
    /// </remarks>
    public List<WorklistConfiguration> Worklists { get; set; } = new();

    /// <summary>
    /// Identifier of the worklist server chosen last, so it is offered first next time.
    /// </summary>
    /// <remarks>
    /// A department that always queries the same one should not have to pick it every
    /// time, and a department that alternates loses nothing. An identifier that no longer
    /// exists is ignored rather than repaired, and the first enabled server is used.
    /// </remarks>
    public string LastUsedWorklistId { get; set; } = string.Empty;

    /// <summary>
    /// Resolution used when rendering PDF pages to images.
    /// </summary>
    /// <remarks>
    /// 150 is the usual floor for body text that stays comfortably readable on screen, and
    /// is what this was verified at. Raising it improves fine print and grows every object
    /// by the square of the change: 300 is four times the pixels of 150.
    /// </remarks>
    public int PdfRenderDotsPerInch { get; set; } = 150;

    /// <summary>Default Study Description for a converted image.</summary>
    public string DefaultImageStudyDescription { get; set; } = "Imported Image";

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

    /// <summary>Worklist servers that are enabled, and therefore offered to the operator.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorklistConfiguration> EnabledWorklists =>
        Worklists.Where(w => w.IsEnabled).ToList();

    /// <summary>
    /// True when at least one worklist server is enabled, which is what decides whether
    /// the worklist is offered on the main window at all.
    /// </summary>
    [JsonIgnore]
    public bool IsWorklistAvailable => Worklists.Any(w => w.IsEnabled);

    /// <summary>
    /// The worklist server to offer first: the one used last where it is still enabled,
    /// otherwise the first enabled one, otherwise none at all.
    /// </summary>
    [JsonIgnore]
    public WorklistConfiguration? PreferredWorklist
    {
        get
        {
            IReadOnlyList<WorklistConfiguration> enabled = EnabledWorklists;

            return enabled.FirstOrDefault(w =>
                       !string.IsNullOrEmpty(LastUsedWorklistId) &&
                       string.Equals(w.Id, LastUsedWorklistId, StringComparison.Ordinal))
                   ?? enabled.FirstOrDefault();
        }
    }

    // ---------------------------------------------------------------- Copying

    /// <summary>
    /// An independent deep copy, so a dialog can edit a copy and discard it on cancel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Done by serializing and deserializing rather than by assigning each property, and
    /// that is the whole point. The settings dialog used to carry its own hand written copy,
    /// and it silently stopped copying three settings as they were added: page rendering and
    /// its resolution from one release, the file name import from the next. The symptom was
    /// as bad as it sounds. Opening Settings and pressing Save, changing nothing, reset
    /// those settings to their defaults, with no error and nothing in the log.
    /// </para>
    /// <para>
    /// A round trip cannot drift, because it enumerates the properties rather than repeating
    /// them. The cost is a few microseconds once per dialog, which is nothing, and a test
    /// walks every property by reflection to prove the copy is faithful.
    /// </para>
    /// <para>
    /// The derived properties above are <c>[JsonIgnore]</c>d and read only, so they are
    /// recomputed from the copied state rather than lost.
    /// </para>
    /// </remarks>
    public AppConfiguration Clone()
    {
        string json = JsonSerializer.Serialize(this, CloneOptions);

        // The deserializer cannot return null for a non nullable object type given valid
        // JSON of our own making; the fallback exists so a defect cannot produce a null
        // configuration that fails somewhere far away from here.
        return JsonSerializer.Deserialize<AppConfiguration>(json, CloneOptions) ?? new AppConfiguration();
    }

    /// <summary>
    /// Options for <see cref="Clone"/>, matching how the configuration is stored.
    /// </summary>
    /// <remarks>
    /// Enumerations go through as names rather than numbers, as they do on disk. It makes no
    /// difference to a round trip, and it means a clone is readable if one is ever dumped
    /// while diagnosing something.
    /// </remarks>
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };
}
