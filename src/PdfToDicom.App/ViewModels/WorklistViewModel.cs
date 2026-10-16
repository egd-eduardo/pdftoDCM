using System.Collections.ObjectModel;
using System.Windows;
using PdfToDicom.App.Infrastructure;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Models;
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

    /// <summary>Entries currently shown.</summary>
    public ObservableCollection<WorklistEntry> Entries { get; } = new();

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
                WorklistQueryResult result = await _services.Worklist.QueryAsync(
                    server,
                    configuration.CallingAeTitle,
                    PatientIdFilter,
                    SurnameFilter).ConfigureAwait(true);

                foreach (WorklistEntry entry in result.Entries)
                {
                    // Stamped only when several servers were asked. With one, the column is
                    // hidden and the text would be noise.
                    Entries.Add(several
                        ? entry with { SourceWorklistName = server.DisplayName }
                        : entry);
                }

                truncated |= result.WasTruncated;

                // Each server reports for itself, named, so one that is unreachable is
                // visibly one server rather than "the worklist is down". A department
                // whose own server answered should not be told otherwise.
                messages.Add(several ? $"{server.DisplayName}: {result.Message}" : result.Message);
            }

            Status = truncated
                ? $"{string.Join("  ", messages)} {Localize("Worklist.Truncated")}"
                : string.Join("  ", messages);

            // One result is almost always the one wanted, and selecting it saves a click
            // at a busy desk. It is the whole of the "type a patient number and get the
            // patient" flow: one match selects itself and the form can be filled at once.
            if (Entries.Count == 1)
            {
                SelectedEntry = Entries[0];
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
