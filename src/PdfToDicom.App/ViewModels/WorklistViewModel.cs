using System.Collections.ObjectModel;
using System.Windows;
using PdfToDicom.App.Infrastructure;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Networking;
using PdfToDicom.Core.Services;

namespace PdfToDicom.App.ViewModels;

/// <summary>
/// Drives the worklist search window.
/// </summary>
/// <remarks>
/// The window's whole purpose is to remove typing from the step where a mistake is most
/// expensive. Nothing here edits patient information; it only finds a scheduled procedure
/// and hands it back to the main form.
/// </remarks>
public sealed class WorklistViewModel : ObservableObject
{
    private readonly ApplicationServices _services;

    private bool _isBusy;
    private string _status = string.Empty;
    private string _patientIdFilter = string.Empty;
    private string _surnameFilter = string.Empty;
    private WorklistEntry? _selectedEntry;
    private WorklistChoice? _selectedServer;
    private WorklistDateRange _selectedDateRange = WorklistDateRange.Today;
    private DateTime? _explicitFrom;
    private DateTime? _explicitTo;

    /// <summary>
    /// Everything the last search fetched, of which <see cref="Entries"/> shows one page.
    /// </summary>
    /// <remarks>
    /// Held here rather than cut away by the query, because an entry that is fetched and
    /// discarded cannot be looked for: that is exactly how an examination scheduled a year
    /// ago went missing from a list that looked complete.
    /// </remarks>
    private readonly List<WorklistEntry> _fetched = new();

    private int _pageIndex;
    private int _pageSize = WorklistPaging.DefaultPageSize;

    /// <summary>Creates the view model.</summary>
    public WorklistViewModel(ApplicationServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        AppConfiguration configuration = _services.ConfigurationService.Current;
        IReadOnlyList<WorklistConfiguration> enabled = configuration.EnabledWorklists;

        foreach (WorklistConfiguration server in enabled)
        {
            Servers.Add(new WorklistChoice(server, server.DisplayName));
        }

        // Offered only when there is more than one, because asking two servers where there
        // is one is the same query twice and a pointless choice to put in front of somebody.
        if (enabled.Count > 1)
        {
            Servers.Add(WorklistChoice.All(Localize("Worklist.AllServers")));
        }

        // The one used last, so a department that always asks the same server never picks
        // it again. Falls back to the first, which is the order they are configured in.
        WorklistConfiguration? preferred = configuration.PreferredWorklist;

        _selectedServer =
            Servers.FirstOrDefault(c => c.Server is not null && ReferenceEquals(c.Server, preferred))
            ?? Servers.FirstOrDefault();

        SearchCommand = new RelayCommand(async _ => await SearchAsync(), _ => !IsBusy);
        ClearFiltersCommand = new RelayCommand(_ => ClearFilters(), _ => !IsBusy);

        FirstPageCommand = new RelayCommand(_ => GoToPage(0), _ => CanGoBack);
        PreviousPageCommand = new RelayCommand(_ => GoToPage(PageIndex - 1), _ => CanGoBack);
        NextPageCommand = new RelayCommand(_ => GoToPage(PageIndex + 1), _ => CanGoForward);
        LastPageCommand = new RelayCommand(_ => GoToPage(PageCount - 1), _ => CanGoForward);

        // The window is opened to find something, so the first search runs without being
        // asked for. A reception desk should see today's list immediately.
        _ = SearchAsync();
    }

    /// <summary>The worklist servers that may be queried, plus "all" when there are several.</summary>
    public ObservableCollection<WorklistChoice> Servers { get; } = new();

