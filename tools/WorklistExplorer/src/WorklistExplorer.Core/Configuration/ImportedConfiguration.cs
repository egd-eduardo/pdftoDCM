using System.Text.Json;

namespace WorklistExplorer.Core.Configuration;

/// <summary>
/// One worklist entry as it was found in the PDF to DICOM configuration file.
/// Values are kept exactly as read so that the screen shows what the other application
/// would have used, including leftover filters.
/// </summary>
public sealed class ImportedWorklist
{
    /// <summary>Opaque identifier from the file. Kept so entries stay distinguishable; never displayed.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
    public string CalledAeTitle { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string CallingAeTitleOverride { get; init; } = "";
    public string Modality { get; init; } = "";
    public string ScheduledStationAeTitle { get; init; } = "";

    /// <summary>Raw DateRange value as written (0 to 4), or null when absent or unreadable.</summary>
    public int? DateRange { get; init; }
    public DateTime? ExplicitFrom { get; init; }
    public DateTime? ExplicitTo { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool UseTls { get; init; }
    public bool AcceptAnyTlsCertificate { get; init; }

    /// <summary>
    /// The other application's display cap. Used here only as the default page size,
    /// never as a fetch limit, because a fetch limit is exactly what may be hiding rows.
    /// </summary>
    public int MaximumResults { get; init; }

    public string DateRangeDescription => DateRange switch
    {
        0 => "0, today",
        1 => "1, yesterday and today",
        2 => "2, last seven days",
        3 => "3, any date",
        4 => $"4, explicit {ExplicitFrom:yyyy-MM-dd} to {ExplicitTo:yyyy-MM-dd}",
        null => "not present",
        var other => $"{other}, unknown value"
    };
}

public sealed class ImportedConfiguration
{
    public string SourcePath { get; init; } = "";
    public string CallingAeTitle { get; init; } = "";
    public List<ImportedWorklist> Worklists { get; } = new();

    /// <summary>Plain language notes about what was and was not found in the file.</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Reads the PDF to DICOM configuration strictly read only.
/// </summary>
/// <remarks>
/// JsonDocument is used instead of deserialising into typed classes on purpose: a typed
/// deserialiser silently fills defaults for missing or misspelt properties, which would
/// hide exactly the kind of configuration problem this tool exists to reveal. Here every
/// absent property is noticed and reported.
/// </remarks>
public static class ConfigurationReader
{
    /// <summary>The path the PDF to DICOM application itself uses on this platform.</summary>
    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PdfToDicom", "config.json");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(root, "PdfToDicom", "config.json");
    }

    public static ImportedConfiguration Read(string path)
    {
        // FileAccess.Read guarantees this process cannot write the file. FileShare.ReadWrite
        // lets us read it while the PDF to DICOM application has it open, instead of failing
        // or, worse, forcing the user to close the application being diagnosed.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            // The file is written by System.Text.Json and should hold neither, but a
            // hand edited copy from another machine might; refusing it would help nobody.
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = doc.RootElement;
        var config = new ImportedConfiguration
        {
            SourcePath = Path.GetFullPath(path),
            CallingAeTitle = GetString(root, "CallingAeTitle") ?? ""
        };

        if (GetString(root, "CallingAeTitle") is null)
            config.Notes.Add("The file has no top level CallingAeTitle.");

        if (root.TryGetProperty("Worklists", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                index++;
                config.Worklists.Add(ReadWorklist(item, $"Worklists[{index}]", config.Notes));
            }
            config.Notes.Add($"Read {config.Worklists.Count} worklist(s) from the Worklists array.");
        }
        else if (root.TryGetProperty("Worklist", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            config.Worklists.Add(ReadWorklist(single, "Worklist", config.Notes));
            config.Notes.Add("Read one worklist from the older single Worklist object.");
        }
        else
        {
            config.Notes.Add("Neither a Worklists array nor a Worklist object was found in the file. Enter a server by hand.");
        }

        return config;
    }

    private static ImportedWorklist ReadWorklist(JsonElement e, string label, List<string> notes)
    {
        string Str(string name)
        {
            var value = GetString(e, name);
            if (value is null) notes.Add($"{label}: property {name} is missing; treated as empty.");
            return value ?? "";
        }

        return new ImportedWorklist
        {
            Id = GetString(e, "Id") ?? "",
            Name = Str("Name"),
            IsEnabled = GetBool(e, "IsEnabled") ?? true,
            CalledAeTitle = Str("CalledAeTitle"),
            Host = Str("Host"),
            Port = GetInt(e, "Port") ?? 0,
            CallingAeTitleOverride = GetString(e, "CallingAeTitleOverride") ?? "",
            Modality = GetString(e, "Modality") ?? "",
            ScheduledStationAeTitle = GetString(e, "ScheduledStationAeTitle") ?? "",
            DateRange = GetDateRange(e),
            ExplicitFrom = GetDate(e, "ExplicitFrom"),
            ExplicitTo = GetDate(e, "ExplicitTo"),
            TimeoutSeconds = GetInt(e, "TimeoutSeconds") ?? 30,
            UseTls = GetBool(e, "UseTls") ?? false,
            AcceptAnyTlsCertificate = GetBool(e, "AcceptAnyTlsCertificate") ?? false,
            MaximumResults = GetInt(e, "MaximumResults") ?? 0
        };
    }

    // Property names are matched exactly (PascalCase), because the file is written with
    // PropertyNamingPolicy = null. Matching case insensitively would make a misspelt
    // property look correct here while the real application ignores it.
    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static DateTime? GetDate(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var d) ? d : null;

    /// <summary>
    /// The enum is normally written as a number. A string is accepted as well, in case the
    /// file was produced with an enum converter, so that the value is shown rather than lost.
    /// </summary>
    private static int? GetDateRange(JsonElement e)
    {
        if (!e.TryGetProperty("DateRange", out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind != JsonValueKind.String) return null;

        return v.GetString()?.Replace(" ", "").ToUpperInvariant() switch
        {
            "TODAY" => 0,
            "YESTERDAYANDTODAY" => 1,
            "LASTSEVENDAYS" => 2,
            "ANYDATE" or "ANY" => 3,
            "EXPLICIT" => 4,
            _ => null
        };
    }
}
