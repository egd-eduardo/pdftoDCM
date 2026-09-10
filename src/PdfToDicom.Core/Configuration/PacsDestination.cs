using System.Text.Json.Serialization;

namespace PdfToDicom.Core.Configuration;

/// <summary>
/// A single DICOM Storage SCP that generated objects can be sent to.
/// </summary>
/// <remarks>
/// The application supports more than one destination. Each entry is configured and
/// enabled independently so that, for example, a site can send simultaneously to a
/// primary PACS and to a departmental archive.
/// </remarks>
public sealed class PacsDestination
{
    /// <summary>Stable identifier used to reference this destination in logs and templates.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Friendly name shown in the user interface, for example "Main PACS".</summary>
    public string Name { get; set; } = "PACS";

    /// <summary>Called AE Title, that is the AE Title of the receiving system.</summary>
    public string CalledAeTitle { get; set; } = "PACS";

    /// <summary>Host name or IP address of the receiving system.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>TCP port on which the receiving system listens.</summary>
    public int Port { get; set; } = 104;

    /// <summary>
    /// Calling AE Title override. When empty the global calling AE Title from
    /// <see cref="AppConfiguration.CallingAeTitle"/> is used.
    /// </summary>
    public string CallingAeTitleOverride { get; set; } = string.Empty;

    /// <summary>When false the destination is retained in the configuration but skipped.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Perform a C-ECHO verification before attempting the C-STORE.</summary>
    public bool VerifyBeforeSend { get; set; } = true;

    /// <summary>Association and network timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of additional attempts after an initial failure. Zero means a single
    /// attempt is made with no retry.
    /// </summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>Delay between retry attempts, in seconds.</summary>
    public int RetryDelaySeconds { get; set; } = 3;

    /// <summary>
    /// Establish the association over TLS. Only enable this when the receiving system is
    /// configured for DICOM TLS, otherwise the association will fail.
    /// </summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// When TLS is enabled, accept a server certificate that does not chain to a trusted
    /// root. Intended only for closed hospital networks using self signed certificates.
    /// </summary>
    public bool AcceptAnyTlsCertificate { get; set; }

    /// <summary>Timestamp of the last successful transmission, used to order the recent list.</summary>
    public DateTime? LastSuccessfulUseUtc { get; set; }

    /// <summary>A short description shown in tooltips and log messages.</summary>
    [JsonIgnore]
    public string DisplaySummary => $"{Name} ({CalledAeTitle}@{Host}:{Port})";

    /// <summary>Creates an independent copy, used by the settings dialog for cancel support.</summary>
    public PacsDestination Clone() => new()
    {
        Id = Id,
        Name = Name,
        CalledAeTitle = CalledAeTitle,
        Host = Host,
        Port = Port,
        CallingAeTitleOverride = CallingAeTitleOverride,
        IsEnabled = IsEnabled,
        VerifyBeforeSend = VerifyBeforeSend,
        TimeoutSeconds = TimeoutSeconds,
        RetryCount = RetryCount,
        RetryDelaySeconds = RetryDelaySeconds,
        UseTls = UseTls,
        AcceptAnyTlsCertificate = AcceptAnyTlsCertificate,
        LastSuccessfulUseUtc = LastSuccessfulUseUtc
    };
}
