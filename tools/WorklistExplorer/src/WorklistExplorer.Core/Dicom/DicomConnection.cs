using FellowOakDicom.Network.Client;
using FellowOakDicom.Network.Tls;
using WorklistExplorer.Core.Query;

namespace WorklistExplorer.Core.Dicom;

/// <summary>
/// The only place a fo-dicom client is created, so the whole network surface of the tool
/// can be audited in one file.
/// </summary>
/// <remarks>
/// The tool only ever adds DicomCEchoRequest and DicomCFindRequest to a client. There is
/// no code path that sends C-STORE, N-CREATE, N-SET or any MPPS message, so nothing on a
/// hospital system can be changed by it.
/// </remarks>
internal static class DicomConnection
{
    public static IDicomClient Create(QueryTarget target)
    {
        IDicomClient client;
        if (target.UseTls && target.AcceptAnyTlsCertificate)
        {
            // Only used when the configuration explicitly asked for it, mirroring the PDF
            // to DICOM application, and stated in the association line of the report.
            var tls = new DefaultTlsInitiator { IgnoreSslPolicyErrors = true };
            client = DicomClientFactory.Create(target.Host, target.Port, tls, target.CallingAeTitle, target.CalledAeTitle);
        }
        else
        {
            client = DicomClientFactory.Create(target.Host, target.Port, target.UseTls, target.CallingAeTitle, target.CalledAeTitle);
        }

        client.ClientOptions.AssociationRequestTimeoutInMs = Math.Max(1, target.TimeoutSeconds) * 1000;
        return client;
    }

    public static string DescribeAssociation(QueryTarget t) =>
        $"calling AE '{t.CallingAeTitle}', called AE '{t.CalledAeTitle}', host {t.Host}, port {t.Port}, " +
        (t.UseTls ? (t.AcceptAnyTlsCertificate ? "TLS accepting any certificate" : "TLS with certificate validation") : "no TLS") +
        $", inactivity timeout {t.TimeoutSeconds} s";
}
