namespace PdfToDicom.Core.Models;

/// <summary>
/// The result of transmitting one DICOM object to one destination, including enough
/// detail for a service desk to diagnose a failure without reading the log files.
/// </summary>
public sealed class TransmissionResult
{
    /// <summary>Friendly name of the destination as configured by the administrator.</summary>
    public string DestinationName { get; init; } = string.Empty;

    /// <summary>Called AE Title used for the association.</summary>
    public string CalledAeTitle { get; init; } = string.Empty;

    /// <summary>Host name or IP address of the destination.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>TCP port of the destination.</summary>
    public int Port { get; init; }

    /// <summary>Outcome classification.</summary>
    public TransmissionOutcome Outcome { get; init; }

    /// <summary>True when the object was accepted by the peer.</summary>
    public bool IsSuccess => Outcome == TransmissionOutcome.Succeeded;

    /// <summary>True when the verification step was performed and succeeded.</summary>
    public bool VerificationPerformed { get; init; }

    /// <summary>Operator facing explanation of what happened.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// DICOM status code returned by the peer for the C-STORE, when one was received.
    /// Recorded as a hexadecimal string, for example "0000" for success.
    /// </summary>
    public string? DicomStatusCode { get; init; }

    /// <summary>Description of the DICOM status returned by the peer, when available.</summary>
    public string? DicomStatusDescription { get; init; }

    /// <summary>Number of attempts made, including the first one.</summary>
    public int AttemptCount { get; init; }

    /// <summary>Total elapsed time for all attempts against this destination.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Exception detail, retained for the log file and the diagnostics view.</summary>
    public string? ExceptionDetail { get; init; }
}
