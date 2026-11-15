namespace PdfToDicom.Interop.Hl7V2.Transport;

/// <summary>
/// One receiving system, configured.
/// </summary>
/// <remarks>
/// The shape follows the DICOM side's destination on purpose: an identifier, a name, a
/// host, a port, an enabled flag, timeouts, a retry policy, a TLS choice and the time it
/// last worked. An administrator who has configured an archive should recognise this page
/// rather than meet a second way of saying the same things, and whoever maintains both
/// should find the same fields in the same order.
/// </remarks>
public sealed record Hl7Destination
{
    /// <summary>A stable identifier, which routing rules refer to.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>What an administrator calls it.</summary>
    public string Name { get; init; } = "HL7 destination";

    /// <summary>Host name or address.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>The TCP port it listens on.</summary>
    public int Port { get; init; } = 2575;

    /// <summary>Whether it may be used.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>What this workstation calls itself, written to MSH-3.</summary>
    public string SendingApplication { get; init; } = string.Empty;

    /// <summary>The site this workstation belongs to, written to MSH-4.</summary>
    public string SendingFacility { get; init; } = string.Empty;

    /// <summary>What the receiver calls itself, written to MSH-5.</summary>
    public string ReceivingApplication { get; init; } = string.Empty;

    /// <summary>The receiver's site, written to MSH-6.</summary>
    public string ReceivingFacility { get; init; } = string.Empty;

    /// <summary>How long to wait for the connection.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 10;

    /// <summary>How long to wait for an acknowledgement, where one is expected.</summary>
    public int ReceiveTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// How many further attempts to make after a failure that did not put anything on the
    /// wire.
    /// </summary>
    public int RetryCount { get; init; } = 2;

    /// <summary>How long to wait between attempts.</summary>
    public int RetryDelaySeconds { get; init; } = 3;

    /// <summary>Whether the connection is encrypted.</summary>
    public bool UseTls { get; init; }

    /// <summary>Whether a certificate that does not validate is accepted anyway.</summary>
    public bool AcceptAnyTlsCertificate { get; init; }

    /// <summary>Which profiles this destination accepts messages from.</summary>
    public IReadOnlyList<string> ProfileIds { get; init; } = [];

    /// <summary>When this destination last accepted something.</summary>
    public DateTimeOffset? LastSuccessfulUseUtc { get; init; }

    /// <summary>A one line description, as the DICOM destinations show.</summary>
    public string DisplaySummary => $"{Name} ({Host}:{Port})";

    /// <summary>
    /// Turns the destination into transport options for one message.
    /// </summary>
    /// <param name="expectAcknowledgement">
    /// Taken from the profile rather than from the destination, because it is the
    /// interface's behaviour and not the address's. The same host could acknowledge one
    /// message type and not another.
    /// </param>
    public MllpOptions ToTransportOptions(bool expectAcknowledgement) =>
        new(Host, Port)
        {
            Name = Name,
            ConnectTimeoutSeconds = ConnectTimeoutSeconds,
            ReceiveTimeoutSeconds = ReceiveTimeoutSeconds,
            ExpectAcknowledgement = expectAcknowledgement
        };

    /// <summary>
    /// Everything worth saying about this destination, advisories included.
    /// </summary>
    /// <remarks>
    /// What a settings page shows. Whatever decides whether to send asks
    /// <see cref="CheckBlocking"/> instead: the two were one list once, and a caller that
    /// recognised the advisory by the word it starts with would have changed what gets sent
    /// the first time somebody reworded a sentence.
    /// </remarks>
    public IReadOnlyList<string> Check()
    {
        var problems = new List<string>(CheckBlocking());

        if (UseTls && AcceptAnyTlsCertificate)
        {
            // Allowed, because a hospital's internal certificate may not chain anywhere a
            // workstation knows. Never silent, because it is a real reduction in what the
            // encryption proves about who is on the other end. Not blocking, because
            // commissioning an interface against such a box has to be possible.
            problems.Add(
                "Warning: any certificate will be accepted, so the connection is encrypted " +
                "but the far end is not authenticated.");
        }

        return problems;
    }

    /// <summary>The problems that stop this destination being used at all.</summary>
    public IReadOnlyList<string> CheckBlocking()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("A destination needs a name.");
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            problems.Add("A destination needs a host name or address.");
        }

        if (Port is < 1 or > 65535)
        {
            problems.Add($"{Port} is not a port number.");
        }

        if (ConnectTimeoutSeconds < 1 || ReceiveTimeoutSeconds < 1)
        {
            problems.Add("Timeouts are at least one second.");
        }

        if (RetryCount < 0)
        {
            problems.Add("A number of retries below zero means nothing.");
        }

        return problems;
    }
}
