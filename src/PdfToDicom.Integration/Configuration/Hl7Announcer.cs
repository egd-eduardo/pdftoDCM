using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Interop.Hl7V2.Dispatch;
using PdfToDicom.Interop.Hl7V2.Profiles;
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
    private readonly ConversionWorkflowService? _workflow;
    private readonly IAppLogger _logger;
    private readonly string _transactionFolder;

    /// <summary>Creates the announcer.</summary>
    /// <param name="workflow">
    /// The conversion workflow, called exactly as the front ends call it, or null when this
    /// announcer is only going to send test messages.
    /// </param>
    /// <param name="logger">Where the HL7 side writes.</param>
    /// <param name="transactionFolder">Where the attempts are written down.</param>
    /// <param name="settings">
    /// The settings service, or a new one reading the standard location.
    /// </param>
    /// <remarks>
    /// The workflow is optional, and that is not a convenience for the tests. Commissioning
    /// an interface means sending messages and converting nothing, so insisting on the
    /// conversion workflow here would make the commissioning half of this type depend on
    /// eleven collaborators it never calls. <see cref="RunAsync"/> says so if it is reached
    /// without one.
    /// </remarks>
    public Hl7Announcer(
        ConversionWorkflowService? workflow,
        IAppLogger logger,
        string transactionFolder,
        Hl7SettingsService? settings = null)
    {
        _workflow = workflow;
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

        if (_workflow is null)
        {
            throw new InvalidOperationException(
                "This announcer was created without a conversion workflow, so it can send " +
                "test messages and cannot convert a document.");
        }

        var service = new IntegratedConversionService(
            new ConversionWorkflowRunner(_workflow),
            _logger,
            plan.Deliveries.Count > 0 ? BuildDispatcher() : null);

        return service.RunAsync(sourcePath, demographics, plan, options, progress, cancellationToken);
    }

    /// <summary>
    /// Sends one message built from invented values, to prove an interface works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of the connection test, which proves only that a port is open. This
    /// goes the whole way: the profile builds a message, the message goes on the wire, and
    /// whatever comes back is read and reported. It is the only way to find out whether the
    /// receiver understands what this application produces.
    /// </para>
    /// <para>
    /// It is an ordinary send in every respect, which is the point and also the risk: the
    /// message reaches the configured destination, so a live receiver will see a patient
    /// called ZZZTEST. Whoever offers this has to make that plain first; this method does
    /// not ask, because a method that asks cannot be tested.
    /// </para>
    /// </remarks>
    public async Task<Hl7DispatchResult> SendTestMessageAsync(
        Hl7DestinationSettings destination,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        Hl7Profile? profile = Settings.Profiles.Find(profileId);

        if (profile is null)
        {
            return new Hl7DispatchResult(
                Hl7DispatchOutcome.Refused,
                $"The profile '{profileId}' is not installed, so no message could be built.");
        }

        IReadOnlyList<string> problems = destination.CheckBlocking();

        if (problems.Count > 0)
        {
            return new Hl7DispatchResult(
                Hl7DispatchOutcome.Refused, string.Join(" ", problems));
        }

        _logger.Information(
            $"Sending an HL7 test message to {destination.Name} " +
            $"({destination.Host}:{destination.Port}) under profile '{profileId}'. " +
            $"The patient is the invented one, {Hl7TestSubject.FamilyName}.");

        return await BuildDispatcher()
            .SendAsync(
                profile,
                destination.ToEngine(),
                Hl7TestSubject.Create(destination),
                relatedSopInstanceUid: string.Empty,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Hl7Dispatcher BuildDispatcher() =>
        new(new JsonLinesTransactionStore(_transactionFolder),
            (destination, expectAcknowledgement) =>
                new MllpTransport(destination.ToTransportOptions(expectAcknowledgement)));
}
