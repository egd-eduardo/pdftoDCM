using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfToDicom.Core.Configuration;

/// <summary>
/// Which parts of the configuration an export carries or an import brings in.
/// </summary>
/// <remarks>
/// A flags enumeration because the two are chosen independently: a site may hand out a
/// file of archives alone, of worklist servers alone, or of both.
/// </remarks>
[Flags]
public enum SettingsTransferParts
{
    /// <summary>Nothing. A file with neither part is of no use, and is reported as such.</summary>
    None = 0,

    /// <summary>The archives that objects are sent to.</summary>
    Destinations = 1,

    /// <summary>The Modality Worklist servers.</summary>
    Worklists = 2,

    /// <summary>Both.</summary>
    Both = Destinations | Worklists
}

/// <summary>
/// What an import does with the entries already configured on the machine.
/// </summary>
public enum SettingsImportMode
{
    /// <summary>
    /// Keep what is here and add what is missing. A server already configured at the same
    /// AE Title, host and port is left exactly as it is.
    /// </summary>
    AddToExisting = 0,

    /// <summary>
    /// Discard the configured entries of the chosen kind and take the file as it stands.
    /// Used when one workstation is the reference and the rest should match it.
    /// </summary>
    ReplaceExisting = 1
}

/// <summary>
/// A file of connection settings, written on one workstation and read on another.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the whole configuration. What is carried is the part that is the same
/// on every workstation in a department and tedious to retype: the archives and the
/// worklist servers. What is left out is the part that is properly per machine, and the
/// omission that matters most is the <b>calling AE Title</b>. Copying that would give two
/// workstations the same identity on the network, which is the one thing a PACS
/// administrator will object to, and it would be copied silently, which is worse.
/// Institution, folders, language and templates are left out for the same reason: they are
/// local, and a file that quietly changed them would be a trap.
/// </para>
/// <para>
/// Nothing secret travels in this file. Neither a destination nor a worklist server holds
/// a password; DICOM associations here are identified by AE Title. The file is still
/// written from behind the password-protected pages of the settings dialog, because it
/// does disclose the addresses of clinical systems.
/// </para>
/// </remarks>
public sealed class SettingsBundle
{
    /// <summary>
    /// Marks the file as this application's. Checked on reading, so that a JSON file that
    /// happens to parse is not mistaken for settings.
    /// </summary>
    /// <remarks>
    /// Empty by default, and deliberately so. A property left at its initialised value is
    /// indistinguishable, after deserialization, from one the file never mentioned, so a
    /// default of the marker itself would have made every JSON file without this property
    /// look like one of ours. <see cref="SettingsTransfer.Write"/> stamps it instead.
    /// </remarks>
    public string FileKind { get; set; } = string.Empty;

    /// <summary>Version of this file's own layout, not of the application.</summary>
    public int FormatVersion { get; set; }

    /// <summary>Version of the configuration the entries were taken from.</summary>
    public int SourceConfigurationVersion { get; set; } = AppConfiguration.CurrentVersion;

    /// <summary>When the file was written, in UTC.</summary>
    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Which workstation it came from, so that a file found on a stick months later can
    /// still be placed. Informational only; nothing is decided from it.
    /// </summary>
    public string ExportedFrom { get; set; } = string.Empty;

    /// <summary>Version of the application that wrote the file. Informational only.</summary>
    public string ApplicationVersion { get; set; } = string.Empty;

    /// <summary>The archives, or an empty list when they were not exported.</summary>
    public List<PacsDestination> Destinations { get; set; } = new();

    /// <summary>The worklist servers, or an empty list when they were not exported.</summary>
    public List<WorklistConfiguration> Worklists { get; set; } = new();

    /// <summary>What this file actually holds, whatever was asked for when it was written.</summary>
    [JsonIgnore]
    public SettingsTransferParts Parts =>
        (Destinations.Count > 0 ? SettingsTransferParts.Destinations : SettingsTransferParts.None) |
        (Worklists.Count > 0 ? SettingsTransferParts.Worklists : SettingsTransferParts.None);
}

/// <summary>The outcome of reading a settings file.</summary>
/// <param name="Bundle">The contents, or null when the file could not be used.</param>
/// <param name="Problem">
/// Why it could not be used, in words an operator can act on. Empty when
/// <paramref name="Bundle"/> is set.
/// </param>
public sealed record SettingsBundleRead(SettingsBundle? Bundle, string Problem)
{
    /// <summary>True when the file was read and can be imported.</summary>
    public bool Succeeded => Bundle is not null;
}

