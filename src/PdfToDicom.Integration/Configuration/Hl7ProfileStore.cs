using PdfToDicom.Interop.Hl7V2.Profiles;

namespace PdfToDicom.Integration.Configuration;

/// <summary>One profile on this machine, as a list shows it.</summary>
/// <param name="ProfileId">The identifier a routing rule refers to.</param>
/// <param name="DisplayName">What the profile calls itself.</param>
/// <param name="MessageType">The message type and trigger event, as <c>ORU^R01</c>.</param>
/// <param name="Revision">Its revision, so a log entry can be read against it.</param>
/// <param name="IsUsable">Whether it holds together well enough to build a message.</param>
/// <param name="Path">The file it was read from.</param>
/// <param name="Issues">Everything the reader objected to, usable or not.</param>
public sealed record StoredProfile(
    string ProfileId,
    string DisplayName,
    string MessageType,
    int Revision,
    bool IsUsable,
    string Path,
    IReadOnlyList<ProfileIssue> Issues)
{
    /// <summary>A one line description, for a list.</summary>
    public string DisplaySummary =>
        $"{(DisplayName.Length > 0 ? DisplayName : ProfileId)} ({MessageType}, revision {Revision})";
}

/// <summary>
/// The profiles installed on this machine, held as files.
/// </summary>
/// <remarks>
/// <para>
/// Files in a folder, rather than profile documents embedded in the settings file. Three
/// reasons, in order of weight. A profile is written against a vendor's conformance
/// statement and belongs under change control, which a file is and a string inside another
/// file is not. A profile that fails to read must be visible and repairable rather than
/// taking the whole settings file down with it. And an integration engineer hands over a
/// profile the way they hand over any other document, by sending the file.
/// </para>
/// <para>
/// The cost is that a profile can be moved or deleted behind the application's back, which
/// is why nothing holds a path: a rule names a profile identifier, the folder is read when
/// it is needed, and a rule whose profile is missing is reported rather than discovered
/// during a send.
/// </para>
/// </remarks>
public sealed class Hl7ProfileStore
{
    private readonly string _folder;

    /// <summary>Creates a store over a folder, creating it if it is not there.</summary>
    public Hl7ProfileStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = folder;
    }

    /// <summary>Where the profiles live, which the settings page shows.</summary>
    public string Folder => _folder;

    /// <summary>
    /// Every profile in the folder, readable or not, in display order.
    /// </summary>
    /// <remarks>
    /// A file that cannot be read at all is still listed, with the reader's objection, so
    /// that a profile somebody has just edited by hand and broken is visible on the page
    /// rather than simply absent from it.
    /// </remarks>
    public IReadOnlyList<StoredProfile> List()
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        var profiles = new List<StoredProfile>();

        foreach (string path in Directory.EnumerateFiles(_folder, "*.json").OrderBy(p => p))
        {
            profiles.Add(Read(path));
        }

        return profiles
            .OrderBy(p => p.DisplayName.Length > 0 ? p.DisplayName : p.ProfileId, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Reads one profile file, whether or not it is any good.</summary>
    public StoredProfile Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;

        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable(path, ex.Message);
        }

        ProfileReadResult result;

        try
        {
            result = Hl7ProfileReader.Read(json);
        }
        catch (Exception ex)
        {
            // The reader reports a malformed document through its result rather than by
            // throwing, so this is for the case it did not anticipate. A settings page that
            // cannot open because one file in a folder is wrong is worse than a list with
            // one bad entry in it.
            return Unreadable(path, ex.Message);
        }

        if (result.Profile is null)
        {
            return new StoredProfile(
                Path.GetFileNameWithoutExtension(path), string.Empty, string.Empty, 0,
                false, path, result.Issues);
        }

        return new StoredProfile(
            result.Profile.ProfileId,
            result.Profile.DisplayName,
            result.Profile.MessageTypeAndTrigger,
            result.Profile.Revision,
            result.IsUsable,
            path,
            result.Issues);
    }

    /// <summary>The profile with this identifier, or null when there is none.</summary>
    public Hl7Profile? Find(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || !Directory.Exists(_folder))
        {
            return null;
        }

        foreach (string path in Directory.EnumerateFiles(_folder, "*.json").OrderBy(p => p))
        {
            string json;

            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            ProfileReadResult result = Hl7ProfileReader.Read(json);

            if (result.IsUsable &&
                string.Equals(result.Profile!.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                return result.Profile;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies a profile file into the folder, refusing one that cannot be used.
    /// </summary>
    /// <remarks>
    /// Refused rather than imported with a warning. A profile that does not hold together
    /// cannot build a message, so importing it would put a name in a list that no rule could
    /// ever use, and the first anybody would hear of it is a refusal at send time.
    /// </remarks>
    public ProfileImportResult Import(string sourcePath, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!File.Exists(sourcePath))
        {
            return new ProfileImportResult(false, string.Empty, $"There is no file at '{sourcePath}'.");
        }

        StoredProfile candidate = Read(sourcePath);

        if (!candidate.IsUsable)
        {
            string detail = candidate.Issues.Count > 0
                ? string.Join("; ", candidate.Issues.Select(i => $"{i.Location}: {i.Message}"))
                : "the file is not a profile this build can read";

            return new ProfileImportResult(false, candidate.ProfileId, detail);
        }

        Directory.CreateDirectory(_folder);

        // Named after the identifier rather than after the file it came from, so that
        // importing the same profile twice replaces it instead of producing two files that
        // claim the same identifier and disagree.
        string target = Path.Combine(_folder, SafeFileName(candidate.ProfileId) + ".json");

        if (File.Exists(target) && !overwrite)
        {
            return new ProfileImportResult(
                false, candidate.ProfileId,
                $"A profile called '{candidate.ProfileId}' is already installed.");
        }

        File.Copy(sourcePath, target, overwrite: true);

        return new ProfileImportResult(true, candidate.ProfileId, string.Empty);
    }

    /// <summary>Removes a profile file from the folder.</summary>
    public bool Remove(StoredProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!File.Exists(profile.Path))
        {
            return false;
        }

        File.Delete(profile.Path);
        return true;
    }

    private StoredProfile Unreadable(string path, string detail) =>
        new(Path.GetFileNameWithoutExtension(path), string.Empty, string.Empty, 0, false, path,
            [new ProfileIssue(ProfileIssueSeverity.Error, Path.GetFileName(path), detail)]);

    /// <summary>
    /// Turns a profile identifier into something that can be a file name on Windows.
    /// </summary>
    /// <remarks>
    /// A profile identifier is written by hand and nothing stops it holding a colon or a
    /// slash. Replacing those is enough; the name only has to be stable and legal, because
    /// nothing looks a profile up by its file name.
    /// </remarks>
    private static string SafeFileName(string profileId)
    {
        var safe = new string(profileId
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)
            .ToArray())
            .Trim();

        return safe.Length > 0 ? safe : "profile";
    }
}

/// <summary>What became of an attempt to install a profile.</summary>
/// <param name="Succeeded">Whether it is now installed.</param>
/// <param name="ProfileId">Its identifier, where one could be read.</param>
/// <param name="Detail">Why not, when it failed.</param>
public sealed record ProfileImportResult(bool Succeeded, string ProfileId, string Detail);
