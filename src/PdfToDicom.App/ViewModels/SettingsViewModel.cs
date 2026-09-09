using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfToDicom.App.Infrastructure;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.App.ViewModels;

/// <summary>
/// View model for the settings dialog.
/// </summary>
/// <remarks>
/// The dialog edits a copy of the configuration. Nothing is written until the operator
/// presses Save, so cancelling genuinely discards every change, including edits made to
/// individual destinations.
/// </remarks>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ApplicationServices _services;
    private readonly AppConfiguration _original;
    private readonly PacsDestinationValidator _destinationValidator = new();

    private PacsDestination? _selectedDestination;
    private string _testResult = string.Empty;
    private bool _isTesting;

    /// <summary>Creates the view model over a working copy of the current configuration.</summary>
    public SettingsViewModel(ApplicationServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _original = services.ConfigurationService.Current;

        Working = CloneConfiguration(_original);

        Destinations = new ObservableCollection<PacsDestination>(Working.Destinations);
        SelectedDestination = Destinations.FirstOrDefault();

        Languages = services.Localizer.AvailableLanguages;
        SelectedLanguage = Languages.FirstOrDefault(l =>
            string.Equals(l.CultureName, Working.Language, StringComparison.OrdinalIgnoreCase))
            ?? Languages.FirstOrDefault();

        AddDestinationCommand = new RelayCommand(AddDestination);
        RemoveDestinationCommand = new RelayCommand(RemoveDestination, () => SelectedDestination is not null);
        TestDestinationCommand = new AsyncRelayCommand(TestDestinationAsync, () => SelectedDestination is not null && !IsTesting);
        BrowseLogFolderCommand = new RelayCommand(() => BrowseForFolder(f => Working.LogFolder = f, Working.LogFolder));
        BrowseOutputFolderCommand = new RelayCommand(() => BrowseForFolder(f => Working.OutputFolder = f, Working.OutputFolder));
        BrowsePdfFolderCommand = new RelayCommand(() => BrowseForFolder(f => Working.DefaultPdfFolder = f, Working.DefaultPdfFolder));
        BrowseMonitoredFolderCommand = new RelayCommand(() => BrowseForFolder(f => Working.MonitoredFolder = f, Working.MonitoredFolder));
    }

    /// <summary>The working copy being edited.</summary>
    public AppConfiguration Working { get; }

    /// <summary>Destinations being edited.</summary>
    public ObservableCollection<PacsDestination> Destinations { get; }

    /// <summary>Languages available in the interface.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; }

    /// <summary>Language selected in the dialog.</summary>
    public LanguageOption? SelectedLanguage { get; set; }

    /// <summary>Character set options offered to the administrator.</summary>
    public IReadOnlyList<EnumOption<DicomCharacterSet>> CharacterSets { get; } = new[]
    {
        new EnumOption<DicomCharacterSet>(DicomCharacterSet.Utf8, "Settings.CharacterSet.Utf8"),
        new EnumOption<DicomCharacterSet>(DicomCharacterSet.Latin1, "Settings.CharacterSet.Latin1")
    };

    /// <summary>UID strategy options.</summary>
    public IReadOnlyList<EnumOption<UidGenerationStrategy>> UidStrategies { get; } = new[]
    {
        new EnumOption<UidGenerationStrategy>(UidGenerationStrategy.UuidDerived, "Settings.UidStrategy.Uuid"),
        new EnumOption<UidGenerationStrategy>(UidGenerationStrategy.RegisteredRoot, "Settings.UidStrategy.Root")
    };

    /// <summary>
    /// Modality values offered. "OT" is the default because it is universally accepted;
    /// "DOC" is the value defined by the current standard for encapsulated documents.
    /// </summary>
    public IReadOnlyList<string> ModalityOptions { get; } = new[] { "OT", "DOC" };

    /// <summary>
    /// Conversion Type values from the defined terms of PS3.3 C.8.6.1.1: workstation,
    /// scanned document, scanned image, digitised film and digital interface.
    /// </summary>
    public IReadOnlyList<string> ConversionTypeOptions { get; } = new[] { "WSD", "SD", "SI", "DF", "DI", "DRW", "SYN" };

    /// <summary>The destination currently selected in the list.</summary>
    public PacsDestination? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (SetProperty(ref _selectedDestination, value))
            {
                OnPropertyChanged(nameof(HasSelectedDestination));
                RemoveDestinationCommand?.RaiseCanExecuteChanged();
                TestDestinationCommand?.RaiseCanExecuteChanged();
                TestResult = string.Empty;
            }
        }
    }

    /// <summary>True when a destination is selected.</summary>
    public bool HasSelectedDestination => SelectedDestination is not null;

    /// <summary>Outcome of the most recent connection test.</summary>
    public string TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }

    /// <summary>True while a connection test is running.</summary>
    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                TestDestinationCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Adds a new destination with sensible starting values.</summary>
    public RelayCommand AddDestinationCommand { get; }

    /// <summary>Removes the selected destination.</summary>
    public RelayCommand RemoveDestinationCommand { get; }

    /// <summary>Sends a verification request to the selected destination.</summary>
    public AsyncRelayCommand TestDestinationCommand { get; }

    /// <summary>Chooses the log folder.</summary>
    public RelayCommand BrowseLogFolderCommand { get; }

    /// <summary>Chooses the output folder.</summary>
    public RelayCommand BrowseOutputFolderCommand { get; }

    /// <summary>Chooses the default PDF folder.</summary>
    public RelayCommand BrowsePdfFolderCommand { get; }

    /// <summary>Chooses the monitored folder.</summary>
    public RelayCommand BrowseMonitoredFolderCommand { get; }

    /// <summary>
    /// Validates and persists the working copy.
    /// </summary>
    /// <returns>True when the configuration was saved.</returns>
    public bool Save()
    {
        Working.Destinations = Destinations.ToList();

        if (SelectedLanguage is not null)
        {
            Working.Language = SelectedLanguage.CultureName;
        }

        ValidationResult validation = _destinationValidator.ValidateAll(Working);

        // Only the network configuration is blocking. A site is entitled to save settings
        // with no destination yet configured while it is still being commissioned, so the
        // "no destination" finding is downgraded to a warning here.
        var blocking = validation.Errors
            .Where(e => !e.Message.Contains("No enabled destination", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (blocking.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, blocking.Select(e => "• " + e.Message)),
                _services.Localizer.Get("Validation.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        if (Working.UidStrategy == UidGenerationStrategy.RegisteredRoot &&
            !DicomValueRules.IsValidUid(Working.OrganizationUidRoot, out string rootError))
        {
            MessageBox.Show(
                $"{_services.Localizer.Get("Settings.OrganizationUidRoot")}: {rootError}",
                _services.Localizer.Get("Validation.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        EnsureFoldersExist();

        _services.ConfigurationService.Save(Working);
        _services.Localizer.SetLanguage(Working.Language);
        ThemeManager.Apply(Working.DarkMode);

        _services.AuditLogger.ConfigurationChanged(DescribeChanges());
        _services.Logger.Information("The configuration was updated by the operator.");

        return true;
    }

    /// <summary>
    /// Creates the configured folders so that a mistyped path is reported now, in the
    /// dialog, rather than at the moment a clinical document needs to be written.
    /// </summary>
    private void EnsureFoldersExist()
    {
        foreach (string folder in new[] { Working.LogFolder, Working.OutputFolder })
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                _services.Logger.Warning($"The configured folder '{folder}' could not be created: {ex.Message}");
            }
        }
    }

    private void AddDestination()
    {
        var destination = new PacsDestination
        {
            Name = $"PACS {Destinations.Count + 1}",
            CalledAeTitle = "PACS",
            Host = "127.0.0.1",
            Port = 104
        };

        Destinations.Add(destination);
        SelectedDestination = destination;
    }

    private void RemoveDestination()
    {
        if (SelectedDestination is null)
        {
            return;
        }

        if (MessageBox.Show(
                $"Remove the destination '{SelectedDestination.Name}'?",
                _services.Localizer.Get("Settings.RemoveDestination"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        Destinations.Remove(SelectedDestination);
        SelectedDestination = Destinations.FirstOrDefault();
    }

    private async Task TestDestinationAsync()
    {
        if (SelectedDestination is null)
        {
            return;
        }

        ValidationResult validation = _destinationValidator.Validate(SelectedDestination, Working.CallingAeTitle);

        if (!validation.IsValid)
        {
            TestResult = validation.ToDisplayString();
            return;
        }

        IsTesting = true;
        TestResult = _services.Localizer.Format("Status.Verifying", SelectedDestination.Name);

        try
        {
            TransmissionResult result = await _services.NetworkService.VerifyAsync(
                SelectedDestination, Working.CallingAeTitle);

            TestResult = $"{(result.IsSuccess ? "OK" : "FAIL")} - {result.Message} " +
                         $"({(long)result.Duration.TotalMilliseconds} ms)";
        }
        catch (Exception ex)
        {
            TestResult = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// Prompts for a folder using the common file dialog in folder selection mode, which
    /// is available on Windows 10 and later without any additional component.
    /// </summary>
    private void BrowseForFolder(Action<string> assign, string currentValue)
    {
        var dialog = new OpenFolderDialog
        {
            Title = _services.Localizer.Get("Settings.Browse"),
            Multiselect = false
        };

        if (Directory.Exists(currentValue))
        {
            dialog.InitialDirectory = currentValue;
        }

        if (dialog.ShowDialog() == true)
        {
            assign(dialog.FolderName);
            OnPropertyChanged(nameof(Working));
        }
    }

    /// <summary>Produces a short description of what changed, for the audit trail.</summary>
    private string DescribeChanges()
    {
        var changes = new List<string>();

        void Compare(string name, object? before, object? after)
        {
            if (!Equals(before, after))
            {
                changes.Add($"{name}: '{before}' -> '{after}'");
            }
        }

        Compare("InstitutionName", _original.InstitutionName, Working.InstitutionName);
        Compare("CallingAeTitle", _original.CallingAeTitle, Working.CallingAeTitle);
        Compare("CharacterSet", _original.CharacterSet, Working.CharacterSet);
        Compare("Modality", _original.Modality, Working.Modality);
        Compare("UidStrategy", _original.UidStrategy, Working.UidStrategy);
        Compare("OutputFolder", _original.OutputFolder, Working.OutputFolder);
        Compare("LogFolder", _original.LogFolder, Working.LogFolder);
        Compare("AutoDelete", _original.AutoDeleteAfterSuccessfulTransmission, Working.AutoDeleteAfterSuccessfulTransmission);
        Compare("FolderMonitoring", _original.EnableFolderMonitoring, Working.EnableFolderMonitoring);
        Compare("Language", _original.Language, Working.Language);
        Compare("DestinationCount", _original.Destinations.Count, Working.Destinations.Count);

        return changes.Count == 0 ? "No effective change." : string.Join("; ", changes);
    }

    /// <summary>
    /// Produces a deep copy so that cancelling the dialog leaves the live configuration
    /// untouched, including the nested destination and template collections.
    /// </summary>
    private static AppConfiguration CloneConfiguration(AppConfiguration source) => new()
    {
        ConfigurationVersion = source.ConfigurationVersion,
        HospitalName = source.HospitalName,
        InstitutionName = source.InstitutionName,
        DepartmentName = source.DepartmentName,
        StationName = source.StationName,
        CallingAeTitle = source.CallingAeTitle,
        Destinations = source.Destinations.Select(d => d.Clone()).ToList(),
        RecentDestinationLimit = source.RecentDestinationLimit,
        CharacterSet = source.CharacterSet,
        UidStrategy = source.UidStrategy,
        OrganizationUidRoot = source.OrganizationUidRoot,
        Modality = source.Modality,
        ConversionType = source.ConversionType,
        Manufacturer = source.Manufacturer,
        BurnedInAnnotation = source.BurnedInAnnotation,
        DefaultDocumentTitle = source.DefaultDocumentTitle,
        DefaultStudyDescription = source.DefaultStudyDescription,
        DefaultSeriesDescription = source.DefaultSeriesDescription,
        SeriesNumber = source.SeriesNumber,
        InstanceNumber = source.InstanceNumber,
        RecordPageCountInPrivateTag = source.RecordPageCountInPrivateTag,
        IncludeConceptNameCodeSequence = source.IncludeConceptNameCodeSequence,
        DefaultPdfFolder = source.DefaultPdfFolder,
        OutputFolder = source.OutputFolder,
        LogFolder = source.LogFolder,
        AutoDeleteAfterSuccessfulTransmission = source.AutoDeleteAfterSuccessfulTransmission,
        AlwaysSaveDicomLocally = source.AlwaysSaveDicomLocally,
        LogRetentionDays = source.LogRetentionDays,
        EnableFolderMonitoring = source.EnableFolderMonitoring,
        MonitoredFolder = source.MonitoredFolder,
        MonitorSettleDelayMilliseconds = source.MonitorSettleDelayMilliseconds,
        DarkMode = source.DarkMode,
        Language = source.Language,
        ShowAdvancedFields = source.ShowAdvancedFields,
        ConfirmBeforeSending = source.ConfirmBeforeSending,
        Templates = source.Templates.Select(t => new PatientTemplate
        {
            Name = t.Name,
            ReferringPhysicianName = t.ReferringPhysicianName,
            StudyDescription = t.StudyDescription,
            SeriesDescription = t.SeriesDescription,
            DocumentTitle = t.DocumentTitle
        }).ToList()
    };
}

/// <summary>An enumeration value paired with a localized label.</summary>
/// <typeparam name="T">The enumeration type.</typeparam>
/// <param name="Value">The underlying value.</param>
/// <param name="ResourceKey">Localization key for the label.</param>
public sealed record EnumOption<T>(T Value, string ResourceKey) where T : struct, Enum
{
    /// <summary>Label shown in the selector, resolved in the active language.</summary>
    public string Display => LocalizationSource.Instance[ResourceKey];
}
