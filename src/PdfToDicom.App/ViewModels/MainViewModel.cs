using System.Globalization;
using System.Collections.ObjectModel;
using System.Runtime;
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
using PdfToDicom.Core.Importing;
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
    private bool _exitConfirmed;
    private BuiltDicomObject? _lastBuiltObject;
    private DicomFile? _lastGeneratedFile;
    private string? _lastSopInstanceUid;

    // ---------------------------------------------------------------- Backing fields

    private string? _pdfPath;
    private PdfDocumentInfo? _pdfInfo;
    private ImageDocumentInfo? _imageInfo;
    private SourceDocumentKind _sourceKind = SourceDocumentKind.Unsupported;
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
    /// <summary>
    /// Study Date and Time taken from an imported file's creation time, or null.
    /// </summary>
    /// <remarks>
    /// Deliberately a field rather than a bound property. The study date is a fact about
    /// the file, not something to offer an operator a box for; an editable one would invite
    /// somebody to put a study on a date it did not happen.
    /// </remarks>
    private DateTime? _studyDateTimeFromFile;
    private bool _cameFromMonitoredFolder;

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
        OpenWorklistCommand = new RelayCommand(OpenWorklist, () => !IsBusy);
        SaveTemplateCommand = new RelayCommand(SaveTemplate, () => !IsBusy);

        // Always enabled, deliberately. A busy application is exactly when somebody needs
        // to be able to stop it, and the confirmation says what is in progress rather than
        // the button refusing to be pressed.
        ExitCommand = new RelayCommand(RequestExit);

        StatusMessage = Localize("Status.Ready");
        PopulateDefaultsFromConfiguration();
        RefreshDestinations();
        RefreshTemplates();
        StartOrStopFolderMonitoring();

        _services.FolderMonitor.PdfDetected += OnMonitoredPdfDetected;
        _services.ConfigurationService.ConfigurationChanged += OnConfigurationChanged;

        ReportConfigurationProblem();
    }

    /// <summary>
    /// Says so, on the window, when the configuration file could not be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A damaged file is set aside and defaults take over, so the application always
    /// starts. It used to do that in silence, and the result is bewildering: every
    /// destination and every worklist server is gone, nothing failed, and the only record
    /// is a text file beside the quarantined one that nobody would think to look for.
    /// </para>
    /// <para>
    /// Not a dialog. The operator may have a document to file and cannot do anything about
    /// a configuration file anyway, so it goes to the status bar and the result area, where
    /// it stays visible, and to the log and the audit trail for whoever can.
    /// </para>
    /// </remarks>
    private void ReportConfigurationProblem()
    {
        string problem = _services.ConfigurationService.LoadProblem;
        string notice = _services.ConfigurationService.LoadNotice;

        if (problem.Length > 0)
        {
            SetState(WorkflowState.Error, Localize("Status.ConfigurationProblem"));
            ResultMessage = problem;

            _services.Logger.Error($"The configuration could not be loaded: {problem}", null);
            _services.AuditLogger.ConfigurationChanged($"Configuration not loaded: {problem}");
            return;
        }

        if (notice.Length > 0)
        {
            // Not an error, so the state is left alone and the operator can carry straight
            // on. It still gets said: the settings file on disk has just been rewritten in
            // a new shape, and somebody who knows what was in it should see that happened
            // rather than discover it weeks later.
            ResultMessage = notice;

            _services.Logger.Information($"The configuration was upgraded: {notice}");
            _services.AuditLogger.ConfigurationChanged($"Configuration upgraded: {notice}");
        }
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

    /// <summary>Descriptive information about the selected image, when one is selected.</summary>
    public ImageDocumentInfo? ImageInfo
    {
        get => _imageInfo;
        private set
        {
            if (SetProperty(ref _imageInfo, value))
            {
                OnPropertyChanged(nameof(PdfPageCountDisplay));
                OnPropertyChanged(nameof(SourceDescription));
            }
        }
    }

    /// <summary>What kind of file is currently selected.</summary>
    public SourceDocumentKind SourceKind
    {
        get => _sourceKind;
        private set
        {
            if (SetProperty(ref _sourceKind, value))
            {
                OnPropertyChanged(nameof(IsImageSelected));
                OnPropertyChanged(nameof(SourceDescription));
            }
        }
    }

    /// <summary>True when the selected file is an image rather than a document.</summary>
    public bool IsImageSelected => SourceKind == SourceDocumentKind.Image;

    /// <summary>
    /// One line describing what was selected and what it will become, so the operator can
    /// see before converting that the file was understood the way they expected.
    /// </summary>
    public string SourceDescription
    {
        get
        {
            if (ImageInfo is not null)
            {
                string instances = ImageInfo.PageCount > 1
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        Localize("Step1.ImageMultiPage"),
                        ImageInfo.PageCount)
                    : Localize("Step1.ImageSinglePage");

                return $"{ImageInfo.FormatName}, {ImageInfo.PixelSummary}. {instances}";
            }

            if (PdfInfo is not null)
            {
                return $"PDF, {PdfInfo.PageCount?.ToString(CultureInfo.CurrentCulture) ?? "?"} page(s).";
            }

            return string.Empty;
        }
    }

    /// <summary>True when a document is selected.</summary>
    public bool HasPdf => PdfPath is not null;

    /// <summary>Page count rendered for display, or a dash when it is unknown.</summary>
    public string PdfPageCountDisplay =>
        ImageInfo is not null
            ? ImageInfo.PageCount.ToString(CultureInfo.CurrentCulture)
            : PdfInfo?.PageCount?.ToString(CultureInfo.CurrentCulture) ?? "-";

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

    /// <summary>Opens the worklist so a scheduled procedure can be chosen.</summary>
    public RelayCommand OpenWorklistCommand { get; }

    /// <summary>Saves the current non identifying values as a template.</summary>
    public RelayCommand SaveTemplateCommand { get; }

    /// <summary>Closes the application, after confirming and stopping anything in progress.</summary>
    public RelayCommand ExitCommand { get; }

    // ---------------------------------------------------------------- PDF selection

    /// <summary>
    /// Loads a PDF, gathering its descriptive information and reporting any problem with
    /// it immediately rather than at the point of conversion.
    /// </summary>
    public void LoadPdf(string path)
    {
        try
        {
            // What the file is decided from its content. An operator who renamed a scan,
            // or a scanner that writes a TIFF with a .jpg extension, is commonplace, and
            // trusting the extension would reject a file that converts perfectly well.
            SourceDocumentKind kind = _services.ImageInspector.DetectKind(path);

            // Reported here rather than left to the PDF validator. That validator would say
            // the file has no PDF header, which is true and unhelpful: it does not mention
            // that an image would have been accepted, and the operator who dragged in a
            // Word document deserves to be told what this application does take.
            if (kind == SourceDocumentKind.Unsupported)
            {
                ClearSelection();
                ReplaceValidationIssues(Array.Empty<ValidationIssue>());
                SetState(WorkflowState.Error, UnsupportedFileMessage(path));
                return;
            }

            ValidationResult validation = kind == SourceDocumentKind.Image
                ? _services.ImageInspector.Validate(path)
                : _services.PdfInspector.Validate(path);

            ReplaceValidationIssues(validation.Issues);

            if (!validation.IsValid)
            {
                ClearSelection();
                SetState(WorkflowState.Error, validation.Errors.First().Message);
                return;
            }

            PdfPath = path;
            SourceKind = kind;

            // A study date read from a previous file belongs to that file alone. Cleared on
            // every selection so a document chosen by hand cannot inherit it; the monitored
            // folder path sets it again straight after this, from the file it just picked up.
            _studyDateTimeFromFile = null;

            // Likewise: a file the operator browsed to is theirs, and moving it out from
            // under them afterwards would be presumptuous. Only the monitored folder path
            // turns this back on.
            _cameFromMonitoredFolder = false;

            if (kind == SourceDocumentKind.Image)
            {
                PdfInfo = null;
                ImageInfo = _services.ImageInspector.Inspect(path);

                _services.Logger.Information(
                    $"The operator selected the image '{ImageInfo.FileName}' " +
                    $"({ImageInfo.FormatName}, {ImageInfo.PixelSummary}, {ImageInfo.FormattedSize}).");
            }
            else
            {
                ImageInfo = null;
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
            }

            OnPropertyChanged(nameof(SourceDescription));

            SetState(WorkflowState.Ready, Localize("Status.Ready"));
            ResultMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"The file '{path}' could not be loaded.", ex);
            SetState(WorkflowState.Error, Localize("Error.PdfNotReadable"));
        }
    }

    /// <summary>Forgets the selected file and everything read from it.</summary>
    private void ClearSelection()
    {
        PdfPath = null;
        PdfInfo = null;
        ImageInfo = null;
        SourceKind = SourceDocumentKind.Unsupported;
        _cameFromMonitoredFolder = false;
        _studyDateTimeFromFile = null;
    }

    private void BrowseForPdf()
    {
        var dialog = new OpenFileDialog
        {
            Title = Localize("Step1.Browse"),
            Filter = BuildFileDialogFilter(),
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
    /// Explains that a file cannot be converted, and says what can be, naming the formats
    /// the inspector actually supports so the message cannot drift from the code.
    /// </summary>
    private string UnsupportedFileMessage(string path)
    {
        string formats = string.Join(
            ", ",
            _services.ImageInspector.SupportedExtensions.Select(e => e.ToUpperInvariant()).Distinct());

        return $"'{Path.GetFileName(path)}' is neither a PDF nor an image this application can read. " +
               $"PDF documents and these image formats can be converted: {formats}.";
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

    /// <summary>
    /// Builds the file picker filter from the formats the inspector actually supports, so
    /// the dialog and the converter can never disagree about what can be opened.
    /// </summary>
    private string BuildFileDialogFilter()
    {
        string imagePatterns = string.Join(
            ";", _services.ImageInspector.SupportedExtensions.Select(e => $"*.{e}"));

        return
            $"Documents and images|*.pdf;{imagePatterns}|" +
            "PDF documents (*.pdf)|*.pdf|" +
            $"Images ({imagePatterns})|{imagePatterns}|" +
            "All files (*.*)|*.*";
    }

    private void ClearPdf()
    {
        ClearSelection();
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
        _studyDateTimeFromFile = null;

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
        StudyId = StudyId.Trim(),

        // Set only when the document came from a file whose creation time was read. There
        // is no field for it on the form: it is a property of the file rather than
        // something an operator should be inventing.
        StudyDateTime = _studyDateTimeFromFile
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
            FileSourceDocument(result, transmit);
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

            ReleaseMemoryAfterWork();
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

    /// <summary>
    /// Moves or renames a document picked up from the monitored folder, once it has been
    /// dealt with, so that it is not picked up a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only for a document the monitor produced. A file the operator chose by hand is
    /// theirs; moving it afterwards would be presumptuous and surprising.
    /// </para>
    /// <para>
    /// Governed by the same settings as reading the name, and off whenever those are off,
    /// so a site already using folder monitoring sees no change until it asks for one.
    /// </para>
    /// <para>
    /// A document the operator typed the details for is filed just the same. What matters
    /// is that it has been dealt with, not where its identifiers came from.
    /// </para>
    /// </remarks>
    private void FileSourceDocument(ConversionResult result, bool transmit)
    {
        if (!_cameFromMonitoredFolder || PdfPath is null)
        {
            return;
        }

        FilenameImportConfiguration settings = _services.ConfigurationService.Current.FilenameImport;

        if (!settings.IsEnabled)
        {
            return;
        }

        // Anything short of "it got where it was going" leaves the file to be dealt with
        // rather than filed as done.
        bool succeeded = result.DicomGenerated && (!transmit || result.AllTransmissionsSucceeded);

        FiledSourceFile filed = new ImportedFileFiler(_services.Logger)
            .File(PdfPath, settings, succeeded);

        if (!filed.Moved)
        {
            if (filed.Message.Length > 0)
            {
                // Said out loud, because the alternative is the same document arriving
                // again and nobody knowing why.
                ResultMessage = Append(ResultMessage, Localize("Step1.SourceNotFiled"));
            }

            return;
        }

        // The document now lives somewhere else, and the window should say where rather
        // than keep showing a path that no longer exists.
        PdfPath = filed.NewPath;
        _cameFromMonitoredFolder = false;
        OnPropertyChanged(nameof(SourceDescription));

        ResultMessage = Append(
            ResultMessage,
            _services.Localizer.Format("Step1.SourceFiled", DisplayLocationOf(filed.NewPath)));
    }

    /// <summary>The moved file's folder and name, which is all an operator needs to find it.</summary>
    private static string DisplayLocationOf(string path)
    {
        string? folder = Path.GetDirectoryName(path);

        return folder is null
            ? Path.GetFileName(path)
            : Path.Combine(Path.GetFileName(folder), Path.GetFileName(path));
    }

    private static string Append(string existing, string line) =>
        existing.Length == 0 ? line : existing + Environment.NewLine + line;

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

            ReleaseMemoryAfterWork();
        }
    }

    /// <summary>
    /// Hands the memory a conversion used back to the operating system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a superstition, and not left to the collector's own judgement, because what the
    /// collector optimises for and what somebody looking at Task Manager wants are not the
    /// same thing. A conversion allocates one multi megabyte array per rendered page.
    /// Arrays that size live on the large object heap, which is not compacted by default
    /// and is only swept on a generation two collection, so the pages of the report that
    /// has just been sent stay resident until something else forces the issue. Measured on
    /// a sixty page document: 246 MB resident after one conversion and 311 MB after two,
    /// against 106 MB when this runs.
    /// </para>
    /// <para>
    /// Called at the end of a conversion, where the work is finished, the window is about
    /// to go idle and nobody is waiting on anything. The collection is blocking and
    /// compacting, which is the point, and it was measured at 17 to 35 milliseconds for
    /// documents from one to sixty pages: not perceptible where it is called, and not
    /// something to call anywhere else.
    /// </para>
    /// </remarks>
    private void ReleaseMemoryAfterWork()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
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
            $"{Localize("Step1.FileName")}: {PdfInfo?.FileName ?? ImageInfo?.FileName}" + Environment.NewLine +
            (SourceKind == SourceDocumentKind.Image
                ? SourceDescription + Environment.NewLine
                : string.Empty) + Environment.NewLine +
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
                // The preview shows the first page, which is the only one whose tags
                // differ from the rest apart from Instance Number and SOP Instance UID.
                _lastBuiltObject = SourceKind == SourceDocumentKind.Image
                    ? _services.ImageBuilder.Build(PdfPath, BuildDemographics(), null, ImageInfo).First()
                    : _services.Builder.Build(PdfPath, BuildDemographics(), PdfInfo);

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

    /// <summary>
    /// True when a worklist has been configured, which decides whether the button is
    /// offered at all. A button that always fails is worse than no button.
    /// </summary>
    public bool IsWorklistAvailable =>
        _services.ConfigurationService.Current.IsWorklistAvailable;

    /// <summary>
    /// Opens the worklist and, when an entry is chosen, fills the patient fields from it.
    /// </summary>
    /// <remarks>
    /// Everything the worklist supplies replaces what is on the form. Merging the two would
    /// leave a half typed name beside an identifier from somewhere else, which is exactly
    /// the state that puts a document on the wrong patient.
    /// </remarks>
    private void OpenWorklist()
    {
        var viewModel = new WorklistViewModel(_services);
        var window = new WorklistWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        PatientDemographics? chosen = viewModel.BuildDemographics();

        if (chosen is null)
        {
            return;
        }

        PatientId = chosen.PatientId;
        FamilyName = chosen.FamilyName;
        GivenName = chosen.GivenName;
        MiddleName = string.Empty;
        DateOfBirth = chosen.DateOfBirth;
        Sex = chosen.Sex;
        AccessionNumber = chosen.AccessionNumber;
        ReferringPhysician = chosen.ReferringPhysicianName;
        StudyInstanceUid = chosen.ExistingStudyInstanceUid;

        if (!string.IsNullOrWhiteSpace(chosen.StudyDescription))
        {
            StudyDescription = chosen.StudyDescription;
        }

        _services.Logger.Information(
            $"Patient details were taken from the worklist for '{chosen.PatientId}', " +
            $"accession '{chosen.AccessionNumber}'.");
    }

    /// <summary>
    /// Opens the settings dialog, and says something useful if it cannot be opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is a global handler that catches anything reaching the interface thread, but
    /// what it can say is "Object reference not set to an instance of an object", which
    /// tells an operator nothing and tells them nothing about what to do.
    /// </para>
    /// <para>
    /// This dialog is worth a case of its own because it is the one whose failure locks
    /// the operator out of every other setting, including the ones they would need to
    /// change to fix it. A build of 3.3.0 did exactly that to a site that had a worklist
    /// configured, and the only way out was to work out for themselves that deleting the
    /// settings file would help. The message now names the file and says that.
    /// </para>
    /// </remarks>
    private void OpenSettings()
    {
        SettingsViewModel viewModel;

        try
        {
            viewModel = new SettingsViewModel(_services);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("The settings dialog could not be prepared.", ex);

            MessageBox.Show(
                _services.Localizer.Format(
                    "Settings.CouldNotOpen",
                    _services.ConfigurationService.ConfigurationFilePath,
                    ex.Message),
                Localize("App.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return;
        }

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

            // Switching the worklist on has to show the button now, not after a restart.
            // Whoever has just configured it will try it immediately, and a button that
            // is not there reads as the setting having failed.
            OnPropertyChanged(nameof(IsWorklistAvailable));
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
            $"{_services.Localizer.Format("About.Version", App.ProductVersion)}{Environment.NewLine}" +
            $"{Localize("About.Copyright")}{Environment.NewLine}{Environment.NewLine}" +
            $"{Localize("About.Licence")}{Environment.NewLine}{Environment.NewLine}" +
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

            // Only when the file was actually accepted. LoadPdf clears the path again when
            // it rejects the file, and filing something that was never converted would move
            // it out of sight for no reason.
            if (PdfPath is null)
            {
                return;
            }

            _cameFromMonitoredFolder = true;
            StatusMessage = Localize("Step1.MonitoredFolderPickup");

            FillFromFilename(path);
        });
    }

    /// <summary>
    /// Fills the patient fields from the file's own name, where a pattern reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of the whole feature: a document arriving from another system usually has
    /// the identifiers in its name already, and retyping them is where a report ends up
    /// against the wrong patient.
    /// </para>
    /// <para>
    /// <b>It fills the form; it does not send.</b> Confirmation before sending is untouched,
    /// so the operator still sees what is about to go and against whom. A file name is not
    /// a trustworthy source of identity, since anybody can rename a file, and the one place
    /// this application must not take a shortcut is the step that decides whose record a
    /// document joins.
    /// </para>
    /// <para>
    /// A name that matches nothing leaves the form exactly as it was, which is the
    /// behaviour of every version before this one.
    /// </para>
    /// </remarks>
    private void FillFromFilename(string path)
    {
        FilenameImportConfiguration settings = _services.ConfigurationService.Current.FilenameImport;

        if (!settings.IsEnabled)
        {
            return;
        }

        var reader = new FilenameMetadataReader(settings);

        DateTime? studyDateTime = null;

        if (settings.UseFileDateAsStudyDate)
        {
            try
            {
                studyDateTime = File.GetCreationTime(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The study date then stays as the moment of conversion, exactly as it was
                // before this feature existed. Not worth refusing a document over.
                _services.Logger.Warning(
                    $"The creation time of '{path}' could not be read: {ex.Message}");
            }
        }

        FilenameReadResult read = reader.Read(path, studyDateTime);

        foreach (string note in read.Notes)
        {
            _services.Logger.Information($"Reading '{Path.GetFileName(path)}': {note}");
        }

        if (!read.IsMatch || read.Demographics is null)
        {
            return;
        }

        PatientDemographics d = read.Demographics;

        PatientId = d.PatientId;
        FamilyName = d.FamilyName;
        GivenName = d.GivenName;
        MiddleName = string.Empty;
        DateOfBirth = d.DateOfBirth;
        Sex = d.Sex;
        AccessionNumber = d.AccessionNumber;
        _studyDateTimeFromFile = d.StudyDateTime;

        if (d.StudyDescription.Length > 0)
        {
            StudyDescription = d.StudyDescription;
        }

        _services.AuditLogger.PatientInformationReadFromFilename(
            Path.GetFileName(path), read.Pattern);

        StatusMessage = Localize("Step1.ReadFromFilename");
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

        BrowseCommand?.RaiseCanExecuteChanged();
        ClearPdfCommand?.RaiseCanExecuteChanged();
        ClearFormCommand?.RaiseCanExecuteChanged();
        GenerateAndSendCommand?.RaiseCanExecuteChanged();
        GenerateOnlyCommand?.RaiseCanExecuteChanged();
        CancelCommand?.RaiseCanExecuteChanged();
        RetryCommand?.RaiseCanExecuteChanged();
        TestConnectionCommand?.RaiseCanExecuteChanged();
        ViewTagsCommand?.RaiseCanExecuteChanged();
        OpenSettingsCommand?.RaiseCanExecuteChanged();
        ExportLogsCommand?.RaiseCanExecuteChanged();
        SaveTemplateCommand?.RaiseCanExecuteChanged();
    }

    private string Localize(string key) => _services.Localizer.Get(key);

    // ---------------------------------------------------------------- Closing down

    /// <summary>
    /// Asks the window to close, which runs the confirmation and the shutdown.
    /// </summary>
    /// <remarks>
    /// Deliberately does not shut anything down itself. Closing the window runs
    /// <c>MainWindow.OnClosing</c>, which asks <see cref="ConfirmExit"/> and then disposes
    /// this view model; going straight to <c>Application.Shutdown</c> would skip that and
    /// leave two different ways of leaving the application behaving differently. The
    /// window's own close button has to work too, so there is one path and the button
    /// takes it.
    /// </remarks>
    private void RequestExit() => Application.Current?.MainWindow?.Close();

    /// <summary>
    /// Confirms the operator means to leave, and stops anything in progress if they do.
    /// </summary>
    /// <returns>True to proceed with closing, false to stay open.</returns>
    /// <remarks>
    /// <para>
    /// Asked whether or not work is in progress, because this application is left open all
    /// day on a shared workstation and a stray click on a door icon should not discard a
    /// form somebody has just filled in.
    /// </para>
    /// <para>
    /// When something IS in progress the question is a different one and says so: a
    /// transmission may be part way through, and the operator needs to know that stopping
    /// now may leave a document that the archive has not accepted. Nothing is deleted
    /// either way, so the object can be sent again from the output folder.
    /// </para>
    /// <para>
    /// Cancellation is co-operative and is requested, not waited for. Blocking the
    /// interface until a network operation notices would mean a window that will not close
    /// for up to the association timeout, which is the behaviour somebody pressing an exit
    /// button is trying to get away from. The process then exits, which ends the work
    /// whether or not it noticed.
    /// </para>
    /// </remarks>
    public bool ConfirmExit()
    {
        if (_exitConfirmed)
        {
            // Closing twice happens: pressing the door while the window is already closing,
            // or Windows shutting down behind a confirmation that was already given.
            return true;
        }

        bool busy = IsBusy;

        MessageBoxResult answer = MessageBox.Show(
            Localize(busy ? "Exit.ConfirmBusy" : "Exit.Confirm"),
            Localize("App.Title"),
            MessageBoxButton.YesNo,
            busy ? MessageBoxImage.Warning : MessageBoxImage.Question,
            busy ? MessageBoxResult.No : MessageBoxResult.Yes);

        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        _exitConfirmed = true;

        if (busy)
        {
            _services.Logger.Information("The operator closed the application while work was in progress.");

            try
            {
                _cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The work finished between the question and the answer. Nothing to stop.
            }
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Everything released here is something the process would release anyway when it
    /// exits. It is done explicitly all the same: the folder monitor holds a
    /// FileSystemWatcher on a directory, the logger has buffered lines worth flushing, and
    /// the generated object can be tens of megabytes. Releasing them in order, rather than
    /// relying on process teardown, is also what makes the application closable while a
    /// conversion is running.
    /// </remarks>
    public void Dispose()
    {
        _services.FolderMonitor.PdfDetected -= OnMonitoredPdfDetected;
        _services.ConfigurationService.ConfigurationChanged -= OnConfigurationChanged;

        // Any work still running is told to stop before its resources go.
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cancellation?.Dispose();
        _cancellation = null;

        // The largest thing this view model holds: a whole DICOM object, kept so the tag
        // viewer and the retry action work without re-reading it from disk.
        _lastGeneratedFile = null;

        TransmissionResults.Clear();
        ValidationIssues.Clear();

        _services.Logger.Flush();
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
