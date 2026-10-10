namespace PdfToDicom.Core.Configuration;

/// <summary>
/// What becomes of a source file once it has been imported.
/// </summary>
public enum ImportedFileDisposition
{
    /// <summary>
    /// Left exactly where it is.
    /// </summary>
    /// <remarks>
    /// The monitor remembers what it has already offered, but only in memory, so a
    /// restart offers everything in the folder again. Leave it here only when something
    /// else clears the folder.
    /// </remarks>
    Leave = 0,

    /// <summary>
    /// Moved into a subfolder of the monitored folder.
    /// </summary>
    /// <remarks>
    /// The default. The monitored folder then shows what is still outstanding, which is
    /// the question somebody standing in front of it is asking. The monitor does not watch
    /// subfolders, so a moved file is out of its sight for good.
    /// </remarks>
    MoveToSubfolder = 1,

    /// <summary>
    /// Renamed in place, with a suffix that takes it out of the watched pattern.
    /// </summary>
    /// <remarks>
    /// For a folder somebody else's software also reads, where moving the file would break
    /// them. The extension stops being .pdf, so the monitor ignores it.
    /// </remarks>
    Rename = 2
}


/// <summary>
/// How patient information is read out of the names of files that arrive in the monitored
/// folder.
/// </summary>
/// <remarks>
/// <para>
/// A site receiving documents from another system usually receives the identifiers in the
/// file name already. Reading them removes the retyping, which is where a document ends up
/// against the wrong patient.
/// </para>
/// <para>
/// Several patterns can be configured, tried in order, because a department may receive
/// from more than one system and those systems will not agree on a layout. The first that
/// matches wins; a name that matches none is still offered to the operator with the fields
/// empty, exactly as before.
/// </para>
/// </remarks>
public sealed class FilenameImportConfiguration
{
    /// <summary>
    /// Whether file names are read at all. Off by default: it changes what appears in the
    /// patient fields, and a site should turn it on deliberately.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The layouts to try, in order.
    /// </summary>
    /// <remarks>
    /// Tokens are <c>{PatientId}</c>, <c>{Name}</c>, <c>{FamilyName}</c>,
    /// <c>{GivenName}</c>, <c>{AccessionNumber}</c>, <c>{DateOfBirth}</c>,
    /// <c>{StudyDescription}</c> and <c>{*}</c> for a part to skip. Anything between the
    /// tokens is a literal separator. A pattern starting <c>regex:</c> is taken as a
    /// regular expression with named groups, for a layout a template cannot express.
    /// </remarks>
    public List<string> Patterns { get; set; } = new();

    /// <summary>
    /// Date formats accepted for <c>{DateOfBirth}</c>, tried in order.
    /// </summary>
    /// <remarks>
    /// Empty uses <see cref="DefaultDateFormats"/>. Explicit formats rather than a locale
    /// parse on purpose: 03/04/1970 is two different dates depending on who wrote it, and
    /// a date of birth silently off by a few months is worse than one left blank.
    /// </remarks>
    public List<string> DateFormats { get; set; } = new();

    /// <summary>
    /// Whether Study Date and Study Time come from the file's creation time.
    /// </summary>
    /// <remarks>
    /// On by default when importing. The study happened when the document was produced,
    /// not when this application got round to converting it, and a folder worked through
    /// in a batch would otherwise stamp every study with the same afternoon.
    /// </remarks>
    public bool UseFileDateAsStudyDate { get; set; } = true;

    /// <summary>
    /// Patient's Sex written when a file name does not carry it, as a DICOM defined term:
    /// <c>O</c>, <c>M</c>, <c>F</c>, or empty for a zero length element.
    /// </summary>
    /// <remarks>
    /// <c>O</c> by default, as asked for. Worth knowing what it means: Patient's Sex is a
    /// Type 2 attribute, so an empty value is the standard's way of saying "not supplied",
    /// whereas <c>O</c> asserts "other". Empty is the more literal encoding of a file name
    /// that does not say, and <c>O</c> is the safer one in practice, because archives and
    /// viewers vary in how gracefully they handle the empty case. Set it to an empty
    /// string to get the literal reading instead.
    /// </remarks>
    public string DefaultSex { get; set; } = "O";

