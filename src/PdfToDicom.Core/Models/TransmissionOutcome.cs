namespace PdfToDicom.Core.Models;

/// <summary>
/// Overall outcome of an attempt to transmit a DICOM object to a single destination.
/// </summary>
public enum TransmissionOutcome
{
    /// <summary>The association was established and the object was accepted by the peer.</summary>
    Succeeded = 0,

    /// <summary>The peer could not be contacted, or the association was rejected or aborted.</summary>
    ConnectionFailed = 1,

    /// <summary>The association succeeded but the peer returned a failure status for the C-STORE.</summary>
    StoreRejected = 2,

    /// <summary>The verification (C-ECHO) step failed, so no C-STORE was attempted.</summary>
    VerificationFailed = 3,

    /// <summary>The operation was cancelled by the operator.</summary>
    Cancelled = 4,

    /// <summary>An unexpected local error prevented the transmission.</summary>
    LocalError = 5
}
