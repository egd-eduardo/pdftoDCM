using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Interop.Hl7V2.Dispatch;
using PdfToDicom.Interop.Hl7V2.Transport;
using PdfToDicom.Interop.Transactions;

namespace PdfToDicom.Integration.Configuration;

/// <summary>
/// Everything the application needs to convert a document and announce it, assembled from
/// the configuration.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the application holds one object rather than six. It also keeps the
/// assembly boundary honest: <c>ApplicationServices</c> lives in Core, which knows nothing
/// about HL7, so the HL7 half cannot be assembled there and is assembled here instead.
/// </para>
/// <para>
/// Nothing is created eagerly. A site with no interface configured, which is most of them,
/// builds no dispatcher, opens no transaction store and touches no folder.
/// </para>
/// </remarks>
public sealed class Hl7Announcer
{
    private readonly ConversionWorkflowService _workflow;
    private readonly IAppLogger _logger;
    private readonly string _transactionFolder;

    /// <summary>Creates the announcer.</summary>
    /// <param name="workflow">The conversion workflow, called exactly as the front ends call it.</param>
    /// <param name="logger">Where the HL7 side writes.</param>
    /// <param name="transactionFolder">Where the attempts are written down.</param>
    /// <param name="settings">
    /// The settings service, or a new one reading the standard location.
    /// </param>
    public Hl7Announcer(
        ConversionWorkflowService workflow,
        IAppLogger logger,
        string transactionFolder,
        Hl7SettingsService? settings = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transactionFolder = transactionFolder;

        Settings = settings ?? new Hl7SettingsService();
        Settings.Load();
    }

    /// <summary>The settings, as last read.</summary>
    public Hl7SettingsService Settings { get; }

    /// <summary>
    /// Whether anything would be announced at all.
    /// </summary>
    /// <remarks>
    /// Asked before a conversion so that the ordinary case, no interface, takes the path it
    /// took before any of this existed.
    /// </remarks>
    public bool IsAnnouncing =>
        Settings.Current.AnnounceConversions && Settings.Current.Rules.Any(r => r.Enabled);

    /// <summary>Reads the settings and the profiles again, after the dialog has saved.</summary>
    public void Reload() => Settings.Load();

    /// <summary>Works out what one document would announce, without sending anything.</summary>
    public Hl7PlanResult Plan(PatientDemographics demographics, string modality = "", string source = "")
    {
        ArgumentNullException.ThrowIfNull(demographics);

        return Hl7PlanFactory.Build(Settings.Current, Settings.Profiles, demographics, modality, source);
    }

    /// <summary>
    /// Converts one document and announces it, in the configured order.
    /// </summary>
    /// <remarks>
    /// The plan is built by the caller and passed in, so that what an operator was shown
    /// before confirming and what is actually sent are the same plan rather than two
    /// evaluations of the same configuration a moment apart.
    /// </remarks>
    public Task<IntegratedConversionResult> RunAsync(
        string sourcePath,
        PatientDemographics demographics,
        Hl7Plan plan,
        ConversionOptions? options = null,
        IProgress<TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var service = new IntegratedConversionService(
            new ConversionWorkflowRunner(_workflow),
            _logger,
            plan.Deliveries.Count > 0 ? BuildDispatcher() : null);

        return service.RunAsync(sourcePath, demographics, plan, options, progress, cancellationToken);
    }

    private Hl7Dispatcher BuildDispatcher() =>
        new(new JsonLinesTransactionStore(_transactionFolder),
            (destination, expectAcknowledgement) =>
                new MllpTransport(destination.ToTransportOptions(expectAcknowledgement)));
}
