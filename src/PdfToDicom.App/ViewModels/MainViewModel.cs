using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FellowOakDicom;
using Microsoft.Win32;
using PdfToDicom.App.Infrastructure;
using PdfToDicom.App.Views;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.App.ViewModels;

/// <summary>
/// Overall state of the workflow, surfaced in the status bar.
/// </summary>
public enum WorkflowState
{
    /// <summary>Waiting for the operator.</summary>
    Ready,

    /// <summary>Checking the entered information.</summary>
    Validating,

    /// <summary>Building the DICOM object.</summary>
    Generating,

    /// <summary>Transmitting to a destination.</summary>
    Sending,

    /// <summary>The run finished successfully.</summary>
    Completed,

    /// <summary>The run failed.</summary>
    Error
}

/// <summary>
/// View model for the main window. Owns the three step workflow, the status bar and every
/// command the operator can invoke.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationServices _services;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _cancellation;
    private BuiltDicomObject? _lastBuiltObject;
    private DicomFile? _lastGeneratedFile;
    private string? _lastSopInstanceUid;

    // ---------------------------------------------------------------- Backing fields

    private string? _pdfPath;
    private PdfDocumentInfo? _pdfInfo;
    private string _patientId = string.Empty;
    private string _familyName = string.Empty;
    private string _givenName = string.Empty;
    private string _middleName = string.Empty;
    private DateTime? _dateOfBirth;
    private PatientSex _sex = PatientSex.Unspecified;
    private string _accessionNumber = string.Empty;
    private string _referringPhysician = string.Empty;
    private string _studyDescription = string.Empty;
    private string _seriesDescription = string.Empty;
    private string _documentTitle = string.Empty;
    private string _studyInstanceUid = string.Empty;
    private string _studyId = string.Empty;

    private WorkflowState _state = WorkflowState.Ready;
    private string _statusMessage = string.Empty;
    private string _resultMessage = string.Empty;
    private bool _isBusy;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private bool _isDragOver;

    /// <summary>Creates the view model.</summary>
    public MainViewModel(ApplicationServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatcher = Dispatcher.CurrentDispatcher;

        ValidationIssues = new ObservableCollection<ValidationIssue>();
        TransmissionResults = new ObservableCollection<TransmissionResult>();

        BrowseCommand = new RelayCommand(BrowseForPdf, () => !IsBusy);
        ClearPdfCommand = new RelayCommand(ClearPdf, () => !IsBusy && PdfPath is not null);
        ClearFormCommand = new RelayCommand(ClearForm, () => !IsBusy);
        GenerateAndSendCommand = new AsyncRelayCommand(() => RunWorkflowAsync(transmit: true), () => CanRun);
        GenerateOnlyCommand = new AsyncRelayCommand(() => RunWorkflowAsync(transmit: false), () => CanRun);
        CancelCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy);
        RetryCommand = new AsyncRelayCommand(RetryTransmissionAsync, () => !IsBusy && _lastGeneratedFile is not null);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsBusy);
        ViewTagsCommand = new RelayCommand(ShowTagViewer, () => !IsBusy);
        OpenSettingsCommand = new RelayCommand(OpenSettings, () => !IsBusy);
        OpenLogViewerCommand = new RelayCommand(OpenLogViewer);
        ExportLogsCommand = new RelayCommand(ExportLogs, () => !IsBusy);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        ShowAboutCommand = new RelayCommand(ShowAbout);
        ApplyTemplateCommand = new RelayCommand(ApplyTemplate);
        SaveTemplateCommand = new RelayCommand(SaveTemplate, () => !IsBusy);

        StatusMessage = Localize("Status.Ready");
        PopulateDefaultsFromConfiguration();
        RefreshDestinations();
        RefreshTemplates();
        StartOrStopFolderMonitoring();

        _services.FolderMonitor.PdfDetected += OnMonitoredPdfDetected;
        _services.ConfigurationService.ConfigurationChanged += OnConfigurationChanged;
    }

    // ---------------------------------------------------------------- Step 1: PDF

    /// <summary>Path of the selected PDF, or null when none is selected.</summary>
    public string? PdfPath
    {
        get => _pdfPath;
        private set
        {
            if (SetProperty(ref _pdfPath, value))
            {
                OnPropertyChanged(nameof(HasPdf));
                RefreshCommands();
            }
        }
    }

    /// <summary>Descriptive information about the selected document.</summary>
    public PdfDocumentInfo? PdfInfo
    {
        get => _pdfInfo;
        private set
        {
            if (SetProperty(ref _pdfInfo, value))
            {
                OnPropertyChanged(nameof(PdfPageCountDisplay));
            }
        }
    }

    /// <summary>True when a document is selected.</summary>
    public bool HasPdf => PdfPath is not null;

    /// <summary>Page count rendered for display, or a dash when it is unknown.</summary>
    public string PdfPageCountDisplay =>
        PdfInfo?.PageCount?.ToString() ?? "-";

    /// <summary>True while a file is being dragged over the drop zone.</summary>
    public bool IsDragOver
    {
        get => _isDragOver;
        set => SetProperty(ref _isDragOver, value);
    }

    // ---------------------------------------------------------------- Step 2: Patient

    /// <summary>Patient ID. Mandatory.</summary>
    public string PatientId
    {
        get => _patientId;
        set { if (SetProperty(ref _patientId, value)) OnFormChanged(); }
    }

    /// <summary>Patient surname. Mandatory.</summary>
    public string FamilyName
    {
        get => _familyName;
        set { if (SetProperty(ref _familyName, value)) OnFormChanged(); }
    }

    /// <summary>Patient given name.</summary>
    public string GivenName
    {
        get => _givenName;
        set { if (SetProperty(ref _givenName, value)) OnFormChanged(); }
    }

    /// <summary>Patient middle name.</summary>
    public string MiddleName
    {
        get => _middleName;
        set { if (SetProperty(ref _middleName, value)) OnFormChanged(); }
    }

    /// <summary>Date of birth. Mandatory.</summary>
    public DateTime? DateOfBirth
    {
        get => _dateOfBirth;
        set { if (SetProperty(ref _dateOfBirth, value)) OnFormChanged(); }
    }

    /// <summary>Patient sex. Optional.</summary>
    public PatientSex Sex
    {
        get => _sex;
        set => SetProperty(ref _sex, value);
    }

    /// <summary>Accession number. Mandatory.</summary>
    public string AccessionNumber
    {
        get => _accessionNumber;
        set { if (SetProperty(ref _accessionNumber, value)) OnFormChanged(); }
    }

    /// <summary>Referring physician. Optional.</summary>
    public string ReferringPhysician
    {
        get => _referringPhysician;
        set => SetProperty(ref _referringPhysician, value);
    }

    /// <summary>Study description. Optional.</summary>
    public string StudyDescription
    {
        get => _studyDescription;
        set => SetProperty(ref _studyDescription, value);
    }

    /// <summary>Series description. Optional.</summary>
    public string SeriesDescription
    {
        get => _seriesDescription;
        set => SetProperty(ref _seriesDescription, value);
    }

    /// <summary>Document title. Optional.</summary>
    public string DocumentTitle
    {
        get => _documentTitle;
        set => SetProperty(ref _documentTitle, value);
    }

    /// <summary>Study Instance UID for filing into an existing study. Advanced, optional.</summary>
    public string StudyInstanceUid
    {
        get => _studyInstanceUid;
        set { if (SetProperty(ref _studyInstanceUid, value)) OnFormChanged(); }
    }

    /// <summary>Study ID. Advanced, optional.</summary>
    public string StudyId
    {
        get => _studyId;
        set => SetProperty(ref _studyId, value);
    }

    /// <summary>Options offered by the sex selector.</summary>
    public IReadOnlyList<PatientSexOption> SexOptions { get; } = new[]
    {
        new PatientSexOption(PatientSex.Unspecified, "Sex.Unspecified"),
        new PatientSexOption(PatientSex.Male, "Sex.Male"),
        new PatientSexOption(PatientSex.Female, "Sex.Female"),
        new PatientSexOption(PatientSex.Other, "Sex.Other")
    };

    /// <summary>True when the advanced DICOM fields should be visible.</summary>
    public bool ShowAdvancedFields => _services.ConfigurationService.Current.ShowAdvancedFields;

    // ---------------------------------------------------------------- Step 3: Destination

    /// <summary>The destinations the document will be sent to.</summary>
    public ObservableCollection<PacsDestination> Destinations { get; } = new();

    /// <summary>Saved, non identifying form templates.</summary>
    public ObservableCollection<PatientTemplate> Templates { get; } = new();

    /// <summary>The template currently selected in the picker.</summary>
    public PatientTemplate? SelectedTemplate { get; set; }

    /// <summary>Summary of the institution, shown in the window header.</summary>
    public string InstitutionHeader
    {
        get
        {
            AppConfiguration configuration = _services.ConfigurationService.Current;

            return string.IsNullOrWhiteSpace(configuration.HospitalName)
                ? Localize("App.Subtitle")
                : configuration.HospitalName;
        }
    }

    // ---------------------------------------------------------------- Status

    /// <summary>Current workflow state.</summary>
    public WorkflowState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(IsCompleted));
            }
        }
    }

    /// <summary>True when the last run failed.</summary>
    public bool IsError => State == WorkflowState.Error;

    /// <summary>True when the last run succeeded.</summary>
    public bool IsCompleted => State == WorkflowState.Completed;

    /// <summary>Short status text shown in the status bar.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Detailed outcome text shown in the results panel.</summary>
    public string ResultMessage
    {
        get => _resultMessage;
        private set
        {
            if (SetProperty(ref _resultMessage, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    /// <summary>True when a result is available to display.</summary>
    public bool HasResult => !string.IsNullOrEmpty(ResultMessage);

    /// <summary>True while an operation is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    /// <summary>Progress bar value between zero and one hundred.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    /// <summary>True while the progress bar should animate without a known end point.</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    /// <summary>Validation findings from the most recent check.</summary>
    public ObservableCollection<ValidationIssue> ValidationIssues { get; }

    /// <summary>Transmission results from the most recent run.</summary>
    public ObservableCollection<TransmissionResult> TransmissionResults { get; }

    /// <summary>True when there is at least one validation finding to show.</summary>
    public bool HasValidationIssues => ValidationIssues.Count > 0;

    /// <summary>True when the primary action may be invoked.</summary>
    public bool CanRun => !IsBusy && HasPdf;

    // ---------------------------------------------------------------- Commands

    /// <summary>Opens the file picker.</summary>
    public RelayCommand BrowseCommand { get; }

    /// <summary>Clears the selected document.</summary>
    public RelayCommand ClearPdfCommand { get; }

    /// <summary>Clears the patient form.</summary>
    public RelayCommand ClearFormCommand { get; }

    /// <summary>Generates the object and transmits it.</summary>
    public AsyncRelayCommand GenerateAndSendCommand { get; }

    /// <summary>Generates and saves the object without transmitting.</summary>
    public AsyncRelayCommand GenerateOnlyCommand { get; }

    /// <summary>Cancels the operation in flight.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Retransmits the object produced by the previous run.</summary>
    public AsyncRelayCommand RetryCommand { get; }

    /// <summary>Sends a verification request to every enabled destination.</summary>
    public AsyncRelayCommand TestConnectionCommand { get; }

    /// <summary>Opens the tag viewer for the object produced by the previous run.</summary>
    public RelayCommand ViewTagsCommand { get; }

    /// <summary>Opens the settings dialog.</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>Opens the log viewer.</summary>
    public RelayCommand OpenLogViewerCommand { get; }

    /// <summary>Exports the logs to a zip archive.</summary>
    public RelayCommand ExportLogsCommand { get; }

    /// <summary>Opens the output folder in Windows Explorer.</summary>
    public RelayCommand OpenOutputFolderCommand { get; }

    /// <summary>Shows the About dialog.</summary>
    public RelayCommand ShowAboutCommand { get; }

    /// <summary>Applies the selected template to the form.</summary>
    public RelayCommand ApplyTemplateCommand { get; }

    /// <summary>Saves the current non identifying values as a template.</summary>
    public RelayCommand SaveTemplateCommand { get; }

    // ---------------------------------------------------------------- PDF selection

    /// <summary>
    /// Loads a PDF, gathering its descriptive information and reporting any problem with
    /// it immediately rather than at the point of conversion.
    /// </summary>
    public void LoadPdf(string path)
    {
        try
        {
            ValidationResult validation = _services.PdfInspector.Validate(path);
            ReplaceValidationIssues(validation.Issues);

            if (!validation.IsValid)
            {
                PdfPath = null;
                PdfInfo = null;
                SetState(WorkflowState.Error, validation.Errors.First().Message);
                return;
            }

            PdfPath = path;
            PdfInfo = _services.PdfInspector.Inspect(path);

            // A document title from the PDF metadata is a better default than the
            // configured generic one, so it is offered when the field is still empty.
            if (string.IsNullOrWhiteSpace(DocumentTitle) && !string.IsNullOrWhiteSpace(PdfInfo.Title))
            {
                DocumentTitle = PdfInfo.Title;
            }

            _services.Logger.Information(
                $"The operator selected '{PdfInfo.FileName}' ({PdfInfo.FormattedSize}, " +
                $"{PdfInfo.PageCount?.ToString() ?? "unknown"} page(s)).");

            SetState(WorkflowState.Ready, Localize("Status.Ready"));
            ResultMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"The PDF '{path}' could not be loaded.", ex);
            SetState(WorkflowState.Error, Localize("Error.PdfNotReadable"));
        }
    }

    private void BrowseForPdf()
    {
        var dialog = new OpenFileDialog
        {
            Title = Localize("Step1.Browse"),
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = ResolveInitialDirectory()
        };

        if (dialog.ShowDialog() == true)
        {
            LoadPdf(dialog.FileName);
        }
    }

    /// <summary>
    /// Chooses the folder the picker opens in, falling back to Documents when the
    /// configured folder no longer exists, for example after a profile move.
    /// </summary>
    private string ResolveInitialDirectory()
    {
        string configured = _services.ConfigurationService.Current.DefaultPdfFolder;

        return Directory.Exists(configured)
            ? configured
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void ClearPdf()
    {
        PdfPath = null;
        PdfInfo = null;
        ValidationIssues.Clear();
        OnPropertyChanged(nameof(HasValidationIssues));
        SetState(WorkflowState.Ready, Localize("Status.Ready"));
    }

    private void ClearForm()
    {
        PatientId = string.Empty;
        FamilyName = string.Empty;
        GivenName = string.Empty;
        MiddleName = string.Empty;
        DateOfBirth = null;
        Sex = PatientSex.Unspecified;
        AccessionNumber = string.Empty;
        ReferringPhysician = string.Empty;
        StudyInstanceUid = string.Empty;
        StudyId = string.Empty;

        PopulateDefaultsFromConfiguration();

        ValidationIssues.Clear();
        OnPropertyChanged(nameof(HasValidationIssues));
        ResultMessage = string.Empty;
        SetState(WorkflowState.Ready, Localize("Status.Ready"));
    }

    // ---------------------------------------------------------------- Workflow

    /// <summary>Assembles the demographics from the form fields.</summary>
    private PatientDemographics BuildDemographics() => new()
    {
        PatientId = PatientId.Trim(),
        FamilyName = FamilyName.Trim(),
        GivenName = GivenName.Trim(),
        MiddleName = MiddleName.Trim(),
        DateOfBirth = DateOfBirth,
        Sex = Sex,
        AccessionNumber = AccessionNumber.Trim(),
        ReferringPhysicianName = ReferringPhysician.Trim(),
        StudyDescription = StudyDescription.Trim(),
        SeriesDescription = SeriesDescription.Trim(),
        DocumentTitle = DocumentTitle.Trim(),
        ExistingStudyInstanceUid = StudyInstanceUid.Trim(),
        StudyId = StudyId.Trim()
    };

    private async Task RunWorkflowAsync(bool transmit)
    {
        if (PdfPath is null)
        {
            return;
        }

        PatientDemographics demographics = BuildDemographics();

        // ------------------------------------------------ Validate before doing anything
        SetState(WorkflowState.Validating, Localize("Status.Validating"));

        ValidationResult validation = _services.Workflow.ValidateInputs(PdfPath, demographics, transmit);
        ReplaceValidationIssues(validation.Issues);

        if (!validation.IsValid)
        {
            SetState(WorkflowState.Error, Localize("Validation.CannotContinue"));
            ResultMessage = validation.ToDisplayString();
            return;
        }

        // ------------------------------------------------ Confirm with the operator
        if (transmit && _services.ConfigurationService.Current.ConfirmBeforeSending &&
            !ConfirmSend(demographics))
        {
            SetState(WorkflowState.Ready, Localize("Status.Ready"));
            return;
        }

        // ------------------------------------------------ Run
        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        TransmissionResults.Clear();
        ResultMessage = string.Empty;

        SetState(WorkflowState.Generating, Localize("Status.GeneratingDicom"));

        var progress = new Progress<TransmissionProgress>(OnTransmissionProgress);

        try
        {
            ConversionResult result = await _services.Workflow.RunAsync(
                PdfPath,
                demographics,
                new ConversionOptions(transmit),
                progress,
                _cancellation.Token);

            ApplyResult(result, transmit);
        }
        catch (OperationCanceledException)
        {
            SetState(WorkflowState.Ready, Localize("Status.Cancelled"));
            ResultMessage = Localize("Status.Cancelled");
        }
        catch (Exception ex)
        {
            _services.Logger.Error("The conversion workflow failed unexpectedly.", ex);
            SetState(WorkflowState.Error, Localize("Status.Error"));
            ResultMessage = Localize("Error.Unexpected") + Environment.NewLine + ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Applies a completed run to the view state.</summary>
    private void ApplyResult(ConversionResult result, bool transmit)
    {
        ReplaceValidationIssues(result.ValidationIssues);

        foreach (TransmissionResult transmission in result.Transmissions)
        {
            TransmissionResults.Add(transmission);
        }

        _lastSopInstanceUid = result.SopInstanceUid;

        // The generated object is retained in memory so that the tag viewer and the retry
        // action work even when the site has chosen not to keep files on disk.
        if (result.DicomGenerated && result.GeneratedFilePath is not null &&
            File.Exists(result.GeneratedFilePath))
        {
            try
            {
                _lastGeneratedFile = DicomFile.Open(result.GeneratedFilePath);
            }
            catch (Exception ex)
            {
                _services.Logger.Warning($"The generated file could not be reopened for inspection: {ex.Message}");
            }
        }

        if (!result.DicomGenerated)
        {
            SetState(WorkflowState.Error, Localize("Status.Error"));
            ResultMessage = result.Summary;
            return;
        }

        if (!transmit)
        {
            SetState(WorkflowState.Completed, Localize("Status.Completed"));
            ResultMessage = result.Summary;
            return;
        }

        if (result.AllTransmissionsSucceeded)
        {
            SetState(WorkflowState.Completed, Localize("Status.Completed"));
            ResultMessage = Localize("Result.Success") + Environment.NewLine + result.Summary;
        }
        else if (result.AnyTransmissionSucceeded)
        {
            SetState(WorkflowState.Error, Localize("Status.Error"));
            ResultMessage = Localize("Result.PartialSuccess") + Environment.NewLine + result.Summary;
        }
        else
        {
            SetState(WorkflowState.Error, Localize("Status.Error"));
            ResultMessage = Localize("Result.Failure") + Environment.NewLine + result.Summary;
        }

        RefreshCommands();
    }

    private void OnTransmissionProgress(TransmissionProgress progress)
    {
        // Progress is reported from a background thread; marshalling here keeps every
        // property change on the dispatcher.
        void Update()
        {
            IsProgressIndeterminate = true;

            StatusMessage = progress.Stage switch
            {
                TransmissionStage.Connecting =>
                    _services.Localizer.Format("Status.Connecting", progress.DestinationName),
                TransmissionStage.Verifying =>
                    _services.Localizer.Format("Status.Verifying", progress.DestinationName),
                TransmissionStage.Storing =>
                    _services.Localizer.Format("Status.Sending", progress.DestinationName),
                TransmissionStage.Retrying =>
                    _services.Localizer.Format(
                        "Status.Retrying", progress.DestinationName, progress.AttemptNumber, progress.TotalAttempts),
                _ => progress.Message
            };

            if (progress.Stage != TransmissionStage.Completed)
            {
                State = WorkflowState.Sending;
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.Invoke(Update);
        }
    }

    private void CancelCurrentOperation()
    {
        _cancellation?.Cancel();
        StatusMessage = Localize("Status.Cancelled");
    }

    private async Task RetryTransmissionAsync()
    {
        if (_lastGeneratedFile is null || _lastSopInstanceUid is null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        TransmissionResults.Clear();

        try
        {
            IReadOnlyList<TransmissionResult> results = await _services.Workflow.RetryTransmissionAsync(
                _lastGeneratedFile,
                _lastSopInstanceUid,
                progress: new Progress<TransmissionProgress>(OnTransmissionProgress),
                cancellationToken: _cancellation.Token);

            foreach (TransmissionResult result in results)
            {
                TransmissionResults.Add(result);
            }

            bool allSucceeded = results.Count > 0 && results.All(r => r.IsSuccess);

            SetState(
                allSucceeded ? WorkflowState.Completed : WorkflowState.Error,
                Localize(allSucceeded ? "Status.Completed" : "Status.Error"));

            ResultMessage = allSucceeded ? Localize("Result.Success") : Localize("Result.Failure");
        }
        catch (OperationCanceledException)
        {
            SetState(WorkflowState.Ready, Localize("Status.Cancelled"));
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private async Task TestConnectionAsync()
    {
        AppConfiguration configuration = _services.ConfigurationService.Current;
        IReadOnlyList<PacsDestination> destinations = configuration.EnabledDestinations;

        if (destinations.Count == 0)
        {
            MessageBox.Show(Localize("Step3.NoDestinations"), Localize("App.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        TransmissionResults.Clear();

        try
        {
            foreach (PacsDestination destination in destinations)
            {
                StatusMessage = _services.Localizer.Format("Status.Verifying", destination.Name);

                TransmissionResult result = await _services.NetworkService.VerifyAsync(
                    destination, configuration.CallingAeTitle);

                TransmissionResults.Add(result);
            }

            bool allSucceeded = TransmissionResults.All(r => r.IsSuccess);

            SetState(
                allSucceeded ? WorkflowState.Completed : WorkflowState.Error,
                Localize(allSucceeded ? "Status.Completed" : "Status.Error"));

            ResultMessage = string.Join(
                Environment.NewLine,
                TransmissionResults.Select(r => $"{(r.IsSuccess ? "OK" : "FAIL")} - {r.Message}"));
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ---------------------------------------------------------------- Dialogs

    /// <summary>
    /// Shows the confirmation summary. Reading the identity back to the operator before
    /// anything leaves the workstation is the last practical defence against a document
    /// being filed against the wrong patient.
    /// </summary>
    private bool ConfirmSend(PatientDemographics demographics)
    {
        string destinations = string.Join(
            Environment.NewLine,
            _services.ConfigurationService.Current.EnabledDestinations.Select(d => "   - " + d.DisplaySummary));

        string message =
            Localize("Confirm.Message") + Environment.NewLine + Environment.NewLine +
            $"{Localize("Step2.PatientId")}: {demographics.PatientId}" + Environment.NewLine +
            $"{Localize("Step2.FamilyName")}: {demographics.FamilyName}, {demographics.GivenName}" + Environment.NewLine +
            $"{Localize("Step2.DateOfBirth")}: {demographics.DateOfBirth:d}" + Environment.NewLine +
            $"{Localize("Step2.AccessionNumber")}: {demographics.AccessionNumber}" + Environment.NewLine +
            $"{Localize("Step1.FileName")}: {PdfInfo?.FileName}" + Environment.NewLine + Environment.NewLine +
            Localize("Step3.Header") + ":" + Environment.NewLine + destinations;

        return MessageBox.Show(
            message,
            Localize("Confirm.Title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private void ShowTagViewer()
    {
        if (_lastGeneratedFile is null)
        {
            // Nothing has been generated yet, so a preview is built from the current form
            // values. This lets an advanced user inspect the tags before committing.
            if (PdfPath is null)
            {
                MessageBox.Show(Localize("Step1.NoFileSelected"), Localize("App.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _lastBuiltObject = _services.Builder.Build(PdfPath, BuildDemographics(), PdfInfo);
                _lastGeneratedFile = _lastBuiltObject.File;
            }
            catch (Exception ex)
            {
                _services.Logger.Error("A preview object could not be generated for the tag viewer.", ex);
                MessageBox.Show(ex.Message, Localize("App.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var window = new TagViewerWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = new TagViewerViewModel(_services, _lastGeneratedFile)
        };

        window.ShowDialog();
    }

    private void OpenSettings()
    {
        var viewModel = new SettingsViewModel(_services);
        var window = new SettingsWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel
        };

        if (window.ShowDialog() == true)
        {
            RefreshDestinations();
            RefreshTemplates();
            StartOrStopFolderMonitoring();

            OnPropertyChanged(nameof(InstitutionHeader));
            OnPropertyChanged(nameof(ShowAdvancedFields));
        }
    }

    private void OpenLogViewer()
    {
        var window = new LogViewerWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = new LogViewerViewModel(_services)
        };

        window.Show();
    }

    private void ExportLogs()
    {
        var dialog = new SaveFileDialog
        {
            Title = Localize("Action.ExportLogs"),
            Filter = "Zip archive (*.zip)|*.zip",
            FileName = $"pdftodicom-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            AppConfiguration configuration = _services.ConfigurationService.Current;

            int count = _services.LogExporter.Export(
                dialog.FileName,
                new[] { configuration.LogFolder, _services.AuditLogger.AuditFolder },
                new[] { _services.ConfigurationService.ConfigurationFilePath });

            MessageBox.Show(
                $"{count} file(s) were exported to:{Environment.NewLine}{dialog.FileName}",
                Localize("App.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("The logs could not be exported.", ex);
            MessageBox.Show(ex.Message, Localize("App.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenOutputFolder()
    {
        string folder = _services.ConfigurationService.Current.OutputFolder;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Logger.Warning($"The output folder '{folder}' could not be opened: {ex.Message}");
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"{Localize("App.Title")}{Environment.NewLine}" +
            $"{_services.Localizer.Format("About.Version", App.ProductVersion)}{Environment.NewLine}{Environment.NewLine}" +
            $"{Localize("About.Offline")}{Environment.NewLine}{Environment.NewLine}" +
            $"{Localize("Settings.LogFolder")}: {_services.Logger.LogFolder}{Environment.NewLine}" +
            $"{Localize("Settings.OutputFolder")}: {_services.ConfigurationService.Current.OutputFolder}",
            Localize("Action.About"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- Templates

    private void ApplyTemplate()
    {
        SelectedTemplate?.ApplyTo(BuildDemographics());

        if (SelectedTemplate is null)
        {
            return;
        }

        ReferringPhysician = SelectedTemplate.ReferringPhysicianName;
        StudyDescription = SelectedTemplate.StudyDescription;
        SeriesDescription = SelectedTemplate.SeriesDescription;
        DocumentTitle = SelectedTemplate.DocumentTitle;
    }

    private void SaveTemplate()
    {
        var window = new TextPromptWindow(
            Localize("Step2.SaveTemplate"),
            Localize("Settings.DestinationName"))
        {
            Owner = Application.Current.MainWindow
        };

        if (window.ShowDialog() != true || string.IsNullOrWhiteSpace(window.EnteredText))
        {
            return;
        }

        AppConfiguration configuration = _services.ConfigurationService.Current;
        PatientTemplate template = PatientTemplate.CaptureFrom(window.EnteredText.Trim(), BuildDemographics());

        configuration.Templates.RemoveAll(t =>
            string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase));

        configuration.Templates.Add(template);
        _services.ConfigurationService.Save(configuration);
        _services.AuditLogger.ConfigurationChanged($"A patient form template named '{template.Name}' was saved.");

        RefreshTemplates();
    }

    // ---------------------------------------------------------------- Plumbing

    private void OnMonitoredPdfDetected(object? sender, string path)
    {
        // Raised on a thread pool thread by the watcher.
        _dispatcher.Invoke(() =>
        {
            if (IsBusy)
            {
                _services.Logger.Information(
                    $"'{path}' was detected while a conversion was in progress and was not loaded automatically.");
                return;
            }

            LoadPdf(path);
            StatusMessage = Localize("Step1.MonitoredFolderPickup");
        });
    }

    private void OnConfigurationChanged(object? sender, AppConfiguration configuration)
    {
        void Update()
        {
            OnPropertyChanged(nameof(InstitutionHeader));
            OnPropertyChanged(nameof(ShowAdvancedFields));
            RefreshDestinations();
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.Invoke(Update);
        }
    }

    private void StartOrStopFolderMonitoring()
    {
        AppConfiguration configuration = _services.ConfigurationService.Current;

        if (configuration.EnableFolderMonitoring && !string.IsNullOrWhiteSpace(configuration.MonitoredFolder))
        {
            _services.FolderMonitor.Start(configuration.MonitoredFolder);
        }
        else
        {
            _services.FolderMonitor.Stop();
        }
    }

    /// <summary>
    /// Applies the configured defaults to a blank form, which is what removes the need for
    /// the operator to retype the same descriptions on every document.
    /// </summary>
    private void PopulateDefaultsFromConfiguration()
    {
        AppConfiguration configuration = _services.ConfigurationService.Current;

        if (string.IsNullOrWhiteSpace(StudyDescription))
        {
            StudyDescription = configuration.DefaultStudyDescription;
        }

        if (string.IsNullOrWhiteSpace(SeriesDescription))
        {
            SeriesDescription = configuration.DefaultSeriesDescription;
        }

        if (string.IsNullOrWhiteSpace(DocumentTitle))
        {
            DocumentTitle = configuration.DefaultDocumentTitle;
        }
    }

    private void RefreshDestinations()
    {
        Destinations.Clear();

        foreach (PacsDestination destination in _services.ConfigurationService.Current.EnabledDestinations)
        {
            Destinations.Add(destination);
        }

        OnPropertyChanged(nameof(Destinations));
    }

    private void RefreshTemplates()
    {
        Templates.Clear();

        foreach (PatientTemplate template in _services.ConfigurationService.Current.Templates)
        {
            Templates.Add(template);
        }
    }

    private void ReplaceValidationIssues(IEnumerable<ValidationIssue> issues)
    {
        ValidationIssues.Clear();

        foreach (ValidationIssue issue in issues)
        {
            ValidationIssues.Add(issue);
        }

        OnPropertyChanged(nameof(HasValidationIssues));
    }

    private void OnFormChanged() => RefreshCommands();

    private void SetState(WorkflowState state, string message)
    {
        State = state;
        StatusMessage = message;
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanRun));

        BrowseCommand.RaiseCanExecuteChanged();
        ClearPdfCommand.RaiseCanExecuteChanged();
        ClearFormCommand.RaiseCanExecuteChanged();
        GenerateAndSendCommand.RaiseCanExecuteChanged();
        GenerateOnlyCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        TestConnectionCommand.RaiseCanExecuteChanged();
        ViewTagsCommand.RaiseCanExecuteChanged();
        OpenSettingsCommand.RaiseCanExecuteChanged();
        ExportLogsCommand.RaiseCanExecuteChanged();
        SaveTemplateCommand.RaiseCanExecuteChanged();
    }

    private string Localize(string key) => _services.Localizer.Get(key);

    /// <inheritdoc />
    public void Dispose()
    {
        _services.FolderMonitor.PdfDetected -= OnMonitoredPdfDetected;
        _services.ConfigurationService.ConfigurationChanged -= OnConfigurationChanged;
        _cancellation?.Dispose();
    }
}

/// <summary>An entry in the patient sex selector.</summary>
/// <param name="Value">The underlying value.</param>
/// <param name="ResourceKey">Localization key for the label.</param>
public sealed record PatientSexOption(PatientSex Value, string ResourceKey)
{
    /// <summary>Label shown in the selector, resolved in the active language.</summary>
    public string Display => Infrastructure.LocalizationSource.Instance[ResourceKey];
}
