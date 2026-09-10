using System.Diagnostics;
using System.Net.Sockets;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Dicom;
using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Networking;

/// <summary>
/// DICOM Verification and Storage Service Class User built on the Fellow Oak toolkit.
/// </summary>
/// <remarks>
/// <para>
/// Each destination is contacted on its own association. Verification, when enabled, is
/// performed on a separate association before the store, which is what most archives
/// expect and which keeps a rejected verification from consuming a store attempt.
/// </para>
/// <para>
/// The presentation contexts offered for the store include Explicit VR Little Endian,
/// which is how the object is encoded, and Implicit VR Little Endian, which the standard
/// requires every Storage SCP to support. Offering both means the association succeeds
/// even against an archive with an unusually narrow context list.
/// </para>
/// </remarks>
public sealed class DicomNetworkService : IDicomNetworkService
{
    private readonly IAppLogger _logger;

    /// <summary>Creates the service.</summary>
    public DicomNetworkService(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Guarantees the implementation identity advertised during association
        // negotiation matches the one written into generated files.
        DicomRuntime.EnsureInitialized();
    }

    /// <inheritdoc />
    public async Task<TransmissionResult> VerifyAsync(
        PacsDestination destination,
        string callingAeTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var stopwatch = Stopwatch.StartNew();
        string effectiveCallingAe = ResolveCallingAeTitle(destination, callingAeTitle);

        _logger.Information(
            $"Sending C-ECHO to '{destination.Name}' ({destination.CalledAeTitle}@{destination.Host}:{destination.Port}) " +
            $"as '{effectiveCallingAe}'.");

        try
        {
            IDicomClient client = CreateClient(destination, effectiveCallingAe);

            DicomStatus? status = null;
            var request = new DicomCEchoRequest();
            request.OnResponseReceived = (_, response) => status = response.Status;

            await client.AddRequestAsync(request).ConfigureAwait(false);
            await client.SendAsync(cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            bool succeeded = status is not null && status.State == DicomState.Success;

            if (succeeded)
            {
                _logger.Information(
                    $"C-ECHO to '{destination.Name}' succeeded in {stopwatch.ElapsedMilliseconds} ms.");
            }
            else
            {
                _logger.Warning(
                    $"C-ECHO to '{destination.Name}' returned status '{DescribeStatus(status)}'.");
            }

            return BuildResult(
                destination,
                effectiveCallingAe,
                succeeded ? TransmissionOutcome.Succeeded : TransmissionOutcome.VerificationFailed,
                verificationPerformed: true,
                message: succeeded
                    ? $"The destination '{destination.Name}' responded to the verification request."
                    : $"The destination '{destination.Name}' rejected the verification request " +
                      $"({DescribeStatus(status)}).",
                status: status,
                attempts: 1,
                duration: stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return BuildResult(
                destination, effectiveCallingAe, TransmissionOutcome.Cancelled, true,
                "The verification was cancelled.", null, 1, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error($"C-ECHO to '{destination.Name}' failed.", ex);

            return BuildResult(
                destination, effectiveCallingAe, TransmissionOutcome.ConnectionFailed, true,
                DescribeException(ex, destination), null, 1, stopwatch.Elapsed, ex.ToString());
        }
    }

    /// <inheritdoc />
    public async Task<TransmissionResult> StoreAsync(
        DicomFile file,
        PacsDestination destination,
        string callingAeTitle,
        IProgress<TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);

        string effectiveCallingAe = ResolveCallingAeTitle(destination, callingAeTitle);
        int totalAttempts = destination.RetryCount + 1;
        var stopwatch = Stopwatch.StartNew();

        TransmissionResult? lastResult = null;

        for (int attempt = 1; attempt <= totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                progress?.Report(new TransmissionProgress(
                    destination.Name, TransmissionStage.Retrying, attempt, totalAttempts,
                    $"Waiting {destination.RetryDelaySeconds} s before attempt {attempt} of {totalAttempts}."));

                _logger.Information(
                    $"Waiting {destination.RetryDelaySeconds} s before retrying '{destination.Name}' " +
                    $"(attempt {attempt} of {totalAttempts}).");

                await Task.Delay(
                    TimeSpan.FromSeconds(destination.RetryDelaySeconds),
                    cancellationToken).ConfigureAwait(false);
            }

            lastResult = await AttemptStoreAsync(
                file, destination, effectiveCallingAe, attempt, totalAttempts,
                progress, cancellationToken).ConfigureAwait(false);

            if (lastResult.IsSuccess || lastResult.Outcome == TransmissionOutcome.Cancelled)
            {
                break;
            }

            // A definitive rejection by the archive, for example an unsupported SOP Class,
            // will not change on a retry, so the attempt loop stops.
            if (lastResult.Outcome == TransmissionOutcome.StoreRejected && !IsRetryableStatus(lastResult))
            {
                _logger.Warning(
                    $"'{destination.Name}' rejected the object with a status that will not change on retry. " +
                    "No further attempts will be made.");
                break;
            }
        }

        stopwatch.Stop();

        TransmissionResult result = lastResult ?? BuildResult(
            destination, effectiveCallingAe, TransmissionOutcome.LocalError, false,
            "No transmission attempt was made.", null, 0, stopwatch.Elapsed);

        progress?.Report(new TransmissionProgress(
            destination.Name, TransmissionStage.Completed, result.AttemptCount, totalAttempts, result.Message));

        if (result.IsSuccess)
        {
            destination.LastSuccessfulUseUtc = DateTime.UtcNow;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TransmissionResult>> StoreToAllAsync(
        DicomFile file,
        IReadOnlyList<PacsDestination> destinations,
        string callingAeTitle,
        IProgress<TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destinations);

        var results = new List<TransmissionResult>(destinations.Count);

        foreach (PacsDestination destination in destinations)
        {
            // Destinations are contacted one at a time rather than in parallel. A hospital
            // workstation frequently sits behind a firewall that limits concurrent
            // associations, and sequential progress is far easier for an operator to follow.
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(await StoreAsync(
                file, destination, callingAeTitle, progress, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    // ------------------------------------------------------------------ Internals

    /// <summary>Performs one verification and store cycle against one destination.</summary>
    private async Task<TransmissionResult> AttemptStoreAsync(
        DicomFile file,
        PacsDestination destination,
        string callingAeTitle,
        int attempt,
        int totalAttempts,
        IProgress<TransmissionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (destination.VerifyBeforeSend)
            {
                progress?.Report(new TransmissionProgress(
                    destination.Name, TransmissionStage.Verifying, attempt, totalAttempts,
                    $"Verifying '{destination.Name}' with C-ECHO."));

                TransmissionResult verification =
                    await VerifyAsync(destination, callingAeTitle, cancellationToken).ConfigureAwait(false);

                if (!verification.IsSuccess)
                {
                    stopwatch.Stop();

                    // The store is abandoned: an archive that will not answer a
                    // verification request is in no position to accept a document.
                    return new TransmissionResult
                    {
                        DestinationName = verification.DestinationName,
                        CalledAeTitle = verification.CalledAeTitle,
                        Host = verification.Host,
                        Port = verification.Port,
                        Outcome = verification.Outcome,
                        VerificationPerformed = true,
                        DicomStatusCode = verification.DicomStatusCode,
                        DicomStatusDescription = verification.DicomStatusDescription,
                        ExceptionDetail = verification.ExceptionDetail,
                        AttemptCount = attempt,
                        Duration = stopwatch.Elapsed,
                        Message =
                            $"Verification of '{destination.Name}' failed, so the document was not sent. " +
                            verification.Message
                    };
                }
            }

            progress?.Report(new TransmissionProgress(
                destination.Name, TransmissionStage.Connecting, attempt, totalAttempts,
                $"Connecting to '{destination.Name}'."));

            IDicomClient client = CreateClient(destination, callingAeTitle);

            // Offering both little endian syntaxes maximises the chance of a successful
            // negotiation. Implicit VR Little Endian is mandatory for every Storage SCP.
            var presentationContext = new DicomPresentationContext(
                1,
                DicomUID.Parse(DicomConstants.EncapsulatedPdfStorageSopClassUid));

            presentationContext.AddTransferSyntax(DicomTransferSyntax.ExplicitVRLittleEndian);
            presentationContext.AddTransferSyntax(DicomTransferSyntax.ImplicitVRLittleEndian);

            client.AdditionalPresentationContexts.Add(presentationContext);

            DicomStatus? status = null;
            var request = new DicomCStoreRequest(file);
            request.OnResponseReceived = (_, response) => status = response.Status;

            await client.AddRequestAsync(request).ConfigureAwait(false);

            progress?.Report(new TransmissionProgress(
                destination.Name, TransmissionStage.Storing, attempt, totalAttempts,
                $"Sending the document to '{destination.Name}'."));

            await client.SendAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            bool succeeded = status is not null && status.State == DicomState.Success;

            if (succeeded)
            {
                _logger.Information(
                    $"C-STORE to '{destination.Name}' succeeded in {stopwatch.ElapsedMilliseconds} ms " +
                    $"on attempt {attempt} of {totalAttempts}.");

                return BuildResult(
                    destination, callingAeTitle, TransmissionOutcome.Succeeded, destination.VerifyBeforeSend,
                    $"'{destination.Name}' accepted the document.", status, attempt, stopwatch.Elapsed);
            }

            // A warning status means the object was stored but something was adjusted, for
            // example a coercion of the patient name to match the archive's own record.
            if (status is not null && status.State == DicomState.Warning)
            {
                _logger.Warning(
                    $"C-STORE to '{destination.Name}' returned the warning status '{DescribeStatus(status)}'. " +
                    "The archive accepted the object with a caveat.");

                return BuildResult(
                    destination, callingAeTitle, TransmissionOutcome.Succeeded, destination.VerifyBeforeSend,
                    $"'{destination.Name}' accepted the document with a warning: {DescribeStatus(status)}.",
                    status, attempt, stopwatch.Elapsed);
            }

            _logger.Warning(
                $"C-STORE to '{destination.Name}' was rejected with status '{DescribeStatus(status)}' " +
                $"on attempt {attempt} of {totalAttempts}.");

            return BuildResult(
                destination, callingAeTitle, TransmissionOutcome.StoreRejected, destination.VerifyBeforeSend,
                $"'{destination.Name}' rejected the document: {DescribeStatus(status)}.",
                status, attempt, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.Information($"The transmission to '{destination.Name}' was cancelled by the operator.");

            return BuildResult(
                destination, callingAeTitle, TransmissionOutcome.Cancelled, destination.VerifyBeforeSend,
                "The transmission was cancelled.", null, attempt, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(
                $"C-STORE to '{destination.Name}' failed on attempt {attempt} of {totalAttempts}.", ex);

            return BuildResult(
                destination, callingAeTitle, TransmissionOutcome.ConnectionFailed, destination.VerifyBeforeSend,
                DescribeException(ex, destination), null, attempt, stopwatch.Elapsed, ex.ToString());
        }
    }

    /// <summary>Creates a configured client for the supplied destination.</summary>
    private static IDicomClient CreateClient(PacsDestination destination, string callingAeTitle)
    {
        IDicomClient client = DicomClientFactory.Create(
            destination.Host.Trim(),
            destination.Port,
            destination.UseTls,
            callingAeTitle,
            destination.CalledAeTitle.Trim());

        var timeout = TimeSpan.FromSeconds(destination.TimeoutSeconds);

        client.ServiceOptions.RequestTimeout = timeout;
        client.ClientOptions.AssociationRequestTimeoutInMs = (int)timeout.TotalMilliseconds;
        client.ClientOptions.AssociationReleaseTimeoutInMs = (int)timeout.TotalMilliseconds;

        // Retrying is handled by this service so that each attempt is logged and reported
        // individually. The client's own retry loop is therefore disabled.
        client.ClientOptions.MaximumNumberOfConsecutiveTimedOutAssociationRequests = 1;

        return client;
    }

    /// <summary>
    /// Determines the calling AE Title for a destination, honouring a per destination
    /// override when one is configured.
    /// </summary>
    private static string ResolveCallingAeTitle(PacsDestination destination, string callingAeTitle) =>
        string.IsNullOrWhiteSpace(destination.CallingAeTitleOverride)
            ? (callingAeTitle ?? string.Empty).Trim()
            : destination.CallingAeTitleOverride.Trim();

    /// <summary>Assembles a result record.</summary>
    private static TransmissionResult BuildResult(
        PacsDestination destination,
        string callingAeTitle,
        TransmissionOutcome outcome,
        bool verificationPerformed,
        string message,
        DicomStatus? status,
        int attempts,
        TimeSpan duration,
        string? exceptionDetail = null) =>
        new()
        {
            DestinationName = destination.Name,
            CalledAeTitle = destination.CalledAeTitle,
            Host = destination.Host,
            Port = destination.Port,
            Outcome = outcome,
            VerificationPerformed = verificationPerformed,
            Message = message,
            DicomStatusCode = status is null ? null : ((ushort)status.Code).ToString("X4"),
            DicomStatusDescription = status?.Description,
            AttemptCount = attempts,
            Duration = duration,
            ExceptionDetail = exceptionDetail
        };

    /// <summary>Renders a DICOM status for display and for the log.</summary>
    private static string DescribeStatus(DicomStatus? status) =>
        status is null
            ? "no response was received"
            : $"0x{(ushort)status.Code:X4} {status.Description}";

    /// <summary>
    /// Turns a network exception into an explanation an operator can act on, rather than
    /// a stack trace. The exception itself is still written to the log in full.
    /// </summary>
    private static string DescribeException(Exception exception, PacsDestination destination)
    {
        string endpoint = $"{destination.CalledAeTitle}@{destination.Host}:{destination.Port}";

        // The asynchronous client surfaces connection problems wrapped in an
        // AggregateException whose own message ("One or more errors occurred") tells the
        // operator nothing. Unwrapping to the real cause is what makes the advice below
        // reachable in practice.
        Exception cause = Unwrap(exception);

        return cause switch
        {
            DicomAssociationRejectedException reject =>
                $"The destination {endpoint} rejected the association ({reject.RejectReason}). " +
                "Confirm the called and calling AE Titles are the ones the archive expects.",

            DicomAssociationAbortedException abort =>
                $"The destination {endpoint} aborted the association ({abort.AbortReason}). " +
                "The archive may not accept Encapsulated PDF objects, or it may be busy.",

            SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                $"The connection to {endpoint} was refused. Confirm the archive is running and " +
                "that the port is correct.",

            SocketException { SocketErrorCode: SocketError.HostNotFound } =>
                $"The host name '{destination.Host}' could not be resolved. Check the spelling, " +
                "or use the IP address instead.",

            SocketException { SocketErrorCode: SocketError.TimedOut } =>
                $"The connection to {endpoint} timed out. A firewall may be blocking the port.",

            SocketException socket =>
                $"A network error occurred while contacting {endpoint} ({socket.SocketErrorCode}).",

            IOException io =>
                $"The connection to {endpoint} was interrupted: {io.Message}",

            _ => $"The transmission to {endpoint} failed: {cause.Message}"
        };
    }

    /// <summary>
    /// Unwraps aggregate and wrapper exceptions to reach the underlying cause, preferring
    /// a socket or association level error because those carry actionable detail.
    /// </summary>
    private static Exception Unwrap(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            AggregateException flattened = aggregate.Flatten();

            Exception? meaningful = flattened.InnerExceptions
                .FirstOrDefault(e => e is SocketException
                    or DicomAssociationRejectedException
                    or DicomAssociationAbortedException);

            return Unwrap(meaningful ?? flattened.InnerExceptions.FirstOrDefault() ?? exception);
        }

        // A wrapper that adds no message of its own is stepped through to its cause.
        if (exception.InnerException is SocketException
            or DicomAssociationRejectedException
            or DicomAssociationAbortedException)
        {
            return exception.InnerException;
        }

        return exception;
    }

    /// <summary>
    /// Determines whether a rejection status is worth retrying. Resource shortage and
    /// similar transient conditions may clear; a refusal based on the content of the
    /// object will not.
    /// </summary>
    private static bool IsRetryableStatus(TransmissionResult result)
    {
        if (string.IsNullOrEmpty(result.DicomStatusCode) ||
            !ushort.TryParse(result.DicomStatusCode, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ushort code))
        {
            return true;
        }

        // 0xA7xx is "Refused: Out of Resources" and 0x0211 is "Unrecognised Operation";
        // the former is transient, the latter and the content related failures are not.
        return (code & 0xFF00) == 0xA700;
    }
}
