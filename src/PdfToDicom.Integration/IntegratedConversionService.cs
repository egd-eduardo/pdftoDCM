using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;
using PdfToDicom.Core.Services;
using PdfToDicom.Interop.Hl7V2.Dispatch;
using PdfToDicom.Interop.Hl7V2.Profiles;
using PdfToDicom.Interop.Hl7V2.Transport;
using PdfToDicom.Interop.Messaging;

namespace PdfToDicom.Integration;

/// <summary>
/// One document, converted and announced, in the order a site has chosen.
/// </summary>
/// <remarks>
/// <para>
/// <b>The conversion workflow itself is not touched.</b> That was the alternative and it
/// was rejected: <see cref="ConversionWorkflowService"/> is the heart of a product in
/// clinical use, and adding a branch to it would mean every future HL7 change reaches into
/// the path that puts documents in archives. This service calls it exactly as the front
/// ends do today, so a build with no HL7 destination behaves identically to one built
/// before any of this existed, and the 291 tests covering that path still cover it whole.
/// </para>
/// <para>
/// <b>An HL7 failure cannot fail a conversion</b> unless a site has explicitly asked for
/// that with <see cref="IntegrationSequence.Hl7ThenDicom"/>. Everything the engine can
/// throw is caught here and turned into a result, because a report reaching the archive
/// matters more than the record system hearing about it, and an exception escaping into
/// the conversion would cost both.
/// </para>
/// </remarks>
public sealed class IntegratedConversionService
{
    private readonly IConversionRunner _conversion;
    private readonly Hl7Dispatcher? _dispatcher;
    private readonly IAppLogger _logger;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="conversion">
    /// The conversion workflow, normally a <see cref="ConversionWorkflowRunner"/> around
    /// the real one.
    /// </param>
    /// <param name="logger">Where a failure on the HL7 side is recorded.</param>
    /// <param name="dispatcher">
    /// The HL7 dispatcher, or null when no HL7 is configured. Null is the ordinary case
    /// and takes the shortest path through this type: the conversion runs and nothing else
    /// happens at all.
    /// </param>
    /// <param name="clock">The clock, for the message subject.</param>
    public IntegratedConversionService(
        IConversionRunner conversion,
        IAppLogger logger,
        Hl7Dispatcher? dispatcher = null,
        Func<DateTimeOffset>? clock = null)
    {
        _conversion = conversion ?? throw new ArgumentNullException(nameof(conversion));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Converts one document and announces it, in the configured order.</summary>
    /// <param name="sourcePath">The document to convert.</param>
    /// <param name="demographics">Patient and order information.</param>
    /// <param name="plan">Which destinations and profiles, and in what order.</param>
    /// <param name="options">The conversion options, passed through untouched.</param>
    /// <param name="progress">Transmission progress, passed through untouched.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<IntegratedConversionResult> RunAsync(
        string sourcePath,
        PatientDemographics demographics,
        Hl7Plan? plan = null,
        ConversionOptions? options = null,
        IProgress<TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demographics);

        plan ??= Hl7Plan.None;

        bool hl7Wanted = _dispatcher is not null && plan.Deliveries.Count > 0;

        if (!hl7Wanted)
        {
            // Nothing configured. One call, no branches, nothing new in the path.
            ConversionResult only = await _conversion
                .RunAsync(sourcePath, demographics, options, progress, cancellationToken)
                .ConfigureAwait(false);

            return new IntegratedConversionResult(only, [], plan.Sequence);
        }

