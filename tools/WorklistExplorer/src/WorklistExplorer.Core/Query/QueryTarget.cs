using WorklistExplorer.Core.Configuration;

namespace WorklistExplorer.Core.Query;

/// <summary>
/// The server to ask and the filters to send to it. Mutable on purpose, because the window
/// lets the user edit an imported modality or station before asking; the imported file is
/// never touched.
/// </summary>
public sealed class QueryTarget
{
    public bool Selected { get; set; } = true;
    public string Name { get; set; } = "";
    public string CalledAeTitle { get; set; } = "";
    public string CallingAeTitle { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 104;
    public bool UseTls { get; set; }
    public bool AcceptAnyTlsCertificate { get; set; }

    /// <summary>Empty means the key is sent empty, which is universal matching for every modality.</summary>
    public string Modality { get; set; } = "";

    /// <summary>Empty means the key is sent empty, which is universal matching for every station.</summary>
    public string ScheduledStationAeTitle { get; set; } = "";

    /// <summary>Seconds without any response before the query is abandoned.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    // Read only context shown next to the editable values, so the user can compare what
    // the PDF to DICOM application would have sent with what this tool is sending.
    public bool ImportedIsEnabled { get; init; } = true;
    public string ImportedModality { get; init; } = "";
    public string ImportedStation { get; init; } = "";
    public string ImportedDateRange { get; init; } = "";
    public int ImportedMaximumResults { get; init; }
    public string Origin { get; init; } = "entered by hand";

    public string Endpoint => $"{CalledAeTitle}@{Host}:{Port}";

    public static QueryTarget FromImported(ImportedWorklist w, string topLevelCallingAe) => new()
    {
        // Disabled entries are imported but not ticked: the user may want to see them,
        // but should not be surprised by a query to a server the application does not use.
        Selected = w.IsEnabled,
        Name = w.Name,
        CalledAeTitle = w.CalledAeTitle,
        CallingAeTitle = string.IsNullOrEmpty(w.CallingAeTitleOverride) ? topLevelCallingAe : w.CallingAeTitleOverride,
        Host = w.Host,
        Port = w.Port,
        UseTls = w.UseTls,
        AcceptAnyTlsCertificate = w.AcceptAnyTlsCertificate,
        Modality = w.Modality,
        ScheduledStationAeTitle = w.ScheduledStationAeTitle,
        TimeoutSeconds = w.TimeoutSeconds > 0 ? w.TimeoutSeconds : 30,
        ImportedIsEnabled = w.IsEnabled,
        ImportedModality = w.Modality,
        ImportedStation = w.ScheduledStationAeTitle,
        ImportedDateRange = w.DateRangeDescription,
        ImportedMaximumResults = w.MaximumResults,
        Origin = "imported"
    };

    /// <summary>Returns the reasons this target cannot be queried, or an empty list.</summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Host)) problems.Add("host is empty");
        if (Port is < 1 or > 65535) problems.Add($"port {Port} is outside 1 to 65535");
        if (string.IsNullOrWhiteSpace(CalledAeTitle)) problems.Add("called AE title is empty");
        // No default calling AE is invented: presenting a title the server has not been
        // told about would produce a rejection that looks like a server fault.
        if (string.IsNullOrWhiteSpace(CallingAeTitle)) problems.Add("calling AE title is empty");
        if (CalledAeTitle.Length > 16) problems.Add("called AE title is longer than 16 characters");
        if (CallingAeTitle.Length > 16) problems.Add("calling AE title is longer than 16 characters");
        return problems;
    }
}
