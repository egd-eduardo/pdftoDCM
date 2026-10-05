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

    /// <summary>Creates the view model.</summary>
    public WorklistViewModel(ApplicationServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        SearchCommand = new RelayCommand(async _ => await SearchAsync(), _ => !IsBusy);
        ClearFiltersCommand = new RelayCommand(_ => ClearFilters(), _ => !IsBusy);

        // The window is opened to find something, so the first search runs without being
        // asked for. A reception desk should see today's list immediately.
        _ = SearchAsync();
    }

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

            WorklistQueryResult result = await _services.Worklist.QueryAsync(
                configuration.Worklist,
                configuration.CallingAeTitle,
                PatientIdFilter,
                SurnameFilter).ConfigureAwait(true);

            foreach (WorklistEntry entry in result.Entries)
            {
                Entries.Add(entry);
            }

            Status = result.WasTruncated
                ? $"{result.Message} {Localize("Worklist.Truncated")}"
                : result.Message;

            // One result is almost always the one wanted, and selecting it saves a click
            // at a busy desk.
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

    private string Localize(string key) => _services.Localizer.Get(key);
}
