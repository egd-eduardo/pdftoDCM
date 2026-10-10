using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Imaging;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Services;

/// <summary>
/// Options that control a single run of the workflow.
/// </summary>
/// <param name="Transmit">
/// When false the object is generated and saved but no association is opened. Used by the
/// "Generate only" action and when troubleshooting.
/// </param>
/// <param name="Destinations">
/// Destinations to send to. When null, every enabled destination in the configuration is used.
/// </param>
/// <param name="Transmit">Whether the generated object is sent as well as built.</param>
/// <param name="Destinations">Where to send it, or null for every enabled destination.</param>
/// <param name="UnattendedImport">
/// True when the patient information came from a file name rather than from somebody
/// typing it.
/// </param>
/// <remarks>
/// <para>
/// <c>UnattendedImport</c> relaxes two rules, and only two: a date of birth and an
/// accession number may be absent. Both are Type 2 attributes, so an empty value is
/// conformant, and for an unattended import a document that cannot be filed at all is
/// worse than one filed without a date of birth. Everything else is validated exactly as
/// it is for a document somebody typed the details for, and a value that IS present is
/// checked just as strictly.
/// </para>
/// <para>
/// It does not relax the Patient ID or the name. Those are what identify the patient, and
/// a document filed against nobody in particular is the failure this application exists to
/// prevent.
/// </para>
/// </remarks>
public sealed record ConversionOptions(
    bool Transmit = true,
    IReadOnlyList<PacsDestination>? Destinations = null,
    bool UnattendedImport = false);

/// <summary>
/// Coordinates the complete convert and send workflow.
/// </summary>
/// <remarks>
/// This is the single place where the order of operations is defined: validate the
/// inputs, build the object, validate the object, save it, transmit it, then clean up.
/// The user interface and the command line tool both drive this service, which is what
/// guarantees they behave identically.
/// </remarks>
public sealed class ConversionWorkflowService
{
    private readonly IConfigurationService _configurationService;
    private readonly IPdfInspector _pdfInspector;
    private readonly IImageInspector _imageInspector;
    private readonly IDicomObjectBuilder _builder;
    private readonly IImageDicomObjectBuilder _imageBuilder;
    private readonly IPdfPageRenderer _pdfRenderer;
    private readonly StudyGroupingService _studyGrouping;
    private readonly IDicomValidator _dicomValidator;
    private readonly IDicomNetworkService _networkService;
    private readonly DicomFileWriter _fileWriter;
    private readonly IAppLogger _logger;
    private readonly IAuditLogger _auditLogger;
    private readonly PatientDemographicsValidator _demographicsValidator;
    private readonly PacsDestinationValidator _destinationValidator;

