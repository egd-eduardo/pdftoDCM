using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfToDicom.App.Infrastructure;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Importing;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Core.Validation;
using PdfToDicom.Core.Security;

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
    private string _filenameTestName = string.Empty;
    private string _filenameTestResult = string.Empty;

    /// <summary>Creates the view model over a working copy of the current configuration.</summary>
    public SettingsViewModel(ApplicationServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _original = services.ConfigurationService.Current;

        Working = _original.Clone();

        // Commands first, before any property is assigned. Several of the setters below
        // refresh a command's enabled state, so a command that does not exist yet is a
        // crash in this constructor and a dialog that will not open. The setters guard
        // against it as well; this ordering means they do not have to be the only thing
        // that does.
        AddDestinationCommand = new RelayCommand(AddDestination);
        RemoveDestinationCommand = new RelayCommand(RemoveDestination, () => SelectedDestination is not null);
        TestDestinationCommand = new AsyncRelayCommand(TestDestinationAsync, () => SelectedDestination is not null && !IsTesting);
        TestWorklistCommand = new AsyncRelayCommand(TestWorklistAsync, () => SelectedWorklist is not null);
        AddWorklistCommand = new RelayCommand(AddWorklist);
        RemoveWorklistCommand = new RelayCommand(RemoveWorklist, () => SelectedWorklist is not null);
        TestFilenameCommand = new RelayCommand(TestFilename);
        BrowseLogFolderCommand = new RelayCommand(() => BrowseForFolder(f => Working.LogFolder = f, Working.LogFolder));
        BrowseOutputFolderCommand = new RelayCommand(() => BrowseForFolder(f => Working.OutputFolder = f, Working.OutputFolder));
        BrowsePdfFolderCommand = new RelayCommand(() => BrowseForFolder(f => Working.DefaultPdfFolder = f, Working.DefaultPdfFolder));
        BrowseMonitoredFolderCommand = new RelayCommand(() => BrowseForFolder(f => Working.MonitoredFolder = f, Working.MonitoredFolder));

        Destinations = new ObservableCollection<PacsDestination>(Working.Destinations);
        SelectedDestination = Destinations.FirstOrDefault();

        Worklists = new ObservableCollection<WorklistConfiguration>(Working.Worklists ?? new List<WorklistConfiguration>());
        SelectedWorklist = Worklists.FirstOrDefault();

        Languages = services.Localizer.AvailableLanguages;
        SelectedLanguage = Languages.FirstOrDefault(l =>
            string.Equals(l.CultureName, Working.Language, StringComparison.OrdinalIgnoreCase))
            ?? Languages.FirstOrDefault();
    }

    /// <summary>The working copy being edited.</summary>
    public AppConfiguration Working { get; }

    /// <summary>Destinations being edited.</summary>
    public ObservableCollection<PacsDestination> Destinations { get; }

    /// <summary>Worklist servers being edited.</summary>
    public ObservableCollection<WorklistConfiguration> Worklists { get; }

    /// <summary>The worklist server whose settings are shown, or null when none is selected.</summary>
    public WorklistConfiguration? SelectedWorklist
    {
        get => _selectedWorklist;
        set
        {
            if (SetProperty(ref _selectedWorklist, value))
            {
                OnPropertyChanged(nameof(HasSelectedWorklist));

                // The last server's result would otherwise sit under a different server's
                // settings and read as though it belonged to them.
                WorklistTestResult = string.Empty;

                // Null conditional because this setter runs from the constructor, before
                // the commands exist. Without the question marks, opening Settings threw a
                // NullReferenceException for anybody who had a worklist server configured,
                // and the dialog would not open at all. SelectedDestination below has
                // carried the same guard, for the same reason, since it was written.
                TestWorklistCommand?.RaiseCanExecuteChanged();
                RemoveWorklistCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when a worklist server is selected, so its settings can be shown.</summary>
    public bool HasSelectedWorklist => SelectedWorklist is not null;

    /// <summary>Adds a worklist server.</summary>
    public RelayCommand AddWorklistCommand { get; }

    /// <summary>Removes the selected worklist server.</summary>
    public RelayCommand RemoveWorklistCommand { get; }

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
    /// <summary>
    /// The publisher's registered UID root as it is shown in the dialog: partly masked,
    /// and read only because it is fixed in the software.
    /// </summary>
    /// <remarks>
    /// The masking discourages the prefix being copied off a screen. It does not make it
    /// secret: every DICOM object the application produces carries UIDs built from it.
    /// </remarks>
    public string RegisteredUidRootForDisplay => UidGenerator.RegisteredUidRootForDisplay;

    // ---------------------------------------------------------------- Protected settings

    private bool _areProtectedSettingsUnlocked = !ProtectedSettingsLock.IsProtected;
    private string _lockMessage = string.Empty;
    private string _worklistTestResult = string.Empty;
    private WorklistConfiguration? _selectedWorklist;

    /// <summary>
    /// Whether the protected settings, the worklist servers and the destinations, are
    /// readable and editable in this dialog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One flag for both pages, because it is one password. Having to type it twice in the
    /// same dialog would teach nothing and protect nothing: whoever opened one page can
    /// open the other, and pretending otherwise would only be tedious.
    /// </para>
    /// <para>
    /// Starts unlocked when no password has been compiled in, because a lock nobody can
    /// open is not a protection, it is a defect. The dialog says plainly which of the two
    /// states it is in rather than implying a protection that is not there.
    /// </para>
    /// <para>
    /// It is not remembered between dialogs. Closing Settings locks both pages again, so
    /// an unattended workstation does not sit there unlocked.
    /// </para>
    /// </remarks>
    public bool AreProtectedSettingsUnlocked
    {
        get => _areProtectedSettingsUnlocked;
        private set
        {
            if (SetProperty(ref _areProtectedSettingsUnlocked, value))
            {
                OnPropertyChanged(nameof(AreProtectedSettingsLocked));
            }
        }
    }

    /// <summary>Inverse of <see cref="AreProtectedSettingsUnlocked"/>, for binding.</summary>
    public bool AreProtectedSettingsLocked => !AreProtectedSettingsUnlocked;

    /// <summary>True when a password was compiled into this build.</summary>
    public bool IsSettingsPasswordSet => ProtectedSettingsLock.IsProtected;

    /// <summary>Why the last unlock attempt failed, when it did.</summary>
    public string LockMessage
    {
        get => _lockMessage;
        private set => SetProperty(ref _lockMessage, value);
    }

    /// <summary>Result of the last worklist connection test.</summary>
    public string WorklistTestResult
    {
        get => _worklistTestResult;
        private set => SetProperty(ref _worklistTestResult, value);
    }

    // ---------------------------------------------------------------- Filename import

    /// <summary>
    /// The configured patterns as editable text, one per line.
    /// </summary>
    /// <remarks>
    /// A list of strings has no sensible editor in a dialog of this kind, and a grid for
    /// three short lines of text would be heavier than the thing it edits. One per line is
    /// how an administrator would write them down anyway. Blank lines are dropped, since a
    /// trailing newline is unavoidable when typing.
    /// </remarks>
    public string FilenamePatternsText
    {
        get => string.Join(Environment.NewLine, Working.FilenameImport.Patterns);
        set
        {
            Working.FilenameImport.Patterns = SplitLines(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The configured date formats as editable text, one per line. Empty means the built in
    /// list, which is what the caption beneath the box says.
    /// </summary>
    public string FilenameDateFormatsText
    {
        get => string.Join(Environment.NewLine, Working.FilenameImport.DateFormats);
        set
        {
            Working.FilenameImport.DateFormats = SplitLines(value);
            OnPropertyChanged();
        }
    }

    /// <summary>A file name the administrator is trying out.</summary>
    public string FilenameTestName
    {
        get => _filenameTestName;
        set => SetProperty(ref _filenameTestName, value);
    }

    /// <summary>What the patterns made of <see cref="FilenameTestName"/>.</summary>
    public string FilenameTestResult
    {
        get => _filenameTestResult;
        private set => SetProperty(ref _filenameTestResult, value);
    }

    /// <summary>Reads the test file name with the settings as they stand in the dialog.</summary>
    public RelayCommand TestFilenameCommand { get; }

    /// <summary>What may happen to a source file once it has been dealt with.</summary>
    public IReadOnlyList<EnumOption<ImportedFileDisposition>> ImportedFileDispositions { get; } = new[]
    {
        new EnumOption<ImportedFileDisposition>(
            ImportedFileDisposition.MoveToSubfolder, "Settings.Import.After.Move"),
        new EnumOption<ImportedFileDisposition>(
            ImportedFileDisposition.Rename, "Settings.Import.After.Rename"),
        new EnumOption<ImportedFileDisposition>(
            ImportedFileDisposition.Leave, "Settings.Import.After.Leave")
    };

    /// <summary>
    /// Tries the current settings against the name in the test box.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same work <c>pdftodicom check-names</c> does, in the dialog, because a naming
    /// layout agreed with another vendor is routinely slightly different in practice and
    /// this is where that should be discovered. It reads the settings as they are being
    /// edited, not as they were saved, so there is no need to save first.
    /// </para>
    /// <para>
    /// Nothing is converted, nothing is transmitted and no file has to exist. The name is
    /// text, and only text.
    /// </para>
    /// </remarks>
    private void TestFilename()
    {
        string name = FilenameTestName.Trim();

        if (name.Length == 0)
        {
            FilenameTestResult = Localized("Settings.Import.Test.Empty");
            return;
        }

        var reader = new FilenameMetadataReader(Working.FilenameImport);
        var lines = new List<string>();

        // Reported first and separately: a pattern the dialog rejected explains a name that
        // does not read, and leaving it unsaid is how somebody spends an afternoon on a
        // missing brace.
        foreach (string patternError in reader.PatternErrors)
        {
            lines.Add(patternError);
        }

        FilenameReadResult read = reader.Read(name);

        if (!read.IsMatch || read.Demographics is null)
        {
            lines.AddRange(read.Notes);
            FilenameTestResult = string.Join(Environment.NewLine, lines);
            return;
        }

        PatientDemographics d = read.Demographics;

        lines.Add(_services.Localizer.Format("Settings.Import.Test.Matched", read.Pattern));
        lines.Add(Pair("Step2.PatientId", d.PatientId));
        lines.Add(Pair("Step2.FamilyName", d.FamilyName));
        lines.Add(Pair("Step2.GivenName", d.GivenName));
        lines.Add(Pair("Step2.AccessionNumber", d.AccessionNumber));
        lines.Add(Pair(
            "Step2.DateOfBirth",
            d.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty));
        lines.Add(Pair("Step2.Sex", d.Sex.ToDicomValue()));

        if (d.StudyDescription.Length > 0)
        {
            lines.Add(Pair("Step2.StudyDescription", d.StudyDescription));
        }

        lines.AddRange(read.Notes);

        FilenameTestResult = string.Join(Environment.NewLine, lines);
    }

    /// <summary>One "label: value" line, with an empty value shown as empty rather than lost.</summary>
    private string Pair(string labelKey, string value) =>
        $"{Localized(labelKey)}: {(value.Length > 0 ? value : Localized("Settings.Import.Test.Blank"))}";

    private static string Localized(string key) => LocalizationSource.Instance[key];

    /// <summary>
    /// Non blank lines, trimmed. Shared by the pattern and date format boxes.
    /// </summary>
    private static List<string> SplitLines(string? text) =>
        (text ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .ToList();

    /// <summary>The date interval choices offered for a worklist query.</summary>
    public IReadOnlyList<EnumOption<WorklistDateRange>> WorklistDateRanges { get; } = new[]
    {
        new EnumOption<WorklistDateRange>(WorklistDateRange.Today, "Settings.Worklist.Range.Today"),
        new EnumOption<WorklistDateRange>(WorklistDateRange.YesterdayAndToday, "Settings.Worklist.Range.YesterdayAndToday"),
        new EnumOption<WorklistDateRange>(WorklistDateRange.LastSevenDays, "Settings.Worklist.Range.LastSevenDays"),
        new EnumOption<WorklistDateRange>(WorklistDateRange.AnyDate, "Settings.Worklist.Range.AnyDate"),
        new EnumOption<WorklistDateRange>(WorklistDateRange.Explicit, "Settings.Worklist.Range.Explicit")
    };


    /// <summary>Confirms the worklist server answers.</summary>
    /// <remarks>
    /// Asynchronous rather than fire and forget, so the button disables itself while the
    /// association is being attempted. A worklist that is simply not there takes the full
    /// timeout to say so, and a button that stays live for thirty seconds invites a second
    /// click and a second association.
    /// </remarks>
    public AsyncRelayCommand TestWorklistCommand { get; }

    /// <summary>
    /// Checks a password and, if it is right, opens both protected pages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the password as an argument rather than exposing it as a bindable property.
    /// A bound password is a plain string living on the view model for as long as the
    /// binding does; passed across and used here, it is unreachable the moment this
    /// returns, and the view clears the box.
    /// </para>
    /// <para>
    /// Called from either page's unlock button. Both open together, because it is one
    /// password and whoever opened one page can open the other.
    /// </para>
    /// </remarks>
    public void TryUnlock(string? password)
    {
        if (ProtectedSettingsLock.Verify(password))
        {
            AreProtectedSettingsUnlocked = true;
            LockMessage = string.Empty;

            // Audited, not merely logged. Who opened the pages that decide where patient
            // data comes from and goes to is a governance question, and a wrong
            // destination configured at 3am is something an audit trail should be able to
            // place.
            _services.Logger.Information("The protected settings were unlocked.");
            _services.AuditLogger.ConfigurationChanged("Protected settings unlocked.");
        }
        else
        {
            LockMessage = _services.Localizer.Get("Settings.Lock.WrongPassword");

            // Also audited. One wrong attempt is a typo; a run of them is somebody trying.
            _services.Logger.Warning("An incorrect password was offered for the protected settings.");
            _services.AuditLogger.ConfigurationChanged("Protected settings unlock refused: wrong password.");
        }

    }

    private async Task TestWorklistAsync()
    {
        if (SelectedWorklist is null)
        {
            return;
        }

        WorklistTestResult = _services.Localizer.Get("Settings.Worklist.Testing");

        // The server as it is being edited, not as it was saved. Testing a connection and
        // then having to save before the test means anything would make the button useless
        // for the one job it has.
        (bool _, string message) = await _services.Worklist
            .TestConnectionAsync(SelectedWorklist, Working.CallingAeTitle)
            .ConfigureAwait(true);

        WorklistTestResult = message;
    }

    /// <summary>
    /// Adds a worklist server, already enabled and selected.
    /// </summary>
    /// <remarks>
    /// Enabled on creation, because somebody who presses Add wants a worklist. Leaving it
    /// disabled is how a site configures a server correctly, tests it successfully, and
    /// then finds the button still absent.
    /// </remarks>
    private void AddWorklist()
    {
        var server = new WorklistConfiguration
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = _services.Localizer.Get("Settings.Worklist.NewName"),
            IsEnabled = true
        };

        Worklists.Add(server);
        SelectedWorklist = server;
    }

    private void RemoveWorklist()
    {
        if (SelectedWorklist is null)
        {
            return;
        }

        int index = Worklists.IndexOf(SelectedWorklist);
        Worklists.Remove(SelectedWorklist);

        SelectedWorklist = Worklists.Count == 0
            ? null
            : Worklists[Math.Min(index, Worklists.Count - 1)];
    }

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
    /// Modality values offered for a converted image.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same list as for documents. "DOC" describes an encapsulated
    /// document and would be wrong on a photograph or a scan; "XC" is the defined term
    /// for external camera photography, and "OT" suits anything.
    /// </remarks>
    public IReadOnlyList<string> ImageModalityOptions { get; } = new[] { "OT", "XC" };

    /// <summary>How several converted files are gathered into studies.</summary>
    public IReadOnlyList<EnumOption<StudyGroupingMode>> StudyGroupingModes { get; } = new[]
    {
        new EnumOption<StudyGroupingMode>(StudyGroupingMode.SeparateStudy, "Settings.StudyGrouping.Separate"),
        new EnumOption<StudyGroupingMode>(StudyGroupingMode.GroupByPatient, "Settings.StudyGrouping.ByPatient")
    };

    /// <summary>
    /// Every defined term for Conversion Type (0008,0064) in PS3.3 C.8.6.1.1, each with a
    /// plain description.
    /// </summary>
    /// <remarks>
    /// The code alone is what goes into the attribute, but a list reading "WSD, SD, SI,
    /// DV, DI, DF, DRW, SYN" asks an administrator to already know the standard. The
    /// description is what makes the choice possible; the code is kept in front of it so
    /// that anyone who does know can still find the value at a glance.
    ///
    /// DV was previously missing from this list. It is a defined term like the others, and
    /// its absence meant a site that needed it had no way to select it.
    /// </remarks>
    public IReadOnlyList<ConversionTypeOption> ConversionTypeOptions { get; } = new[]
    {
        new ConversionTypeOption("WSD", "ConversionType.WSD"),
        new ConversionTypeOption("SD",  "ConversionType.SD"),
        new ConversionTypeOption("SI",  "ConversionType.SI"),
        new ConversionTypeOption("DV",  "ConversionType.DV"),
        new ConversionTypeOption("DI",  "ConversionType.DI"),
        new ConversionTypeOption("DF",  "ConversionType.DF"),
        new ConversionTypeOption("DRW", "ConversionType.DRW"),
        new ConversionTypeOption("SYN", "ConversionType.SYN")
    };

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
        Working.Worklists = Worklists.ToList();

        // A server that no longer exists must not be remembered as the one last chosen, or
        // the worklist window would open on nothing.
        if (Working.Worklists.All(w => !string.Equals(w.Id, Working.LastUsedWorklistId, StringComparison.Ordinal)))
        {
            Working.LastUsedWorklistId = string.Empty;
        }

        if (SelectedLanguage is not null)
        {
            Working.Language = SelectedLanguage.CultureName;
        }

        // A ComboBox bound by SelectedValue has nothing to select when the stored value
        // matches no item, which happens if a configuration file was hand edited to a
        // value outside the defined terms. Whether WPF then writes null back to the source
        // is not something that can be established without running WPF, so the value is
        // simply put back rather than relying on an answer either way. Silently changing a
        // site's configured value by opening a dialog and pressing Save would be a poor way
        // to find out.
        if (string.IsNullOrWhiteSpace(Working.ConversionType))
        {
            Working.ConversionType = _original.ConversionType;
        }

        if (string.IsNullOrWhiteSpace(Working.Modality))
        {
            Working.Modality = _original.Modality;
        }

        if (string.IsNullOrWhiteSpace(Working.ImageModality))
        {
            Working.ImageModality = _original.ImageModality;
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
        Compare("ImageModality", _original.ImageModality, Working.ImageModality);
        Compare("StudyGrouping", _original.StudyGrouping, Working.StudyGrouping);
        Compare("UidStrategy", _original.UidStrategy, Working.UidStrategy);
        Compare("OutputFolder", _original.OutputFolder, Working.OutputFolder);
        Compare("LogFolder", _original.LogFolder, Working.LogFolder);
        Compare("AutoDelete", _original.AutoDeleteAfterSuccessfulTransmission, Working.AutoDeleteAfterSuccessfulTransmission);
        Compare("FolderMonitoring", _original.EnableFolderMonitoring, Working.EnableFolderMonitoring);
        Compare("FilenameImport", _original.FilenameImport.IsEnabled, Working.FilenameImport.IsEnabled);
        Compare(
            "FilenameImportPatterns",
            string.Join("|", _original.FilenameImport.Patterns),
            string.Join("|", Working.FilenameImport.Patterns));
        Compare("PadPatientIdTo", _original.FilenameImport.PadPatientIdTo, Working.FilenameImport.PadPatientIdTo);
        Compare("AfterImport", _original.FilenameImport.AfterImport, Working.FilenameImport.AfterImport);
        Compare("Language", _original.Language, Working.Language);
        Compare("DestinationCount", _original.Destinations.Count, Working.Destinations.Count);

        return changes.Count == 0 ? "No effective change." : string.Join("; ", changes);
    }
}

/// <summary>
/// A DICOM Conversion Type defined term paired with a description of what it means.
/// </summary>
/// <remarks>
/// Not an <see cref="EnumOption{T}"/> because the underlying value is a DICOM string
/// rather than an enumeration. The attribute stores a code defined by the standard, and
/// modelling it as an enumeration would mean translating back and forth for no gain.
/// </remarks>
/// <param name="Code">The value written to Conversion Type (0008,0064).</param>
/// <param name="ResourceKey">Localization key for the description.</param>
public sealed record ConversionTypeOption(string Code, string ResourceKey)
{
    /// <summary>
    /// What is shown in the picker: the code, then what it means. Resolved in the active
    /// language, so a Portuguese interface describes them in Portuguese.
    /// </summary>
    public string Display => $"{Code}: {LocalizationSource.Instance[ResourceKey]}";
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