        return plan.Sequence switch
        {
            IntegrationSequence.Hl7ThenDicom =>
                await Hl7FirstAsync(sourcePath, demographics, plan, options, progress, cancellationToken)
                    .ConfigureAwait(false),

            _ => await DicomFirstAsync(sourcePath, demographics, plan, options, progress, cancellationToken)
                    .ConfigureAwait(false)
        };
    }

    private async Task<IntegratedConversionResult> DicomFirstAsync(
        string sourcePath,
        PatientDemographics demographics,
        Hl7Plan plan,
        ConversionOptions? options,
        IProgress<TransmissionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ConversionResult conversion = await _conversion
            .RunAsync(sourcePath, demographics, options, progress, cancellationToken)
            .ConfigureAwait(false);

        if (plan.Sequence == IntegrationSequence.DicomThenHl7 && !conversion.IsCompleteSuccess)
        {
            // The site asked for the message to follow the object. A message carrying a
            // Study Instance UID that no archive holds points at nothing.
            _logger.Information(
                "No HL7 message was sent, because the DICOM half did not complete and this " +
                "destination is configured to announce only what the archive has accepted.");

            return new IntegratedConversionResult(conversion, [], plan.Sequence);
        }

        IReadOnlyList<Hl7DispatchResult> messages = await SendAllAsync(
                demographics, conversion, plan, cancellationToken)
            .ConfigureAwait(false);

        return new IntegratedConversionResult(conversion, messages, plan.Sequence);
    }

    private async Task<IntegratedConversionResult> Hl7FirstAsync(
        string sourcePath,
        PatientDemographics demographics,
        Hl7Plan plan,
        ConversionOptions? options,
        IProgress<TransmissionProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Hl7DispatchResult> messages = await SendAllAsync(
                demographics, result: null, plan, cancellationToken)
            .ConfigureAwait(false);

        bool everyMessageLanded = messages.All(m =>
            m.Outcome is Hl7DispatchOutcome.Acknowledged
                      or Hl7DispatchOutcome.Sent
                      or Hl7DispatchOutcome.AlreadyDelivered);

        if (!everyMessageLanded)
        {
            // The one sequence where HL7 stops DICOM, and it only happens because somebody
            // chose it. The document is not converted, so nothing is left half done.
            _logger.Warning(
                "The document was not converted, because this site sends the HL7 message " +
                "first and it did not reach every destination.");

            return new IntegratedConversionResult(null, messages, plan.Sequence);
        }

        ConversionResult conversion = await _conversion
            .RunAsync(sourcePath, demographics, options, progress, cancellationToken)
            .ConfigureAwait(false);

        return new IntegratedConversionResult(conversion, messages, plan.Sequence);
    }

    private async Task<IReadOnlyList<Hl7DispatchResult>> SendAllAsync(
        PatientDemographics demographics,
        ConversionResult? result,
        Hl7Plan plan,
        CancellationToken cancellationToken)
    {
        MessageSubject subject = Hl7SubjectFactory.From(demographics, result, _clock());
        string sopInstanceUid = result?.SopInstanceUid ?? string.Empty;

        var outcomes = new List<Hl7DispatchResult>();

        foreach (Hl7Delivery delivery in plan.Deliveries)
        {
            outcomes.Add(await SendOneAsync(delivery, subject, sopInstanceUid, cancellationToken)
                .ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<Hl7DispatchResult> SendOneAsync(
        Hl7Delivery delivery,
        MessageSubject subject,
        string sopInstanceUid,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _dispatcher!
                .SendAsync(delivery.Profile, delivery.Destination, subject, sopInstanceUid,
                    progress: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately everything. A defect in the engine must not reach the conversion
            // path, because a report in the archive is worth more than a tidy stack trace
            // and the operator can act on one and not the other.
            _logger.Error(
                $"The HL7 message to '{delivery.Destination.Name}' failed unexpectedly: {ex}");

            return new Hl7DispatchResult(
                Hl7DispatchOutcome.Failed,
                $"The message to {delivery.Destination.Name} failed unexpectedly: {ex.Message}. " +
                "The DICOM side was not affected.");
        }
    }
}

/// <summary>One destination and the profile that builds its message.</summary>
/// <param name="Destination">Where it goes.</param>
/// <param name="Profile">What builds it.</param>
public sealed record Hl7Delivery(Hl7Destination Destination, Hl7Profile Profile);

/// <summary>What a conversion should announce, to whom, and in what order.</summary>
/// <param name="Deliveries">The destinations, with their profiles. Empty means none.</param>
/// <param name="Sequence">The order the two halves run in.</param>
public sealed record Hl7Plan(
    IReadOnlyList<Hl7Delivery> Deliveries,
    IntegrationSequence Sequence = IntegrationSequence.Independent)
{
    /// <summary>A plan that announces nothing, which is what a site gets by default.</summary>
    public static Hl7Plan None { get; } = new([]);
}
