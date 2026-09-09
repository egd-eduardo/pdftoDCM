using FellowOakDicom;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Performs the one time process wide configuration that the DICOM toolkit requires.
/// </summary>
/// <remarks>
/// <para>
/// The Implementation Class UID and Implementation Version Name identify the software
/// that produced an object. The toolkit rebuilds the file meta information group from
/// its own static values every time a file is saved, and it uses the same values when
/// negotiating an association, so these must be set globally rather than per object.
/// Leaving the toolkit defaults in place would cause every archive to attribute this
/// application's objects to the underlying library instead, which makes tracing an
/// instance back to its source impossible.
/// </para>
/// <para>
/// The method is idempotent and safe to call from several threads, so every component
/// that depends on the configuration can simply call it in its constructor rather than
/// relying on the host to remember.
/// </para>
/// </remarks>
public static class DicomRuntime
{
    private static readonly object InitialisationLock = new();
    private static bool _initialised;

    /// <summary>
    /// Applies the process wide DICOM configuration if it has not already been applied.
    /// </summary>
    /// <param name="implementationClassUid">
    /// Implementation Class UID to advertise. Defaults to this application's own UID.
    /// </param>
    /// <param name="implementationVersionName">
    /// Implementation Version Name to advertise. The standard limits this attribute to
    /// 16 characters, so a longer value is truncated rather than being rejected later.
    /// </param>
    public static void EnsureInitialized(
        string? implementationClassUid = null,
        string? implementationVersionName = null)
    {
        if (_initialised)
        {
            return;
        }

        lock (InitialisationLock)
        {
            if (_initialised)
            {
                return;
            }

            string classUid = string.IsNullOrWhiteSpace(implementationClassUid)
                ? UidGenerator.DefaultImplementationClassUid
                : implementationClassUid.Trim();

            string versionName = string.IsNullOrWhiteSpace(implementationVersionName)
                ? UidGenerator.DefaultImplementationVersionName
                : implementationVersionName.Trim();

            if (versionName.Length > 16)
            {
                versionName = versionName[..16];
            }

            DicomImplementation.ClassUID = new DicomUID(
                classUid,
                "PDF to DICOM Implementation Class UID",
                DicomUidType.Unknown);

            DicomImplementation.Version = versionName;

            // fo-dicom resolves its network client and transcoder implementations through
            // a service provider. Registering the default services here means the desktop
            // application, the command line tool and the tests all obtain a working
            // network stack without each having to repeat the setup.
            new DicomSetupBuilder()
                .RegisterServices(services => services.AddFellowOakDicom())
                .SkipValidation()
                .Build();

            _initialised = true;
        }
    }
}
