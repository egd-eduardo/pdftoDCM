using FellowOakDicom;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
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
public sealed record ConversionOptions(
    bool Transmit = true,
    IReadOnlyList<PacsDestination>? Destinations = null);

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
    private readonly IDicomObjectBuilder _builder;
    private readonly IDicomValidator _dicomValidator;
    private readonly IDicomNetworkService _networkService;
    private readonly DicomFileWriter _fileWriter;
    private readonly IAppLogger _logger;
    private readonly IAuditLogger _auditLogger;
    private readonly ILicenceService? _licenceService;
    private readonly PatientDemographicsValidator _demographicsValidator;
    private readonly PacsDestinationValidator _destinationValidator;

    /// <summary>Creates the workflow service.</summary>
    /// <param name="licenceService">
    /// Optional. When supplied, transmission is withheld from a copy whose licence is
    /// missing, forged, registered to another machine, or long expired. Conversion and
    /// local saving are never withheld: a clinician must always be able to produce the
    /// document, whatever the commercial position. When null, or when the build carries no
    /// publisher key, nothing is withheld at all.
    /// </param>
    public ConversionWorkflowService(
        IConfigurationService configurationService,
        IPdfInspector pdfInspector,
        IDicomObjectBuilder builder,
        IDicomValidator dicomValidator,
        IDicomNetworkService networkService,
        DicomFileWriter fileWriter,
        IAppLogger logger,
        IAuditLogger auditLogger,
        ILicenceService? licenceService = null)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _pdfInspector = pdfInspector ?? throw new ArgumentNullException(nameof(pdfInspector));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _dicomValidator = dicomValidator ?? throw new ArgumentNullException(nameof(dicomValidator));
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _licenceService = licenceService;

        _demographicsValidator = new PatientDemographicsValidator();
        _destinationValidator = new PacsDestinationValidator();
    }

    /// <summary>
    /// Validates everything that must be correct before a conversion is attempted.
    /// </summary>
    /// <remarks>
    /// Exposed separately so that the user interface can enable or disable the action
    /// button as the operator types, using exactly the rules the workflow will apply.
    /// </remarks>
    public ValidationResult ValidateInputs(
        string? pdfPath,
        PatientDemographics demographics,
        bool requireDestination)
    {
        ArgumentNullException.ThrowIfNull(demographics);

        AppConfiguration configuration = _configurationService.Current;
        var result = new ValidationResult();

        result.Merge(_pdfInspector.Validate(pdfPath));
        result.Merge(_demographicsValidator.Validate(demographics, configuration.CharacterSetSupportsUnicode));

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

        // ---------------------------------------------------------- 0. Licence
        // A licence problem downgrades the run to "generate and save" rather than failing
        // it. The document is still produced, so the operator is never left unable to do
        // clinical work; only the transmission is withheld, and the reason is stated.
        string? licenceRefusal = DescribeLicenceRefusal(configuration);
        bool transmit = options.Transmit && licenceRefusal is null;

        if (licenceRefusal is not null && options.Transmit)
        {
            _logger.Warning($"Transmission was withheld because of the licence: {licenceRefusal}");
        }

        IReadOnlyList<PacsDestination> destinations = transmit
            ? options.Destinations ?? configuration.EnabledDestinations
            : Array.Empty<PacsDestination>();

        // ---------------------------------------------------------- 1. Validate inputs
        ValidationResult inputValidation = ValidateInputs(pdfPath, demographics, transmit);

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

        // ---------------------------------------------------------- 2. Build the object
        PdfDocumentInfo pdfInfo = _pdfInspector.Inspect(pdfPath);
        _auditLogger.PdfSelected(pdfInfo);
        _auditLogger.PatientInformationEntered(demographics);

        BuiltDicomObject built;
        try
        {
            built = _builder.Build(pdfPath, demographics, pdfInfo);
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

        // ---------------------------------------------------------- 3. Validate the object
        ValidationResult objectValidation = _dicomValidator.Validate(built.File);

        if (!objectValidation.IsValid)
        {
            // Transmitting an object known to be non conformant would place a defective
            // record in the patient's imaging history, so the workflow stops here.
            _logger.Error(
                "The generated object failed validation and was not transmitted: " +
                string.Join(" | ", objectValidation.Errors.Select(e => e.Message)));

            return new ConversionResult
            {
                DicomGenerated = false,
                StudyInstanceUid = built.StudyInstanceUid,
                SeriesInstanceUid = built.SeriesInstanceUid,
                SopInstanceUid = built.SopInstanceUid,
                ValidationIssues = objectValidation.Issues,
                Summary = "The generated DICOM object did not pass validation, so it was not transmitted."
            };
        }

        foreach (ValidationIssue warning in objectValidation.Warnings)
        {
            _logger.Warning($"DICOM validation warning: {warning.Message}");
        }

        // ---------------------------------------------------------- 4. Save the object
        string? savedPath = null;
        bool saveRequested = configuration.AlwaysSaveDicomLocally || !transmit;

        if (saveRequested)
        {
            try
            {
                savedPath = _fileWriter.Write(built.File, configuration.OutputFolder, built.SuggestedFileName);
            }
            catch (Exception ex)
            {
                // The transmission can still proceed from the in memory object, so a
                // failure to write locally is a warning rather than a fatal error.
                _logger.Error("The generated object could not be saved locally.", ex);
                objectValidation.AddWarning(
                    "Output", $"The DICOM file could not be saved to '{configuration.OutputFolder}': {ex.Message}");
            }
        }

        _auditLogger.DicomGenerated(
            built.StudyInstanceUid, built.SeriesInstanceUid, built.SopInstanceUid, savedPath);

        if (!transmit)
        {
            if (licenceRefusal is not null && options.Transmit)
            {
                objectValidation.AddWarning("Licence", licenceRefusal);
            }

            string outcome = savedPath is null
                ? "The DICOM object was generated but could not be saved."
                : $"The DICOM object was generated and saved to '{savedPath}'.";

            return new ConversionResult
            {
                DicomGenerated = true,
                GeneratedFilePath = savedPath,
                StudyInstanceUid = built.StudyInstanceUid,
                SeriesInstanceUid = built.SeriesInstanceUid,
                SopInstanceUid = built.SopInstanceUid,
                ValidationIssues = objectValidation.Issues,
                Summary = licenceRefusal is not null && options.Transmit
                    ? $"{outcome} It was not transmitted: {licenceRefusal}"
                    : outcome
            };
        }

        // ---------------------------------------------------------- 5. Transmit
        IReadOnlyList<TransmissionResult> transmissions = await _networkService
            .StoreToAllAsync(built.File, destinations, configuration.CallingAeTitle, progress, cancellationToken)
            .ConfigureAwait(false);

        foreach (TransmissionResult transmission in transmissions)
        {
            _auditLogger.TransmissionAttempted(transmission, built.SopInstanceUid);
        }

        // Persist the "last used" timestamps that the network service recorded, so the
        // recently used destination list survives a restart.
        TrySaveConfiguration();

        // ---------------------------------------------------------- 6. Clean up
        bool allSucceeded = transmissions.Count > 0 && transmissions.All(t => t.IsSuccess);
        bool deleted = false;

        if (allSucceeded &&
            configuration.AutoDeleteAfterSuccessfulTransmission &&
            savedPath is not null)
        {
            deleted = _fileWriter.TryDelete(savedPath);

            if (deleted)
            {
                _auditLogger.GeneratedFileDeleted(savedPath);
            }
        }

        return new ConversionResult
        {
            DicomGenerated = true,
            GeneratedFilePath = deleted ? null : savedPath,
            GeneratedFileDeleted = deleted,
            StudyInstanceUid = built.StudyInstanceUid,
            SeriesInstanceUid = built.SeriesInstanceUid,
            SopInstanceUid = built.SopInstanceUid,
            ValidationIssues = objectValidation.Issues,
            Transmissions = transmissions,
            Summary = BuildSummary(transmissions, savedPath, deleted)
        };
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

        // The retry action bypasses RunAsync, so the licence is checked here as well.
        // Without this, a copy that cannot transmit could still transmit by pressing Retry.
        string? licenceRefusal = DescribeLicenceRefusal(configuration);

        if (licenceRefusal is not null)
        {
            _logger.Warning($"The retry was withheld because of the licence: {licenceRefusal}");

            return targets.Select(target => new TransmissionResult
            {
                DestinationName = target.Name,
                CalledAeTitle = target.CalledAeTitle,
                Host = target.Host,
                Port = target.Port,
                Outcome = TransmissionOutcome.LocalError,
                Message = licenceRefusal
            }).ToList();
        }

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

    /// <summary>
    /// Returns the reason transmission must be withheld, or null when it may proceed.
    /// </summary>
    /// <remarks>
    /// Null is returned whenever there is any doubt: when no licence service was supplied,
    /// when the build carries no publisher key, and when the check itself faults. A
    /// licensing subsystem that stops a hospital sending a report because of a bug in the
    /// licensing subsystem is a worse failure than an unlicensed copy.
    /// </remarks>
    private string? DescribeLicenceRefusal(AppConfiguration configuration)
    {
        if (_licenceService is null || !_licenceService.IsEnforced)
        {
            return null;
        }

        try
        {
            LicenceStatus status = _licenceService.Check(configuration.LicenceCode);
            return status.MayTransmit ? null : status.Message;
        }
        catch (Exception ex)
        {
            _logger.Error("The licence could not be checked, so transmission was allowed.", ex);
            return null;
        }
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