    /// <summary>
    /// Split a run-together name such as <c>JohnDoe</c> at its internal capitals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, because it is a guess.</b> Some systems write the name with no
    /// separator at all, and for <c>JohnDoe</c> the intent is obvious; for <c>McDonald</c>,
    /// <c>DaSilva</c> or <c>OBrien</c> it is obvious and wrong, and a patient's name
    /// silently mangled is worse than one that is merely incomplete. With this off such a
    /// name becomes the family name entire, which is incomplete and not wrong.
    /// </para>
    /// <para>
    /// Turn it on where the sending software is known to write <c>FirstLast</c>, and check
    /// a real listing with <c>pdftodicom check-names</c> before trusting it. The split is
    /// reported there, so what it did to each name is visible rather than assumed.
    /// </para>
    /// </remarks>
    public bool SplitRunTogetherNames { get; set; }

    /// <summary>
    /// Width to left-pad a Patient ID read from a file name to, with zeros. Zero disables it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sending system often drops the leading zeros from a patient number, writing
    /// <c>77</c> where the hospital's record is <c>0000000077</c>. Those are different
    /// strings, and an archive matches patients on the string, so the document would open a
    /// second record for the same person. Padding closes that gap.
    /// </para>
    /// <para>
    /// <b>It changes the one value that decides whose record a document joins</b>, so it is
    /// deliberately narrow. Only a value that is entirely digits is padded, because
    /// <c>AB12</c> is not a short number and zeros in front of it would be nonsense. A value
    /// already at or beyond the width is left exactly as it was: never truncated, since the
    /// right hand end of an identifier is the part that distinguishes people.
    /// </para>
    /// <para>
    /// Ten by default, which is the width asked for and the common one for a hospital
    /// patient number. Set it to the width your archive stores, or to zero to pass the
    /// identifier through untouched. Every padding is reported, so
    /// <c>pdftodicom check-names</c>, the Settings dialog's test box and the log all show
    /// what was changed rather than leaving it to be discovered.
    /// </para>
    /// </remarks>
    public int PadPatientIdTo { get; set; } = 10;

    /// <summary>What happens to a source file once it has been imported.</summary>
    public ImportedFileDisposition AfterImport { get; set; } = ImportedFileDisposition.MoveToSubfolder;

    /// <summary>
    /// Subfolder for files that were imported, created on demand.
    /// </summary>
    public string ImportedSubfolder { get; set; } = "Imported";

    /// <summary>
    /// Subfolder for files that could not be imported, created on demand.
    /// </summary>
    /// <remarks>
    /// Kept separate from the imported ones, and separate from the monitored folder. A
    /// file that failed must not be left where it is: the monitor would offer it again on
    /// every restart, for ever, and it would be indistinguishable from work still to do.
    /// Nor may it be filed as imported, which would be a lie. A folder of its own is the
    /// honest answer and the one somebody can act on.
    /// </remarks>
    public string FailedSubfolder { get; set; } = "Failed";

    /// <summary>
    /// Suffix appended when <see cref="AfterImport"/> is
    /// <see cref="ImportedFileDisposition.Rename"/>.
    /// </summary>
    public string RenameSuffix { get; set; } = ".imported";

    /// <summary>The formats used when none are configured.</summary>
    /// <remarks>
    /// Unambiguous forms first. The day-first forms are included because they are what a
    /// Portuguese system writes, and the month-first ones are deliberately absent: with
    /// both present, 03-04-1970 would match whichever came first in the list and nobody
    /// would know which.
    ///
    /// No format uses a slash, because a slash cannot appear in a file name on either
    /// platform: it is the directory separator. Listing one would be a format that could
    /// never match, which is worse than useless in a list somebody reads to work out why
    /// their date was not recognised.
    /// </remarks>
    public static IReadOnlyList<string> DefaultDateFormats { get; } = new[]
    {
        "yyyyMMdd",
        "yyyy-MM-dd",
        "ddMMyyyy",
        "dd-MM-yyyy",
        "dd.MM.yyyy",
    };

    /// <summary>The formats actually used, configured or default.</summary>
    public string[] EffectiveDateFormats =>
        DateFormats.Count > 0
            ? DateFormats.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray()
            : DefaultDateFormats.ToArray();

    /// <summary>Creates an independent copy, so an editing dialog cannot write through.</summary>
    public FilenameImportConfiguration Clone() => new()
    {
        IsEnabled = IsEnabled,
        Patterns = new List<string>(Patterns),
        DateFormats = new List<string>(DateFormats),
        UseFileDateAsStudyDate = UseFileDateAsStudyDate,
        DefaultSex = DefaultSex,
        SplitRunTogetherNames = SplitRunTogetherNames,
        PadPatientIdTo = PadPatientIdTo,
        AfterImport = AfterImport,
        ImportedSubfolder = ImportedSubfolder,
        FailedSubfolder = FailedSubfolder,
        RenameSuffix = RenameSuffix
    };
}
