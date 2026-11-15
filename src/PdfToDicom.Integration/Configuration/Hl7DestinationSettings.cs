using PdfToDicom.Interop.Hl7V2.Transport;

namespace PdfToDicom.Integration.Configuration;

/// <summary>
/// One HL7 destination as it is stored and edited.
/// </summary>
/// <remarks>
/// <para>
/// A mutable mirror of <see cref="Hl7Destination"/>, which is an immutable record. Two
/// alternatives were rejected. Making the engine's own type mutable would hand every caller
/// the ability to change a destination half way through a send, and the engine passes it
/// across threads. Binding the editor to the record directly is not possible at all: WPF
/// two way binding needs a setter, and the repository has a validator that fails the build
/// for exactly that mistake.
/// </para>
/// <para>
/// The cost of the mirror is that a property added here and not carried across would
/// silently have no effect, which is the failure this product has already paid for once. Two
/// tests guard it: one compares the property names of the two types, so either gaining a
/// property the other lacks fails, and one sets every property to a distinctive value and
/// reads it back off the engine type.
/// </para>
/// </remarks>
public sealed class Hl7DestinationSettings
{
    /// <summary>Stable identifier, which the routing rules refer to.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What an operator calls this destination.</summary>
    public string Name { get; set; } = "HL7 destination";

    /// <summary>Host name or address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>TCP port. 2575 is the usual one for MLLP.</summary>
    public int Port { get; set; } = 2575;

    /// <summary>Whether this destination is used at all.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>What this application calls itself, for MSH-3.</summary>
    public string SendingApplication { get; set; } = "PDFTODICOM";

    /// <summary>The sending facility, for MSH-4.</summary>
    public string SendingFacility { get; set; } = string.Empty;

    /// <summary>What the receiver calls itself, for MSH-5.</summary>
    public string ReceivingApplication { get; set; } = string.Empty;

    /// <summary>The receiving facility, for MSH-6.</summary>
    public string ReceivingFacility { get; set; } = string.Empty;

    /// <summary>How long to wait for the connection.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>How long to wait for an acknowledgement.</summary>
    public int ReceiveTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How many further attempts to make. Zero by default, deliberately.
    /// </summary>
    /// <remarks>
    /// A retry after a timeout can produce a second copy of a document at the far end,
    /// because a message that was received and not acknowledged looks exactly like one that
    /// never arrived. Whether that is acceptable depends on the receiver, so it is a
    /// decision somebody takes rather than a default they inherit.
    /// </remarks>
    public int RetryCount { get; set; }

    /// <summary>How long to wait between attempts.</summary>
    public int RetryDelaySeconds { get; set; } = 3;

    /// <summary>Whether to wrap the connection in TLS.</summary>
    public bool UseTls { get; set; }

    /// <summary>Whether to accept a certificate that does not validate.</summary>
    public bool AcceptAnyTlsCertificate { get; set; }

    /// <summary>
    /// The profiles this destination accepts, or empty for any of them.
    /// </summary>
    /// <remarks>
    /// Empty means any, which is what a site gets without doing anything. A non empty list
    /// is an explicit restriction, and a rule that sends a profile this list does not name
    /// is reported as a problem rather than quietly skipped: a routing mistake that silently
    /// sends nothing is harder to notice than one that says so.
    /// </remarks>
    public List<string> ProfileIds { get; set; } = new();

    /// <summary>When this destination last accepted something, for the operator's benefit.</summary>
    public DateTimeOffset? LastSuccessfulUseUtc { get; set; }

    /// <summary>A one line description, for a list.</summary>
    public string DisplaySummary => $"{Name} ({Host}:{Port})";

    /// <summary>Turns this into the engine's own destination.</summary>
    public Hl7Destination ToEngine() =>
        new()
        {
            Id = Id,
            Name = Name,
            Host = Host,
            Port = Port,
            IsEnabled = IsEnabled,
            SendingApplication = SendingApplication,
            SendingFacility = SendingFacility,
            ReceivingApplication = ReceivingApplication,
            ReceivingFacility = ReceivingFacility,
            ConnectTimeoutSeconds = ConnectTimeoutSeconds,
            ReceiveTimeoutSeconds = ReceiveTimeoutSeconds,
            RetryCount = RetryCount,
            RetryDelaySeconds = RetryDelaySeconds,
            UseTls = UseTls,
            AcceptAnyTlsCertificate = AcceptAnyTlsCertificate,
            ProfileIds = ProfileIds.ToList(),
            LastSuccessfulUseUtc = LastSuccessfulUseUtc
        };

    /// <summary>Builds the editable form of an engine destination.</summary>
    public static Hl7DestinationSettings FromEngine(Hl7Destination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        return new Hl7DestinationSettings
        {
            Id = destination.Id,
            Name = destination.Name,
            Host = destination.Host,
            Port = destination.Port,
            IsEnabled = destination.IsEnabled,
            SendingApplication = destination.SendingApplication,
            SendingFacility = destination.SendingFacility,
            ReceivingApplication = destination.ReceivingApplication,
            ReceivingFacility = destination.ReceivingFacility,
            ConnectTimeoutSeconds = destination.ConnectTimeoutSeconds,
            ReceiveTimeoutSeconds = destination.ReceiveTimeoutSeconds,
            RetryCount = destination.RetryCount,
            RetryDelaySeconds = destination.RetryDelaySeconds,
            UseTls = destination.UseTls,
            AcceptAnyTlsCertificate = destination.AcceptAnyTlsCertificate,
            ProfileIds = destination.ProfileIds.ToList(),
            LastSuccessfulUseUtc = destination.LastSuccessfulUseUtc
        };
    }

    /// <summary>
    /// The problems that stop this destination being used at all.
    /// </summary>
    /// <remarks>
    /// Delegates to the engine's own check rather than repeating it, so the rules a send
    /// enforces and the rules the editor enforces cannot drift apart. Kept separate from
    /// <see cref="Check"/> so that whatever decides whether to send can ask for the blocking
    /// problems directly. The alternative, one list and a caller that recognises the
    /// advisory entries by their wording, makes a change to a sentence change what gets
    /// sent.
    /// </remarks>
    public IReadOnlyList<string> CheckBlocking() => ToEngine().CheckBlocking();

    /// <summary>
    /// Everything worth saying about this destination, as the settings page shows it.
    /// </summary>
    /// <remarks>
    /// Both of these are the engine's answers, named here rather than restated. An earlier
    /// version of this type repeated the certificate advisory in its own words, so the same
    /// situation was reported twice in two different sentences depending on who asked.
    /// </remarks>
    public IReadOnlyList<string> Check()
    {
        IReadOnlyList<string> problems = ToEngine().Check();

        // Prefixed with the name, because this list is shown beside the other destinations'
        // and "a destination needs a host" does not say which.
        return problems.Select(p => $"'{Name}': {p}").ToList();
    }
}
