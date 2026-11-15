namespace PdfToDicom.Interop.Hl7V2.Transport;

/// <summary>
/// Everything one MLLP destination needs, and nothing about what is sent to it.
/// </summary>
/// <remarks>
/// The shape follows the DICOM side's destination deliberately: a name, a host, a port,
/// timeouts and a TLS choice. An administrator who has configured an archive should
/// recognise this page rather than meet a second way of saying the same things.
/// </remarks>
public sealed record MllpOptions
{
    /// <summary>Creates the options for one destination.</summary>
    public MllpOptions(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        Host = host;
        Port = port;
    }

    /// <summary>The host name or address of the receiving system.</summary>
    public string Host { get; }

    /// <summary>The TCP port it listens on.</summary>
    public int Port { get; }

    /// <summary>A name for the destination, shown to the operator and recorded.</summary>
    public string Name { get; init; } = "HL7 destination";

    /// <summary>How long to wait for the connection to open.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 10;

    /// <summary>How long to wait for a reply, where one is expected.</summary>
    public int ReceiveTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether this destination replies at all.
    /// </summary>
    /// <remarks>
    /// A profile declaring NE in both acknowledgement fields does not reply, and waiting
    /// for a reply that will never come would turn every successful transmission into a
    /// timeout. The default is to expect one, because most interfaces do and a silent
    /// assumption the other way would hide a receiver that had stopped answering.
    /// </remarks>
    public bool ExpectAcknowledgement { get; init; } = true;

    /// <summary>
    /// The largest reply this transport will read before refusing.
    /// </summary>
    /// <remarks>
    /// An acknowledgement is a few hundred bytes. The limit is generous rather than tight
    /// so that a verbose error acknowledgement is not lost, and finite so that a peer
    /// sending bytes without end cannot exhaust this process.
    /// </remarks>
    public int MaximumAcknowledgementBytes { get; init; } = 1024 * 1024;
}
