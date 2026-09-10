namespace PdfToDicom.Core.Abstractions;

/// <summary>
/// The outcome of examining the licence code held in the configuration.
/// </summary>
public enum LicenceState
{
    /// <summary>
    /// No public key is embedded in this build, so licensing is not in force at all.
    /// Every feature behaves exactly as it would without any licensing code present.
    /// </summary>
    NotEnforced = 0,

    /// <summary>The code is present, correctly signed and within its term.</summary>
    Valid = 1,

    /// <summary>No code has been entered.</summary>
    Missing = 2,

    /// <summary>The code is malformed, or its signature does not verify.</summary>
    Invalid = 3,

    /// <summary>The code has passed its expiry date and its grace period.</summary>
    Expired = 4,

    /// <summary>The code is bound to a different machine.</summary>
    WrongMachine = 5,

    /// <summary>
    /// The code has expired but is still inside its grace period. Transmission continues
    /// so that a hospital is never cut off without warning.
    /// </summary>
    ExpiringSoon = 6
}

/// <summary>
/// The result of a licence check, in a form the user interface can display directly.
/// </summary>
/// <param name="State">Classification of the licence.</param>
/// <param name="Licensee">Who the licence was issued to, when it could be read.</param>
/// <param name="ExpiresOn">Expiry date, or null for a perpetual licence.</param>
/// <param name="Seats">Number of workstations the licence covers, or zero when unstated.</param>
/// <param name="IsMachineBound">True when the licence names a specific machine.</param>
/// <param name="Message">An explanation suitable for showing to an operator.</param>
public sealed record LicenceStatus(
    LicenceState State,
    string? Licensee,
    DateTime? ExpiresOn,
    int Seats,
    bool IsMachineBound,
    string Message)
{
    /// <summary>
    /// True when the application may transmit to a PACS.
    /// </summary>
    /// <remarks>
    /// Deliberately permissive. Transmission is allowed when licensing is not enforced at
    /// all, when the licence is valid, and during the grace period after expiry. Only a
    /// missing, forged, wrong machine or long expired licence withholds it.
    /// </remarks>
    public bool MayTransmit =>
        State is LicenceState.NotEnforced or LicenceState.Valid or LicenceState.ExpiringSoon;

    /// <summary>True when the operator should be shown a warning about the licence.</summary>
    public bool ShouldWarn =>
        State is not (LicenceState.NotEnforced or LicenceState.Valid);
}

/// <summary>
/// Validates the licence code held in the configuration.
/// </summary>
public interface ILicenceService
{
    /// <summary>
    /// True when this build carries a publisher public key, and licence codes are
    /// therefore checked. False means the feature is dormant.
    /// </summary>
    bool IsEnforced { get; }

    /// <summary>
    /// A stable identifier for this workstation, shown in Settings so that it can be
    /// quoted when requesting a machine bound licence.
    /// </summary>
    string MachineId { get; }

    /// <summary>Examines the supplied code and reports what it is worth.</summary>
    /// <param name="licenceCode">The code as entered by the operator. May be null or empty.</param>
    LicenceStatus Check(string? licenceCode);
}