    /// <summary>
    /// Which server, or all of them, the next search will ask.
    /// </summary>
    /// <remarks>
    /// Changing it searches again at once. The alternative is a picker that appears to do
    /// nothing until a second button is pressed, which invites the operator to read a list
    /// from one department as though it came from another.
    /// </remarks>
    public WorklistChoice? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (!SetProperty(ref _selectedServer, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowsSourceColumn));
            AdoptServerDates(value?.Server);
            RememberChoice();
            _ = SearchAsync();
        }
    }

    /// <summary>True when the picker is worth showing at all.</summary>
    public bool HasSeveralServers => Servers.Count > 1;

    /// <summary>
    /// True when the results can have come from more than one server, so the list needs to
    /// say which.
    /// </summary>
    public bool ShowsSourceColumn => SelectedServer?.IsAll == true;

    /// <summary>The page of entries currently shown.</summary>
    public ObservableCollection<WorklistEntry> Entries { get; } = new();

    // ------------------------------------------------------------------ The pages

    /// <summary>Goes to the first page.</summary>
    public System.Windows.Input.ICommand FirstPageCommand { get; }

    /// <summary>Goes back one page.</summary>
    public System.Windows.Input.ICommand PreviousPageCommand { get; }

    /// <summary>Goes forward one page.</summary>
    public System.Windows.Input.ICommand NextPageCommand { get; }

    /// <summary>Goes to the last page.</summary>
    public System.Windows.Input.ICommand LastPageCommand { get; }

    /// <summary>How many entries the last search fetched, across every server asked.</summary>
    public int TotalCount => _fetched.Count;

    /// <summary>
    /// How many entries one page holds.
    /// </summary>
    /// <remarks>
    /// Taken from the server's own configured maximum, so a site that chose 200 still sees
    /// 200 at a time. When several servers are asked at once the largest of their values
    /// wins, because the alternative is for the quietest department's setting to decide how
    /// the busiest one is read.
    /// </remarks>
    public int PageSize
    {
        get => _pageSize;
        private set
        {
            if (SetProperty(ref _pageSize, WorklistPaging.SanePageSize(value)))
            {
                RefreshPage();
            }
        }
    }

    /// <summary>Which page is shown, counted from zero.</summary>
    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                OnPropertyChanged(nameof(PageNumber));
                OnPropertyChanged(nameof(PageSummary));
            }
        }
    }

    /// <summary>Which page is shown, counted from one, which is how people count pages.</summary>
    public int PageNumber => PageIndex + 1;

    /// <summary>
    /// How many pages the fetched entries make.
    /// </summary>
    /// <remarks>
    /// One when nothing was found, rather than zero. "Page 1 of 0" is not a thing anybody
    /// can read, and an empty list is one empty page.
    /// </remarks>
    public int PageCount => WorklistPaging.PageCount(TotalCount, PageSize);

    /// <summary>True when there is more than one page, so the pager is worth showing.</summary>
    public bool HasSeveralPages => PageCount > 1;

    /// <summary>
    /// Whether the grid lets a column header reorder the list.
    /// </summary>
    /// <remarks>
    /// Off while there is more than one page, which is the uncomfortable half of paging. A
    /// grid sorts what it has been given, so sorting a page by scheduled date reorders five
    /// hundred rows out of three thousand and presents the result as though it were the
    /// whole answer. That is the same defect as cutting a list before sorting it, which this
    /// release exists to fix, arrived at from the other direction.
    /// </remarks>
    public bool CanSortColumns => !HasSeveralPages;

    /// <summary>True when there is a page before this one.</summary>
    public bool CanGoBack => PageIndex > 0;

    /// <summary>True when there is a page after this one.</summary>
    public bool CanGoForward => PageIndex + 1 < PageCount;

    /// <summary>
    /// What the pager says, for example "Page 2 of 7, showing 501 to 1000 of 3214".
    /// </summary>
    /// <remarks>
    /// The total is said out loud, and separately from the page. An operator who can see
    /// that the search found three thousand entries knows the one they want is in there
    /// somewhere; a window that shows five hundred rows and says nothing else invites them
    /// to conclude the patient is not scheduled.
    /// </remarks>
    public string PageSummary
    {
        get
        {
            if (TotalCount == 0)
            {
                return string.Empty;
            }

            (int first, int last) = WorklistPaging.PageWindow(PageIndex, TotalCount, PageSize);

            return string.Format(
                Localize("Worklist.PageSummary"),
                PageNumber, PageCount, first, last, TotalCount);
        }
    }

    // ------------------------------------------------------------------ The dates

    /// <summary>
    /// The date ranges offered for this query.
    /// </summary>
    /// <remarks>
    /// Offered here as well as in Settings because the two answer different questions.
    /// Settings decides what this workstation does every morning; this decides what one
    /// search looks at, which is how somebody finds an examination from last month without
    /// the settings password and without changing anything for tomorrow.
    /// </remarks>
    public IReadOnlyList<EnumOption<WorklistDateRange>> DateRanges { get; } =
    [
        new(WorklistDateRange.Today, "Settings.Worklist.Range.Today"),
        new(WorklistDateRange.YesterdayAndToday, "Settings.Worklist.Range.YesterdayAndToday"),
        new(WorklistDateRange.LastSevenDays, "Settings.Worklist.Range.LastSevenDays"),
        new(WorklistDateRange.AnyDate, "Settings.Worklist.Range.AnyDate"),
        new(WorklistDateRange.Explicit, "Settings.Worklist.Range.Explicit")
    ];

    /// <summary>The range this search will use.</summary>
    public WorklistDateRange SelectedDateRange
    {
        get => _selectedDateRange;
        set
        {
            if (SetProperty(ref _selectedDateRange, value))
            {
                OnPropertyChanged(nameof(ShowsExplicitDates));
            }
        }
    }

    /// <summary>First date of an explicit range.</summary>
    public DateTime? ExplicitFrom
    {
        get => _explicitFrom;
        set => SetProperty(ref _explicitFrom, value);
    }

    /// <summary>Last date of an explicit range.</summary>
    public DateTime? ExplicitTo
    {
        get => _explicitTo;
        set => SetProperty(ref _explicitTo, value);
    }

    /// <summary>Whether the two date pickers apply to the current choice.</summary>
    public bool ShowsExplicitDates => SelectedDateRange == WorklistDateRange.Explicit;

    /// <summary>
    /// The window to ask one server for, or null to use whatever that server has saved.
    /// </summary>
    /// <remarks>
    /// Null when the operator has not touched the control and it still shows what the server
    /// is configured with. Sending an override that merely repeats the saved setting would
    /// work and would make the log read as though somebody had chosen it, which is the sort
    /// of small untruth that wastes an afternoon later.
    /// </remarks>
    private WorklistDateWindow? SelectedDateWindow(WorklistConfiguration server) =>
        SelectedDateRange == server.DateRange
            && (SelectedDateRange != WorklistDateRange.Explicit
                || (ExplicitFrom == server.ExplicitFrom && ExplicitTo == server.ExplicitTo))
            ? null
            : new WorklistDateWindow(SelectedDateRange, ExplicitFrom, ExplicitTo);

    /// <summary>
    /// Points the date control at what the chosen server is configured with.
    /// </summary>
    /// <remarks>
    /// So the window opens showing the truth. Defaulting to "today" regardless would be
    /// wrong for a site that has configured something else, and defaulting to "any date"
    /// would quietly undo a deliberate setting on every search.
    /// </remarks>
    private void AdoptServerDates(WorklistConfiguration? server)
    {
        if (server is null)
        {
            return;
        }

        SelectedDateRange = server.DateRange;
        ExplicitFrom = server.ExplicitFrom;
        ExplicitTo = server.ExplicitTo;
    }

    /// <summary>The entry the operator picked, or null while nothing is selected.</summary>
    public WorklistEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionPreview));
            }
        }
    }

    /// <summary>True when an entry is selected and can be used.</summary>
    public bool HasSelection => SelectedEntry is not null;

    /// <summary>Optional Patient ID filter.</summary>
    public string PatientIdFilter
    {
        get => _patientIdFilter;
        set => SetProperty(ref _patientIdFilter, value);
    }

    /// <summary>Optional surname filter.</summary>
    public string SurnameFilter
    {
        get => _surnameFilter;
        set => SetProperty(ref _surnameFilter, value);
    }

    /// <summary>True while a query is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ClearFiltersCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Inverse of <see cref="IsBusy"/>, for binding.</summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>Operator facing summary of the last query.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// What will actually be put into the form, including any shortening.
    /// </summary>
    /// <remarks>
    /// Shown deliberately. The department asked for shortening to happen without
    /// interrupting anyone, and this respects that: nothing blocks and nothing pops up.
    /// But the operator has the selected entry in front of them, and showing the name that
    /// will be sent costs nothing and means the difference is never a surprise later.
    /// </remarks>
    public string SelectionPreview
    {
        get
        {
            if (SelectedEntry is null)
            {
                return string.Empty;
            }

            ShortenedPersonName fitted = PersonNameShortener.Fit(
                SelectedEntry.FamilyName, SelectedEntry.GivenName);

            string composed = string.IsNullOrEmpty(fitted.GivenName)
                ? fitted.FamilyName
                : $"{fitted.FamilyName}, {fitted.GivenName}";

            return fitted.WasShortened
                ? $"{composed}   ({Localize("Worklist.Shortened")})"
                : composed;
        }
    }

    /// <summary>Runs the query.</summary>
    public System.Windows.Input.ICommand SearchCommand { get; }

    /// <summary>Clears the filters and searches again.</summary>
    public System.Windows.Input.ICommand ClearFiltersCommand { get; }

    /// <summary>
    /// Turns the selected entry into patient information for the main form.
    /// </summary>
    /// <remarks>
    /// The middle name is deliberately not carried across. The department does not use it,
    /// and leaving it out means it cannot consume part of the sixty four characters a
    /// Person Name is allowed, which would otherwise shorten the given name further for no
    /// benefit.
    /// </remarks>
    public PatientDemographics? BuildDemographics()
    {
        if (SelectedEntry is null)
        {
            return null;
        }

        ShortenedPersonName fitted = PersonNameShortener.Fit(
            SelectedEntry.FamilyName, SelectedEntry.GivenName);

        if (fitted.WasShortened)
        {
            // Silent to the operator, as asked, but never silent to the record. A later
            // question about why a name differs from the worklist has an answer here.
            _services.Logger.Information(
                $"The worklist name for patient '{SelectedEntry.PatientId}' was " +
                $"{fitted.OriginalLength} characters, over the " +
                $"{Core.Validation.DicomValueRules.MaxPersonNameGroupLength} a DICOM Person Name allows. " +
                $"The surname was kept whole and the given name shortened to '{fitted.GivenName}'.");
        }

        return new PatientDemographics
        {
            PatientId = SelectedEntry.PatientId,
            FamilyName = fitted.FamilyName,
            GivenName = fitted.GivenName,
            MiddleName = string.Empty,
            DateOfBirth = SelectedEntry.DateOfBirth,
            Sex = SelectedEntry.Sex,
            AccessionNumber = SelectedEntry.AccessionNumber,
            ReferringPhysicianName = SelectedEntry.ReferringPhysicianName,
            StudyDescription = SelectedEntry.RequestedProcedureDescription,

            // The order already created a study. Filing the document under it is what puts
            // the report beside the images rather than in a study of its own.
            ExistingStudyInstanceUid = SelectedEntry.StudyInstanceUid
        };
    }

    // ------------------------------------------------------------------ Internals

    private async Task SearchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = Localize("Worklist.Searching");
        _fetched.Clear();
        Entries.Clear();
        SelectedEntry = null;

        try
        {
            AppConfiguration configuration = _services.ConfigurationService.Current;
            IReadOnlyList<WorklistConfiguration> servers = ServersToQuery(configuration);

            if (servers.Count == 0)
            {
                Status = Localize("Worklist.NoServers");
                return;
            }

            bool several = servers.Count > 1;
            var messages = new List<string>();
            bool truncated = false;

            foreach (WorklistConfiguration server in servers)
            {
                // Forgiving, because the boxes say "contains" and because a worklist
                // holding Gonçalves has to be findable by somebody typing Goncalves. The
                // text goes across as typed and the service decides how to match it; it
                // used to be wrapped here, which left the window and the command line
                // meaning different things by the same argument.
                WorklistQueryResult result = await _services.Worklist.QueryAsync(
                    server,
                    configuration.CallingAeTitle,
                    PatientIdFilter,
                    SurnameFilter,
                    SelectedDateWindow(server),
                    WorklistMatching.Forgiving).ConfigureAwait(true);

                foreach (WorklistEntry entry in result.Entries)
                {
                    // Stamped only when several servers were asked. With one, the column is
                    // hidden and the text would be noise.
                    _fetched.Add(several
                        ? entry with { SourceWorklistName = server.DisplayName }
                        : entry);
                }

                truncated |= result.WasTruncated;

                // Each server reports for itself, named, so one that is unreachable is
                // visibly one server rather than "the worklist is down". A department
                // whose own server answered should not be told otherwise.
                messages.Add(several ? $"{server.DisplayName}: {result.Message}" : result.Message);
            }

            // The page size follows the servers that were actually asked, so it is settled
            // before the first page is filled.
            PageSize = servers.Max(s => s.MaximumResults);

            // Asked after every server has answered, so that entries from two departments
            // are interleaved by date rather than appearing as one list after another. The
            // scheduled date is what an operator reads down the column.
            _fetched.Sort(ByScheduledDateThenName);
            RefreshPage();

            Status = truncated
                ? $"{string.Join("  ", messages)} {Localize("Worklist.Truncated")}"
                : string.Join("  ", messages);

            // One result is almost always the one wanted, and selecting it saves a click
            // at a busy desk. It is the whole of the "type a patient number and get the
            // patient" flow: one match selects itself and the form can be filled at once.
            //
            // With one exception, added when the boxes started matching on part of what was
            // typed. A partial number now has one answer more often, and that answer is not
            // always the patient meant: typing 123 can return the only patient whose number
            // contains it, who may be 99123. Where a number was typed and the single result
            // is not that number, the row is left for the operator to choose on purpose.
            // Typing the whole number, which is what the hint asks for, still selects itself.
            //
            // Counted over everything fetched rather than over the page, because "one
            // result" has to mean one result. A page holding a single row while another
            // nineteen pages wait behind it is not one result, and selecting that row would
            // fill the form from an entry chosen by arithmetic.
            if (_fetched.Count == 1 && IsSafeToSelectAutomatically(_fetched[0]))
            {
                SelectedEntry = _fetched[0];
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("The worklist search failed.", ex);
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Scheduled date first, then family name, which is the order the list is read in.
    /// </summary>
    /// <remarks>
    /// Entries with no scheduled date sort last rather than first. A worklist that sends no
    /// date for a step is saying it does not know when it is, and a row with nothing in the
    /// column it is being sorted by belongs at the end rather than above everything a
    /// department has scheduled.
    /// </remarks>
    private static int ByScheduledDateThenName(WorklistEntry left, WorklistEntry right)
    {
        int byDate = (left.ScheduledStart ?? DateTime.MaxValue)
            .CompareTo(right.ScheduledStart ?? DateTime.MaxValue);

        return byDate != 0
            ? byDate
            : string.Compare(
                left.FamilyName, right.FamilyName, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Shows a page, clamped to the pages that exist.</summary>
    /// <remarks>
    /// Clamped rather than guarded, so that a page number arriving from anywhere, a command
    /// enabled a moment too long, a search that returned fewer entries than the last one,
    /// cannot leave the window showing nothing with no way back.
    /// </remarks>
    private void GoToPage(int index)
    {
        PageIndex = WorklistPaging.ClampPageIndex(index, TotalCount, PageSize);
        FillPage();
    }

    /// <summary>
    /// Rebuilds the paging after the fetched entries changed, keeping the first page.
    /// </summary>
    private void RefreshPage()
    {
        PageIndex = 0;
        FillPage();
    }

    /// <summary>
    /// Puts the current page's entries into the bound collection.
    /// </summary>
    /// <remarks>
    /// The collection is replaced in place rather than being swapped for a new one, because
    /// the grid is bound to this instance and a new collection would silently stop updating
    /// it.
    /// </remarks>
    private void FillPage()
    {
        Entries.Clear();

        foreach (WorklistEntry entry in _fetched
            .Skip(WorklistPaging.Skipped(PageIndex, TotalCount, PageSize))
            .Take(PageSize))
        {
            Entries.Add(entry);
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageNumber));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(HasSeveralPages));
        OnPropertyChanged(nameof(CanSortColumns));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));

        (FirstPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviousPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LastPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ClearFilters()
    {
        PatientIdFilter = string.Empty;
        SurnameFilter = string.Empty;
        _ = SearchAsync();
    }

    /// <summary>
    /// The servers the next query will ask.
    /// </summary>
    /// <remarks>
    /// Resolved against the live configuration each time rather than held from the
    /// constructor, so a server disabled in Settings while this window is open is not
    /// queried by a list that no longer reflects what is configured.
    /// </remarks>
    private IReadOnlyList<WorklistConfiguration> ServersToQuery(AppConfiguration configuration)
    {
        IReadOnlyList<WorklistConfiguration> enabled = configuration.EnabledWorklists;

        if (SelectedServer is null || SelectedServer.IsAll)
        {
            return enabled;
        }

        return enabled
            .Where(w => string.Equals(w.Id, SelectedServer.Server!.Id, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Records which server was chosen, so the next window opens on it.
    /// </summary>
    /// <remarks>
    /// Saved at once rather than when the window closes, because the choice is worth
    /// keeping whether or not the operator goes on to pick a patient. "All" is not
    /// recorded: it is a way of finding out where a patient is, not a server to return to.
    /// </remarks>
    /// <summary>
    /// Whether the single result can be chosen for the operator rather than by them.
    /// </summary>
    private bool IsSafeToSelectAutomatically(WorklistEntry entry)
    {
        string typed = PatientIdFilter.Trim();

        if (typed.Length == 0)
        {
            // No number was typed, so nothing was claimed about which patient this is and
            // the operator is reading the row either way.
            return true;
        }

        return string.Equals(entry.PatientId?.Trim(), typed, StringComparison.OrdinalIgnoreCase);
    }

    private void RememberChoice()
    {
        if (SelectedServer?.Server is null)
        {
            return;
        }

        AppConfiguration configuration = _services.ConfigurationService.Current;

        if (string.Equals(configuration.LastUsedWorklistId, SelectedServer.Server.Id, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            configuration.LastUsedWorklistId = SelectedServer.Server.Id;
            _services.ConfigurationService.Save(configuration);
        }
        catch (Exception ex)
        {
            // A preference that could not be written must not break a clinical search.
            _services.Logger.Warning($"The chosen worklist server could not be remembered: {ex.Message}");
        }
    }

    private string Localize(string key) => _services.Localizer.Get(key);
}

/// <summary>
/// One entry in the worklist server picker: a configured server, or all of them.
/// </summary>
/// <remarks>
/// "All" exists because of how a patient number is looked up. With several departments
/// each holding their own worklist, somebody at a desk with a document in their hand does
/// not necessarily know which department scheduled the examination, and making them guess
/// turns one search into four.
/// </remarks>
public sealed class WorklistChoice
{
    /// <summary>Creates a choice for one configured server, or for all of them.</summary>
    /// <param name="server">The server, or null to mean every enabled server.</param>
    /// <param name="display">What the picker shows.</param>
    public WorklistChoice(WorklistConfiguration? server, string display)
    {
        Server = server;
        Display = display;
    }

    /// <summary>Creates the choice that queries every enabled server.</summary>
    public static WorklistChoice All(string display) => new(null, display);

    /// <summary>The server, or null for the "all" choice.</summary>
    public WorklistConfiguration? Server { get; }

    /// <summary>What the picker shows.</summary>
    public string Display { get; }

    /// <summary>True for the choice that queries every enabled server.</summary>
    public bool IsAll => Server is null;
}