/// <summary>What an import changed, so that it can be reported rather than assumed.</summary>
/// <param name="DestinationsAdded">Archives brought in.</param>
/// <param name="DestinationsAlreadyPresent">Archives skipped because the same server was already configured.</param>
/// <param name="DestinationsRemoved">Archives discarded, which only a replacing import does.</param>
/// <param name="WorklistsAdded">Worklist servers brought in.</param>
/// <param name="WorklistsAlreadyPresent">Worklist servers skipped because the same server was already configured.</param>
/// <param name="WorklistsRemoved">Worklist servers discarded, which only a replacing import does.</param>
public sealed record SettingsImportReport(
    int DestinationsAdded,
    int DestinationsAlreadyPresent,
    int DestinationsRemoved,
    int WorklistsAdded,
    int WorklistsAlreadyPresent,
    int WorklistsRemoved)
{
    /// <summary>An import that left the configuration exactly as it was.</summary>
    public bool ChangedNothing =>
        DestinationsAdded == 0 && DestinationsRemoved == 0 &&
        WorklistsAdded == 0 && WorklistsRemoved == 0;
}

/// <summary>
/// Writes and reads <see cref="SettingsBundle"/> files, and applies one to a configuration.
/// </summary>
/// <remarks>
/// Lives in Core, with no reference to the desktop application, so that the same transfer
/// can be driven from the command line tool or from a deployment script later without any
/// of this logic being written a second time.
/// </remarks>
public static class SettingsTransfer
{
    /// <summary>The marker written into, and checked on reading, every settings file.</summary>
    public const string FileKind = "PdfToDicom.Settings";

    /// <summary>The layout this build writes, and the newest it can read.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The suggested file name extension, chosen so the files are recognisable.</summary>
    public const string FileExtension = ".pdftodicom-settings.json";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Collects the chosen parts of a configuration into a bundle ready to be written.
    /// </summary>
    /// <param name="configuration">The configuration to take entries from.</param>
    /// <param name="parts">Which kinds to include.</param>
    /// <param name="exportedFrom">A name for the originating workstation.</param>
    /// <param name="applicationVersion">The running version, recorded in the file.</param>
    /// <remarks>
    /// The entries are copied, so a later edit in the settings dialog cannot change a
    /// bundle already built. Each copy has its last successful transmission timestamp
    /// cleared: that is a record of what happened on the machine it came from, not a
    /// setting, and carrying it over would order another machine's recent list by
    /// transmissions it never made.
    /// </remarks>
    public static SettingsBundle CreateBundle(
        AppConfiguration configuration,
        SettingsTransferParts parts,
        string exportedFrom,
        string applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var bundle = new SettingsBundle
        {
            SourceConfigurationVersion = configuration.ConfigurationVersion,
            ExportedUtc = DateTime.UtcNow,
            ExportedFrom = exportedFrom ?? string.Empty,
            ApplicationVersion = applicationVersion ?? string.Empty
        };

        if (parts.HasFlag(SettingsTransferParts.Destinations))
        {
            foreach (PacsDestination destination in configuration.Destinations ?? new List<PacsDestination>())
            {
                PacsDestination copy = destination.Clone();
                copy.LastSuccessfulUseUtc = null;
                bundle.Destinations.Add(copy);
            }
        }

        if (parts.HasFlag(SettingsTransferParts.Worklists))
        {
            foreach (WorklistConfiguration worklist in configuration.Worklists ?? new List<WorklistConfiguration>())
            {
                bundle.Worklists.Add(CloneWorklist(worklist));
            }
        }

        return bundle;
    }

