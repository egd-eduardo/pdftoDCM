using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Localization;
using PdfToDicom.Core.Logging;
using PdfToDicom.Core.Monitoring;
using PdfToDicom.Core.Networking;
using PdfToDicom.Core.Imaging;
using PdfToDicom.Core.Pdf;

namespace PdfToDicom.Core.Services;

/// <summary>
/// Composition root for the application.
/// </summary>
/// <remarks>
/// <para>
/// The object graph is assembled by hand rather than by a container. The graph is small,
/// fixed and constructed exactly once, so a container would add a dependency and a layer
/// of indirection without buying anything. Doing it here keeps the desktop application
/// and the command line tool provably identical in behaviour, because both obtain their
/// services from this one place.
/// </para>
/// <para>
/// The instance owns the logger, so it must be disposed when the host shuts down.
/// </para>
/// </remarks>
public sealed class ApplicationServices : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Builds the object graph.
    /// </summary>
    /// <param name="configurationFilePath">
    /// Optional override for the configuration file location. Used by the tests and by
    /// the command line tool's --config switch.
    /// </param>
    /// <param name="verboseLogging">Include debug level entries in the log.</param>
    public ApplicationServices(string? configurationFilePath = null, bool verboseLogging = false)
    {
        DicomRuntime.EnsureInitialized();

        ConfigurationService = new ConfigurationService(configurationFilePath);
        AppConfiguration configuration = ConfigurationService.Load();

        AppLogger = new AppLogger(configuration.LogFolder, configuration.LogRetentionDays, verboseLogging);
        Logger = AppLogger;

        AuditLogger = new AuditLogger(Path.Combine(configuration.LogFolder, "Audit"), Logger);
        LogExporter = new LogExporter(Logger);
        Localizer = new Localizer(configuration.Language);

        PdfInspector = new PdfInspector(Logger);
        ImageInspector = new ImageInspector(Logger);
        PdfRenderer = new PdfPageRenderer(Logger);
        FileWriter = new DicomFileWriter(Logger);
        // Dispatches on the SOP Class the object declares, so one validator serves both
        // the encapsulated documents and the images.
        DicomValidator = new SopClassDicomValidator();
        NetworkService = new DicomNetworkService(Logger);
        TagInspector = new DicomTagInspector();
        FolderMonitor = new FolderMonitor(Logger, configuration.MonitorSettleDelayMilliseconds);

        RebuildConfigurationDependentServices(configuration);

        // The builder depends on values the administrator can change at runtime, so it is
        // rebuilt whenever the configuration is saved.
        ConfigurationService.ConfigurationChanged += (_, updated) => RebuildConfigurationDependentServices(updated);
    }

    /// <summary>Configuration store.</summary>
    public ConfigurationService ConfigurationService { get; }

    /// <summary>Diagnostic logger, typed so the log viewer can subscribe to new entries.</summary>
    public AppLogger AppLogger { get; }

    /// <summary>Diagnostic logger.</summary>
    public IAppLogger Logger { get; }

    /// <summary>Audit trail.</summary>
    public IAuditLogger AuditLogger { get; }

    /// <summary>Support bundle exporter.</summary>
    public LogExporter LogExporter { get; }

    /// <summary>User interface translations.</summary>
    public ILocalizer Localizer { get; }

    /// <summary>PDF reader.</summary>
    public IPdfInspector PdfInspector { get; }

    /// <summary>Reads image files and decides what kind of file the operator selected.</summary>
    public IImageInspector ImageInspector { get; }

    /// <summary>Renders PDF pages to images, so a viewer that needs one can open the study.</summary>
    public IPdfPageRenderer PdfRenderer { get; }

    /// <summary>Writer for generated objects.</summary>
    public DicomFileWriter FileWriter { get; }

    /// <summary>IOD validator.</summary>
    public IDicomValidator DicomValidator { get; }

    /// <summary>Verification and storage service class user.</summary>
    public IDicomNetworkService NetworkService { get; }

    /// <summary>Tag viewer support.</summary>
    public DicomTagInspector TagInspector { get; }

    /// <summary>Monitored folder watcher.</summary>
    public FolderMonitor FolderMonitor { get; }

    /// <summary>UID source, rebuilt when the UID settings change.</summary>
    public IUidGenerator UidGenerator { get; private set; } = null!;

    /// <summary>DICOM object builder, rebuilt when the DICOM settings change.</summary>
    public IDicomObjectBuilder Builder { get; private set; } = null!;

    /// <summary>Builds Secondary Capture objects from images.</summary>
    public IImageDicomObjectBuilder ImageBuilder { get; private set; } = null!;

    /// <summary>Decides which study several converted files share.</summary>
    public StudyGroupingService StudyGrouping { get; } = new();

    /// <summary>Workflow orchestrator, rebuilt when the DICOM settings change.</summary>
    public ConversionWorkflowService Workflow { get; private set; } = null!;

    /// <summary>
    /// Rebuilds the services that capture configuration values at construction time.
    /// </summary>
    /// <remarks>
    /// If the administrator has entered a UID root that cannot be used, the application
    /// falls back to the UUID derived strategy and records why, rather than refusing to
    /// start. An operator locked out of the application by a settings mistake is a far
    /// worse outcome than one whose UIDs are not under the expected root.
    /// </remarks>
    private void RebuildConfigurationDependentServices(AppConfiguration configuration)
    {
        try
        {
            UidGenerator = Dicom.UidGenerator.FromConfiguration(configuration);
        }
        catch (ArgumentException ex)
        {
            Logger.Error(
                "The configured UID root cannot be used, so identifiers will be derived from a UUID instead. " +
                ex.Message,
                ex);

            UidGenerator = new UidGenerator(UidGenerationStrategy.UuidDerived);
        }

        Builder = new EncapsulatedPdfBuilder(configuration, UidGenerator, PdfInspector, Logger);
        ImageBuilder = new SecondaryCaptureImageBuilder(configuration, UidGenerator, ImageInspector, Logger);

        Workflow = new ConversionWorkflowService(
            ConfigurationService,
            PdfInspector,
            ImageInspector,
            Builder,
            ImageBuilder,
            PdfRenderer,
            DicomValidator,
            NetworkService,
            FileWriter,
            Logger,
            AuditLogger,
            StudyGrouping);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        FolderMonitor.Dispose();
        AppLogger.Dispose();
    }
}
