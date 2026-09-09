using FellowOakDicom;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Models;

namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// Progress information reported during transmission so the user interface can show
/// what is happening rather than an indeterminate spinner.
/// </summary>
/// <param name="DestinationName">Destination currently being contacted.</param>
/// <param name="Stage">Current stage of the exchange.</param>
/// <param name="AttemptNumber">One based attempt counter.</param>
/// <param name="TotalAttempts">Maximum number of attempts that will be made.</param>
/// <param name="Message">Operator facing description of the current stage.</param>
public sealed record TransmissionProgress(
    string DestinationName,
    TransmissionStage Stage,
    int AttemptNumber,
    int TotalAttempts,
    string Message);

/// <summary>Stages of a transmission, reported in order.</summary>
public enum TransmissionStage
{
    /// <summary>Opening the TCP connection and negotiating the association.</summary>
    Connecting = 0,

    /// <summary>Performing the C-ECHO verification.</summary>
    Verifying = 1,

    /// <summary>Sending the object with C-STORE.</summary>
    Storing = 2,

    /// <summary>Waiting between retry attempts.</summary>
    Retrying = 3,

    /// <summary>The exchange finished, successfully or otherwise.</summary>
    Completed = 4
}

/// <summary>
/// Performs DICOM Verification and Storage as a Service Class User.
/// </summary>
public interface IDicomNetworkService
{
    /// <summary>
    /// Performs a C-ECHO against the supplied destination. Used both before a transmission
    /// and by the "Test connection" button in the settings dialog.
    /// </summary>
    Task<TransmissionResult> VerifyAsync(
        PacsDestination destination,
        string callingAeTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one object to one destination, honouring the destination's verification,
    /// timeout and retry settings.
    /// </summary>
    Task<TransmissionResult> StoreAsync(
        DicomFile file,
        PacsDestination destination,
        string callingAeTitle,
        IProgress<TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one object to every supplied destination in turn, returning one result per
    /// destination. A failure against one destination does not prevent the others from
    /// being attempted.
    /// </summary>
    Task<IReadOnlyList<TransmissionResult>> StoreToAllAsync(
        DicomFile file,
        IReadOnlyList<PacsDestination> destinations,
        string callingAeTitle,
        IProgress<TransmissionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