    /// <summary>Creates the workflow service.</summary>
    public ConversionWorkflowService(
        IConfigurationService configurationService,
        IPdfInspector pdfInspector,
        IImageInspector imageInspector,
        IDicomObjectBuilder builder,
        IImageDicomObjectBuilder imageBuilder,
        IPdfPageRenderer pdfRenderer,
        IDicomValidator dicomValidator,
        IDicomNetworkService networkService,
        DicomFileWriter fileWriter,
        IAppLogger logger,
        IAuditLogger auditLogger,
        StudyGroupingService? studyGrouping = null)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _pdfInspector = pdfInspector ?? throw new ArgumentNullException(nameof(pdfInspector));
        _imageInspector = imageInspector ?? throw new ArgumentNullException(nameof(imageInspector));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _imageBuilder = imageBuilder ?? throw new ArgumentNullException(nameof(imageBuilder));
        _pdfRenderer = pdfRenderer ?? throw new ArgumentNullException(nameof(pdfRenderer));
        _studyGrouping = studyGrouping ?? new StudyGroupingService();
        _dicomValidator = dicomValidator ?? throw new ArgumentNullException(nameof(dicomValidator));
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));

        _demographicsValidator = new PatientDemographicsValidator();
        _destinationValidator = new PacsDestinationValidator();
    }

    /// <summary>
    /// The study grouping in force, so the user interface can show which study files are
    /// currently joining and let the operator start a new one.
    /// </summary>
    public StudyGroupingService StudyGrouping => _studyGrouping;

    /// <summary>
    /// Decides what kind of file this is by reading its content, so the caller can show
    /// the right preview and the workflow can choose the right builder.
    /// </summary>
    public SourceDocumentKind DetectKind(string? sourcePath) => _imageInspector.DetectKind(sourcePath);

    /// <summary>
    /// Validates everything that must be correct before a conversion is attempted.
    /// </summary>
    /// <remarks>
    /// Exposed separately so that the user interface can enable or disable the action
    /// button as the operator types, using exactly the rules the workflow will apply.
    /// </remarks>
    /// <param name="unattendedImport">
    /// True when the values came from a file name rather than from somebody typing them,
    /// which allows an absent date of birth and accession number. See
    /// <see cref="ConversionOptions"/>.
    /// </param>
    public ValidationResult ValidateInputs(
        string? pdfPath,
        PatientDemographics demographics,
        bool requireDestination,
        bool unattendedImport = false)
    {
        ArgumentNullException.ThrowIfNull(demographics);

        AppConfiguration configuration = _configurationService.Current;
        var result = new ValidationResult();

        // Which validator applies depends on what the file actually is, not on what it is
        // called. A PDF renamed to .jpg is still a PDF and converts perfectly well.
        switch (DetectKind(pdfPath))
        {
            case SourceDocumentKind.Pdf:
                result.Merge(_pdfInspector.Validate(pdfPath));
                break;

            case SourceDocumentKind.Image:
                result.Merge(_imageInspector.Validate(pdfPath));
                break;

            default:
                // Falls back to the PDF inspector so that the familiar messages about a
                // missing or empty file are still produced. Only a file that exists and is
                // readable, but is neither a PDF nor an image, reaches the second branch.
                result.Merge(_pdfInspector.Validate(pdfPath));

                if (result.IsValid)
                {
                    result.AddError(
                        "PdfPath",
                        "The file is neither a PDF nor an image in a supported format. " +
                        "PDF, JPEG, PNG, TIFF, BMP, GIF, WebP and TGA can be converted.");
                }

                break;
        }

        result.Merge(_demographicsValidator.Validate(
            demographics,
            configuration.CharacterSetSupportsUnicode,
            requireDateOfBirth: !unattendedImport,
            requireAccessionNumber: !unattendedImport));

        if (requireDestination)
        {
            result.Merge(_destinationValidator.ValidateAll(configuration));
        }

        return result;
    }

    /// <summary>
    /// Runs the workflow.
    /// </summary>
    /// <param name="pdfPath">Absolute path of the PDF to convert.</param>
    /// <param name="demographics">Patient and order information.</param>
    /// <param name="options">Controls whether the object is transmitted and to where.</param>
    /// <param name="progress">Receives transmission progress updates.</param>
    /// <param name="cancellationToken">Cancels an in flight transmission.</param>
    public async Task<ConversionResult> RunAsync(
        string pdfPath,
        PatientDemographics demographics,
        ConversionOptions? options = null,
        IProgress<TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demographics);
        options ??= new ConversionOptions();

        AppConfiguration configuration = _configurationService.Current;

        IReadOnlyList<PacsDestination> destinations = options.Transmit
            ? options.Destinations ?? configuration.EnabledDestinations
            : Array.Empty<PacsDestination>();

        // ---------------------------------------------------------- 1. Validate inputs
        ValidationResult inputValidation = ValidateInputs(
            pdfPath, demographics, options.Transmit, options.UnattendedImport);

        if (!inputValidation.IsValid)
        {
            _logger.Warning(
                "The conversion was refused because the information supplied is not valid: " +
                string.Join(" | ", inputValidation.Errors.Select(e => e.Message)));

            return new ConversionResult
            {
                DicomGenerated = false,
                ValidationIssues = inputValidation.Issues,
                Summary = "The information supplied is not complete or not valid, so nothing was generated."
            };
        }

        foreach (ValidationIssue warning in inputValidation.Warnings)
        {
            _logger.Warning($"Validation warning: {warning.Message}");
        }

        // ---------------------------------------------------------- 2. Build the object(s)
        SourceDocumentKind kind = DetectKind(pdfPath);

        // Grouping asks for the study the previous file for this patient used. It returns
        // null when grouping is off, when the patient has changed, or for the first file.
        string? groupedStudyUid = _studyGrouping.ResolveStudyInstanceUid(
            configuration.StudyGrouping, demographics.PatientId);

        IReadOnlyList<BuiltDicomObject> built;
        try
        {
            built = kind == SourceDocumentKind.Image
                ? BuildImageObjects(pdfPath!, demographics, groupedStudyUid)
                : BuildPdfObject(pdfPath!, demographics, groupedStudyUid);
        }
        catch (Exception ex)
        {
            _logger.Error("The DICOM object could not be generated.", ex);

            return new ConversionResult
            {
                DicomGenerated = false,
                ValidationIssues = new[]
                {
                    ValidationIssue.Error("Generation", $"The DICOM object could not be generated: {ex.Message}")
                },
                Summary = "The DICOM object could not be generated."
            };
        }

        if (built.Count == 0)
        {
            return new ConversionResult
            {
                DicomGenerated = false,
                ValidationIssues = new[]
                {
                    ValidationIssue.Error("Generation", "The file produced no pages to convert.")
                },
                Summary = "The file produced no pages to convert."
            };
        }

        // Recorded from what the builder actually used, which may be a Study Instance UID
        // the operator typed rather than the one grouping suggested.
        _studyGrouping.Remember(configuration.StudyGrouping, demographics.PatientId, built[0].StudyInstanceUid);

        // ---------------------------------------------------------- 3. Validate the objects
        var objectIssues = new ValidationResult();

        foreach (BuiltDicomObject candidate in built)
        {
            ValidationResult objectValidation = _dicomValidator.Validate(candidate.File);
            objectIssues.Merge(objectValidation);
        }

        if (!objectIssues.IsValid)
        {
            // Transmitting an object known to be non conformant would place a defective
            // record in the patient's imaging history, so the workflow stops here. Nothing
            // is sent, not even the pages that passed, because half a document in the
            // archive is worse than none.
            _logger.Error(
                "The generated object failed validation and was not transmitted: " +
                string.Join(" | ", objectIssues.Errors.Select(e => e.Message)));

            return new ConversionResult
            {
                DicomGenerated = false,
                StudyInstanceUid = built[0].StudyInstanceUid,
                SeriesInstanceUid = built[0].SeriesInstanceUid,
                SopInstanceUid = built[0].SopInstanceUid,
                SopInstanceUids = built.Select(b => b.SopInstanceUid).ToArray(),
                ValidationIssues = objectIssues.Issues,
                Summary = built.Count == 1
                    ? "The generated DICOM object did not pass validation, so it was not transmitted."
                    : "One or more of the generated DICOM objects did not pass validation, so none were transmitted."
            };
        }

        foreach (ValidationIssue warning in objectIssues.Warnings)
        {
            _logger.Warning($"DICOM validation warning: {warning.Message}");
        }

        // ---------------------------------------------------------- 4. Save the objects
        bool saveRequested = configuration.AlwaysSaveDicomLocally || !options.Transmit;
        var savedPaths = new List<string>(built.Count);

        foreach (BuiltDicomObject candidate in built)
        {
            string? savedPath = null;

            if (saveRequested)
            {
                try
                {
                    savedPath = _fileWriter.Write(candidate.File, configuration.OutputFolder, candidate.SuggestedFileName);
                }
                catch (Exception ex)
                {
                    // The transmission can still proceed from the in memory object, so a
                    // failure to write locally is a warning rather than a fatal error.
                    _logger.Error("The generated object could not be saved locally.", ex);
                    objectIssues.AddWarning(
                        "Output", $"The DICOM file could not be saved to '{configuration.OutputFolder}': {ex.Message}");
                }
            }

            if (savedPath is not null)
            {
                savedPaths.Add(savedPath);
            }

            _auditLogger.DicomGenerated(
                candidate.StudyInstanceUid, candidate.SeriesInstanceUid, candidate.SopInstanceUid, savedPath);
        }

        string? firstSavedPath = savedPaths.FirstOrDefault();

        if (!options.Transmit)
        {
            return new ConversionResult
            {
                DicomGenerated = true,
                GeneratedFilePath = firstSavedPath,
                GeneratedFilePaths = savedPaths,
                StudyInstanceUid = built[0].StudyInstanceUid,
                SeriesInstanceUid = built[0].SeriesInstanceUid,
                SopInstanceUid = built[0].SopInstanceUid,
                SopInstanceUids = built.Select(b => b.SopInstanceUid).ToArray(),
                ValidationIssues = objectIssues.Issues,
                Summary = firstSavedPath is null
                    ? "The DICOM object was generated but could not be saved."
                    : built.Count == 1
                        ? $"The DICOM object was generated and saved to '{firstSavedPath}'."
                        : $"{built.Count} DICOM objects were generated and saved to '{configuration.OutputFolder}'."
            };
        }

        // ---------------------------------------------------------- 5. Transmit
        var transmissions = new List<TransmissionResult>();

        foreach (BuiltDicomObject candidate in built)
        {
            IReadOnlyList<TransmissionResult> attempts = await _networkService
                .StoreToAllAsync(candidate.File, destinations, configuration.CallingAeTitle, progress, cancellationToken)
                .ConfigureAwait(false);

            foreach (TransmissionResult transmission in attempts)
            {
                _auditLogger.TransmissionAttempted(transmission, candidate.SopInstanceUid);
            }

            transmissions.AddRange(attempts);

            // A failure on one page means the rest of the document would land in the
            // archive without it. Stopping leaves less to reconcile than carrying on.
            if (attempts.Count > 0 && !attempts.All(t => t.IsSuccess))
            {
                break;
            }
        }

        // Persist the "last used" timestamps that the network service recorded, so the
        // recently used destination list survives a restart.
        TrySaveConfiguration();

        // ---------------------------------------------------------- 6. Clean up
        bool allSucceeded = transmissions.Count > 0 && transmissions.All(t => t.IsSuccess);
        bool deleted = false;

        if (allSucceeded && configuration.AutoDeleteAfterSuccessfulTransmission && savedPaths.Count > 0)
        {
            deleted = true;

            foreach (string path in savedPaths)
            {
                if (_fileWriter.TryDelete(path))
                {
                    _auditLogger.GeneratedFileDeleted(path);
                }
                else
                {
                    deleted = false;
                }
            }
        }

        return new ConversionResult
        {
            DicomGenerated = true,
            GeneratedFilePath = deleted ? null : firstSavedPath,
            GeneratedFilePaths = deleted ? Array.Empty<string>() : savedPaths,
            GeneratedFileDeleted = deleted,
            StudyInstanceUid = built[0].StudyInstanceUid,
            SeriesInstanceUid = built[0].SeriesInstanceUid,
            SopInstanceUid = built[0].SopInstanceUid,
            SopInstanceUids = built.Select(b => b.SopInstanceUid).ToArray(),
            ValidationIssues = objectIssues.Issues,
            Transmissions = transmissions,
            Summary = BuildSummary(transmissions, firstSavedPath, deleted)
        };
    }

    /// <summary>Builds the single object that a PDF produces.</summary>
    private IReadOnlyList<BuiltDicomObject> BuildPdfObject(
        string pdfPath,
        PatientDemographics demographics,
        string? groupedStudyUid)
    {
        PdfDocumentInfo pdfInfo = _pdfInspector.Inspect(pdfPath);
        _auditLogger.PdfSelected(pdfInfo);
        _auditLogger.PatientInformationEntered(demographics);

        // The PDF builder takes the study to join through the demographics, which is also
        // how an operator supplied identifier reaches it. A copy is used so that grouping
        // never writes into the caller's object.
        PatientDemographics forBuild = demographics;

        if (!string.IsNullOrWhiteSpace(groupedStudyUid)
            && string.IsNullOrWhiteSpace(demographics.ExistingStudyInstanceUid))
        {
            forBuild = demographics.Clone();
            forBuild.ExistingStudyInstanceUid = groupedStudyUid;
        }

        BuiltDicomObject document = _builder.Build(pdfPath, forBuild, pdfInfo);

        IReadOnlyList<BuiltDicomObject> pageImages =
            BuildRenderedPageImages(pdfPath, pdfInfo, forBuild, document.StudyInstanceUid);

        if (pageImages.Count == 0)
        {
            return new[] { document };
        }

        // The document first, so that the object carrying the authoritative content is the
        // one a caller reporting a single identifier reports.
        var all = new List<BuiltDicomObject>(pageImages.Count + 1) { document };
        all.AddRange(pageImages);

        return all;
    }

    /// <summary>
    /// Renders the pages of the PDF into Secondary Capture images in their own series
    /// within the same study, when the site has that switched on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Many PACS viewers will only open a study that contains an image. An Encapsulated
    /// PDF is not an image SOP Class, so at such a site the viewer refuses to launch and
    /// the report cannot be read, however correct the object is. Sending the rendered
    /// pages alongside it makes the report readable on screen while the document itself
    /// stays for fidelity, text selection and printing.
    /// </para>
    /// <para>
    /// Every failure is non fatal and returns an empty list. Losing the page images leaves
    /// a correct encapsulated document; failing the conversion would lose the report.
    /// </para>
    /// </remarks>
    private IReadOnlyList<BuiltDicomObject> BuildRenderedPageImages(
        string pdfPath,
        PdfDocumentInfo pdfInfo,
        PatientDemographics demographics,
        string studyInstanceUid)
    {
        AppConfiguration configuration = _configurationService.Current;

        if (!configuration.RenderPdfPagesAsImages)
        {
            return Array.Empty<BuiltDicomObject>();
        }

        try
        {
            IReadOnlyList<RenderedPdfPage> pages =
                _pdfRenderer.Render(pdfPath, configuration.PdfRenderDotsPerInch);

            if (pages.Count == 0)
            {
                return Array.Empty<BuiltDicomObject>();
            }

            long bytes = pages.Sum(p => p.Pixels.LongLength);

            _logger.Information(
                $"Rendered {pages.Count} page(s) of '{pdfInfo.FileName}' at " +
                $"{configuration.PdfRenderDotsPerInch} DPI, {bytes / (1024 * 1024)} MB of pixel data.");

            return _imageBuilder.BuildFromRenderedPages(
                pages, demographics, studyInstanceUid, pdfInfo.FileName);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                $"The pages of '{pdfInfo.FileName}' could not be turned into images: {ex.Message}. " +
                "The encapsulated PDF is unaffected and will still be sent.");

            return Array.Empty<BuiltDicomObject>();
        }
    }

    /// <summary>Builds one object per page of an image.</summary>
    private IReadOnlyList<BuiltDicomObject> BuildImageObjects(
        string imagePath,
        PatientDemographics demographics,
        string? groupedStudyUid)
    {
        ImageDocumentInfo imageInfo = _imageInspector.Inspect(imagePath);
        _auditLogger.PatientInformationEntered(demographics);

        _logger.Information(
            $"Converting image '{imageInfo.FileName}' ({imageInfo.FormatName}, {imageInfo.PixelSummary}, " +
            $"{imageInfo.FormattedSize}).");

        return _imageBuilder.Build(imagePath, demographics, groupedStudyUid, imageInfo);
    }

    /// <summary>
    /// Retransmits an object that was generated earlier, used by the retry action after a
    /// transmission failure so the operator does not have to re-enter anything.
    /// </summary>
    public async Task<IReadOnlyList<TransmissionResult>> RetryTransmissionAsync(
        DicomFile file,
        string sopInstanceUid,
        IReadOnlyList<PacsDestination>? destinations = null,
        IProgress<TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        AppConfiguration configuration = _configurationService.Current;
        IReadOnlyList<PacsDestination> targets = destinations ?? configuration.EnabledDestinations;

        _logger.Information($"Retrying the transmission of {sopInstanceUid} to {targets.Count} destination(s).");

        IReadOnlyList<TransmissionResult> results = await _networkService
            .StoreToAllAsync(file, targets, configuration.CallingAeTitle, progress, cancellationToken)
            .ConfigureAwait(false);

        foreach (TransmissionResult result in results)
        {
            _auditLogger.TransmissionAttempted(result, sopInstanceUid);
        }

        TrySaveConfiguration();
        return results;
    }

    /// <summary>Builds the operator facing summary of the run.</summary>
    private static string BuildSummary(
        IReadOnlyList<TransmissionResult> transmissions,
        string? savedPath,
        bool deleted)
    {
        int succeeded = transmissions.Count(t => t.IsSuccess);
        int total = transmissions.Count;

        string transmissionPart = succeeded switch
        {
            0 when total == 0 => "The object was generated but no destination was configured.",
            0 => $"The document was not accepted by any of the {total} destination(s).",
            _ when succeeded == total && total == 1 =>
                $"The document was accepted by '{transmissions[0].DestinationName}'.",
            _ when succeeded == total => $"The document was accepted by all {total} destinations.",
            _ => $"The document was accepted by {succeeded} of {total} destinations."
        };

        string filePart = deleted
            ? " The local copy was deleted as configured."
            : savedPath is not null
                ? $" A copy is retained at '{savedPath}'."
                : string.Empty;

        return transmissionPart + filePart;
    }

    /// <summary>
    /// Saves the configuration, swallowing a failure. Losing the "last used" timestamp is
    /// inconsequential next to a clinical document that has already been delivered.
    /// </summary>
    private void TrySaveConfiguration()
    {
        try
        {
            _configurationService.Save();
        }
        catch (Exception ex)
        {
            _logger.Warning($"The configuration could not be saved after the transmission: {ex.Message}");
        }
    }
}
