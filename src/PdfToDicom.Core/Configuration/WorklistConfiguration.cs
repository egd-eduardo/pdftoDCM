namespace PdfToDicom.Core.Configuration;

/// <summary>
/// Which scheduled procedure steps a worklist query asks for.
/// </summary>
public enum WorklistDateRange
{
    /// <summary>Today only. The default, and what a reception desk wants almost always.</summary>
    Today = 0,

    /// <summary>Yesterday and today, for a document that arrives after a shift ends.</summary>
    YesterdayAndToday = 1,

    /// <summary>The last seven days, including today.</summary>
    LastSevenDays = 2,

    /// <summary>
    /// Every date. Sends no date key at all, so the server matches regardless.
    /// </summary>
    /// <remarks>
    /// A busy department's worklist can hold thousands of steps, so this is offered but
    /// not the default: an unbounded query is slow for the server and unhelpful on screen.
    /// </remarks>
    AnyDate = 3,

    /// <summary>An explicit range the operator sets.</summary>
    Explicit = 4
}

/// <summary>
/// Connection and query settings for a Modality Worklist SCP.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="PacsDestination"/> deliberately. A worklist server is
/// usually a different system from the archive that stores objects, often on a different
/// host, and conflating the two would mean a site could not point at both.
/// </remarks>
public sealed class WorklistConfiguration
{
    /// <summary>Whether the worklist is offered to the operator at all.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Application Entity Title of the worklist server.</summary>
    public string CalledAeTitle { get; set; } = "WORKLIST";

    /// <summary>Host name or address of the worklist server.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Port the worklist server listens on. 104 is the registered DICOM port.</summary>
    public int Port { get; set; } = 104;

    /// <summary>
    /// Calling AE Title to present. Empty means the application's own configured title is
    /// used, which is what most sites want.
    /// </summary>
    public string CallingAeTitleOverride { get; set; } = string.Empty;

    /// <summary>
    /// Modality to match, for example "OT". Empty matches every modality.
    /// </summary>
    /// <remarks>
    /// Empty is a legitimate and common choice here. A department converting documents
    /// for several modalities wants the whole worklist, and DICOM universal matching is
    /// exactly an empty value.
    /// </remarks>
    public string Modality { get; set; } = string.Empty;

    /// <summary>
    /// Scheduled Station AE Title to match. Empty matches every station, which is usually
    /// right: the document is being filed from a desk, not from the scheduled device.
    /// </summary>
    public string ScheduledStationAeTitle { get; set; } = string.Empty;

    /// <summary>Which scheduled dates to ask for.</summary>
    public WorklistDateRange DateRange { get; set; } = WorklistDateRange.Today;

    /// <summary>First date of an explicit range, used when <see cref="DateRange"/> is Explicit.</summary>
    public DateTime? ExplicitFrom { get; set; }

    /// <summary>Last date of an explicit range, used when <see cref="DateRange"/> is Explicit.</summary>
    public DateTime? ExplicitTo { get; set; }

    /// <summary>Seconds to wait for the association and the response.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Whether to connect over TLS.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// Accept any TLS certificate the server presents. Convenient on a closed hospital
    /// network with a self signed certificate, and a hole anywhere else.
    /// </summary>
    public bool AcceptAnyTlsCertificate { get; set; }

    /// <summary>
    /// Largest number of entries to show. A guard against a mistyped query returning a
    /// department's entire schedule and freezing the window.
    /// </summary>
    public int MaximumResults { get; set; } = 500;

    /// <summary>Creates an independent copy, used so an editing dialog cannot write through.</summary>
    public WorklistConfiguration Clone() => new()
    {
        IsEnabled = IsEnabled,
        CalledAeTitle = CalledAeTitle,
        Host = Host,
        Port = Port,
        CallingAeTitleOverride = CallingAeTitleOverride,
        Modality = Modality,
        ScheduledStationAeTitle = ScheduledStationAeTitle,
        DateRange = DateRange,
        ExplicitFrom = ExplicitFrom,
        ExplicitTo = ExplicitTo,
        TimeoutSeconds = TimeoutSeconds,
        UseTls = UseTls,
        AcceptAnyTlsCertificate = AcceptAnyTlsCertificate,
        MaximumResults = MaximumResults
    };

    /// <summary>
    /// Resolves the configured range to actual dates, or null when no date key should be
    /// sent at all.
    /// </summary>
    /// <param name="today">Today's date, injected so the behaviour can be tested.</param>
    public (DateTime From, DateTime To)? ResolveDates(DateTime today)
    {
        DateTime day = today.Date;

        return DateRange switch
        {
            WorklistDateRange.Today => (day, day),
            WorklistDateRange.YesterdayAndToday => (day.AddDays(-1), day),
            WorklistDateRange.LastSevenDays => (day.AddDays(-6), day),
            WorklistDateRange.AnyDate => null,
            WorklistDateRange.Explicit => ResolveExplicit(day),
            _ => (day, day)
        };
    }

    /// <summary>
    /// Falls back to today for either end of an explicit range that was left unset, and
    /// swaps the ends if they were entered the wrong way round.
    /// </summary>
    private (DateTime From, DateTime To) ResolveExplicit(DateTime today)
    {
        DateTime from = (ExplicitFrom ?? today).Date;
        DateTime to = (ExplicitTo ?? today).Date;

        return from <= to ? (from, to) : (to, from);
    }
}