    /// <summary>Writes a bundle to disk.</summary>
    /// <remarks>
    /// Written through a temporary file and then moved into place, as the configuration
    /// itself is, so that an interrupted write cannot leave half a file that somebody later
    /// tries to import. The bundle is stamped with the file marker and the current layout
    /// version on the way out.
    /// </remarks>
    public static void Write(SettingsBundle bundle, string path)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Stamped here rather than defaulted on the property, so that a written file always
        // carries the marker and the layout it was actually written in.
        bundle.FileKind = FileKind;
        bundle.FormatVersion = CurrentFormatVersion;

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(bundle, FileOptions));
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    /// <summary>
    /// Reads a settings file, reporting in plain words why it cannot be used rather than
    /// throwing.
    /// </summary>
    /// <remarks>
    /// The file is one a person chose from a disk or a stick, so every way it can be wrong
    /// is an ordinary outcome: the wrong file, a truncated copy, something written by a
    /// later version. None of them is exceptional and none of them should reach a crash
    /// dialog.
    /// </remarks>
    public static SettingsBundleRead Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new SettingsBundleRead(null, "No file was chosen.");
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SettingsBundleRead(null, $"'{path}' is not a usable file path ({ex.Message}).");
        }

        if (!File.Exists(fullPath))
        {
            return new SettingsBundleRead(null, $"There is no file at '{fullPath}'.");
        }

        SettingsBundle? bundle;

        try
        {
            bundle = JsonSerializer.Deserialize<SettingsBundle>(File.ReadAllText(fullPath), FileOptions);
        }
        catch (JsonException ex)
        {
            return new SettingsBundleRead(
                null,
                $"'{Path.GetFileName(fullPath)}' is not a valid settings file: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsBundleRead(null, $"'{Path.GetFileName(fullPath)}' could not be read: {ex.Message}");
        }

        if (bundle is null)
        {
            return new SettingsBundleRead(null, $"'{Path.GetFileName(fullPath)}' is empty.");
        }

        if (!string.Equals(bundle.FileKind, FileKind, StringComparison.Ordinal))
        {
            return new SettingsBundleRead(
                null,
                $"'{Path.GetFileName(fullPath)}' is a JSON file, but it was not exported by " +
                "PDF to DICOM. Choose the file written by Settings, Backup, Export.");
        }

        if (bundle.FormatVersion > CurrentFormatVersion)
        {
            return new SettingsBundleRead(
                null,
                $"'{Path.GetFileName(fullPath)}' was exported by a newer version of the application " +
                $"(file layout {bundle.FormatVersion}, this build reads {CurrentFormatVersion}). " +
                "Export it again from a workstation running this version, or update this one.");
        }

        // A hand-edited or truncated file can parse with a missing list.
        bundle.Destinations ??= new List<PacsDestination>();
        bundle.Worklists ??= new List<WorklistConfiguration>();

        if (bundle.Parts == SettingsTransferParts.None)
        {
            return new SettingsBundleRead(
                null,
                $"'{Path.GetFileName(fullPath)}' holds no archives and no worklist servers, " +
                "so there is nothing to import.");
        }

        return new SettingsBundleRead(bundle, string.Empty);
    }

    /// <summary>
    /// Brings the chosen parts of a bundle into a configuration.
    /// </summary>
    /// <param name="bundle">What was read from the file.</param>
    /// <param name="target">The configuration to change, in place.</param>
    /// <param name="parts">Which kinds to bring in, which need not be all the file holds.</param>
    /// <param name="mode">Whether to add to what is configured or replace it.</param>
    /// <returns>What was changed.</returns>
    /// <remarks>
    /// <para>
    /// Two servers are treated as the same server when their AE Title, host and port all
    /// match, ignoring case and surrounding spaces. The identifier is not used for this:
    /// identifiers are generated per machine, so the same archive entered by hand on two
    /// workstations has two different ones, and matching on them would add a duplicate
    /// every time a file was imported.
    /// </para>
    /// <para>
    /// An incoming entry always gets a fresh identifier. The one in the file belongs to the
    /// machine the file came from, and two entries sharing one would make "the server used
    /// last" ambiguous.
    /// </para>
    /// </remarks>
    public static SettingsImportReport Apply(
        SettingsBundle bundle,
        AppConfiguration target,
        SettingsTransferParts parts,
        SettingsImportMode mode)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(target);

        target.Destinations ??= new List<PacsDestination>();
        target.Worklists ??= new List<WorklistConfiguration>();

        int destinationsAdded = 0;
        int destinationsPresent = 0;
        int destinationsRemoved = 0;
        int worklistsAdded = 0;
        int worklistsPresent = 0;
        int worklistsRemoved = 0;

        if (parts.HasFlag(SettingsTransferParts.Destinations))
        {
            if (mode == SettingsImportMode.ReplaceExisting)
            {
                destinationsRemoved = target.Destinations.Count;
                target.Destinations.Clear();
            }

            foreach (PacsDestination incoming in bundle.Destinations)
            {
                PacsDestination copy = incoming.Clone();
                Normalise(copy);

                if (target.Destinations.Any(existing => IsSameServer(
                        existing.CalledAeTitle, existing.Host, existing.Port,
                        copy.CalledAeTitle, copy.Host, copy.Port)))
                {
                    destinationsPresent++;
                    continue;
                }

                copy.Id = Guid.NewGuid().ToString("N");
                copy.LastSuccessfulUseUtc = null;
                copy.Name = UniqueName(
                    copy.Name,
                    copy.CalledAeTitle,
                    target.Destinations.Select(existing => existing.Name));

                target.Destinations.Add(copy);
                destinationsAdded++;
            }
        }

        if (parts.HasFlag(SettingsTransferParts.Worklists))
        {
            if (mode == SettingsImportMode.ReplaceExisting)
            {
                worklistsRemoved = target.Worklists.Count;
                target.Worklists.Clear();
            }

            foreach (WorklistConfiguration incoming in bundle.Worklists)
            {
                WorklistConfiguration copy = CloneWorklist(incoming);
                Normalise(copy);

                if (target.Worklists.Any(existing => IsSameServer(
                        existing.CalledAeTitle, existing.Host, existing.Port,
                        copy.CalledAeTitle, copy.Host, copy.Port)))
                {
                    worklistsPresent++;
                    continue;
                }

                copy.Id = Guid.NewGuid().ToString("N");
                copy.Name = UniqueName(
                    copy.Name,
                    copy.CalledAeTitle,
                    target.Worklists.Select(existing => existing.Name));

                target.Worklists.Add(copy);
                worklistsAdded++;
            }

            // The server remembered as the last one used may have just been discarded.
            // Left pointing at nothing, the picker would open on no selection at all.
            if (target.Worklists.All(worklist => worklist.Id != target.LastUsedWorklistId))
            {
                target.LastUsedWorklistId = string.Empty;
            }
        }

        return new SettingsImportReport(
            destinationsAdded,
            destinationsPresent,
            destinationsRemoved,
            worklistsAdded,
            worklistsPresent,
            worklistsRemoved);
    }

    /// <summary>
    /// Two entries point at the same server when the AE Title, host and port all match.
    /// </summary>
    private static bool IsSameServer(
        string aeTitleA, string hostA, int portA,
        string aeTitleB, string hostB, int portB) =>
        portA == portB &&
        string.Equals((aeTitleA ?? string.Empty).Trim(), (aeTitleB ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals((hostA ?? string.Empty).Trim(), (hostB ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a name no entry in the list is already using.
    /// </summary>
    /// <remarks>
    /// Two entries with one name in a list somebody picks from is a way to send a report to
    /// the wrong archive, so an imported entry whose name is taken gets a number. The
    /// address it points at is untouched; only what it is called changes.
    /// </remarks>
    private static string UniqueName(string proposed, string fallback, IEnumerable<string> taken)
    {
        string candidate = string.IsNullOrWhiteSpace(proposed)
            ? (string.IsNullOrWhiteSpace(fallback) ? "Imported" : fallback.Trim())
            : proposed.Trim();

        var used = new HashSet<string>(
            taken.Select(name => (name ?? string.Empty).Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (!used.Contains(candidate))
        {
            return candidate;
        }

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string numbered = $"{candidate} ({suffix})";

            if (!used.Contains(numbered))
            {
                return numbered;
            }
        }

        return $"{candidate} ({Guid.NewGuid().ToString("N")[..6]})";
    }

    /// <summary>
    /// Brings an incoming entry's numbers inside their allowed range.
    /// </summary>
    /// <remarks>
    /// The same clamping the configuration service applies when saving. Done here as well
    /// because an imported entry is shown in the settings dialog before anything is saved,
    /// and a port of 0 from a hand-edited file should not be on screen as if it were usable.
    /// </remarks>
    private static void Normalise(PacsDestination destination)
    {
        destination.Port = Math.Clamp(destination.Port, 1, 65535);
        destination.TimeoutSeconds = Math.Clamp(destination.TimeoutSeconds, 1, 600);
        destination.RetryCount = Math.Clamp(destination.RetryCount, 0, 10);
        destination.RetryDelaySeconds = Math.Clamp(destination.RetryDelaySeconds, 0, 120);
    }

    /// <inheritdoc cref="Normalise(PacsDestination)" />
    private static void Normalise(WorklistConfiguration worklist)
    {
        worklist.Port = Math.Clamp(worklist.Port, 1, 65535);
        worklist.TimeoutSeconds = Math.Clamp(worklist.TimeoutSeconds, 1, 600);
        worklist.MaximumResults = Math.Clamp(worklist.MaximumResults, 1, 5_000);
    }

    /// <summary>
    /// Copies a worklist entry.
    /// </summary>
    /// <remarks>
    /// Through the serializer rather than property by property. A hand written copy is how
    /// a setting goes missing when a new one is added: that has already happened once in
    /// this application, to the configuration clone, and cost a release.
    /// </remarks>
    private static WorklistConfiguration CloneWorklist(WorklistConfiguration worklist)
    {
        string json = JsonSerializer.Serialize(worklist, FileOptions);
        return JsonSerializer.Deserialize<WorklistConfiguration>(json, FileOptions) ?? new WorklistConfiguration();
    }
}
