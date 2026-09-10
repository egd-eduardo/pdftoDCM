using System.Security.Cryptography;
using PdfToDicom.Core.Abstractions;

namespace PdfToDicom.Core.Licensing;

/// <summary>
/// Validates licence codes against the publisher's public key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Licensing is dormant until a public key is embedded.</b> A build whose
/// <see cref="PublisherPublicKey"/> is blank reports <see cref="LicenceState.NotEnforced"/>
/// and every feature works exactly as it would with no licensing code present at all.
/// That is deliberate: adding this subsystem must not be able to change the behaviour of
/// a working installation until the publisher chooses to switch it on.
/// </para>
/// <para>
/// Every failure path returns a status rather than raising. A licensing subsystem that
/// can throw is a licensing subsystem that can stop a clinician sending a report, which
/// would be a far worse outcome than an unlicensed copy.
/// </para>
/// </remarks>
public sealed class LicenceService : ILicenceService
{
    /// <summary>
    /// Days after expiry during which transmission still works, with a warning.
    /// </summary>
    /// <remarks>
    /// A licence that stops working at midnight, in a hospital, is a clinical incident
    /// rather than a commercial success. The grace period gives a site time to renew
    /// without anyone being unable to send a report.
    /// </remarks>
    public const int GracePeriodDays = 14;

    // Dates in these messages are written as yyyy-MM-dd. The current culture's short date
    // renders the same day as 09/10/2027 in one locale and 10/09/2027 in another, and an
    // operator reading a licence expiry has no way to tell which convention is in use.

    /// <summary>
    /// The publisher's ECDSA P-256 public key, in base64 encoded SubjectPublicKeyInfo form.
    /// </summary>
    /// <remarks>
    /// <b>Blank by default, which leaves licensing switched off.</b>
    ///
    /// To switch it on, run the licence generator once:
    ///
    ///     pdftodicom-licence keygen
    ///
    /// It writes the private key to a file, which must be kept secret and never committed,
    /// and prints the public key to paste here. Rebuild, and the application will then
    /// require a licence code before it will transmit.
    /// </remarks>
    public const string PublisherPublicKey = "";

    private readonly IAppLogger? _logger;
    private readonly string _publicKey;
    private readonly Func<DateTime> _nowProvider;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="logger">Optional diagnostic log.</param>
    /// <param name="publicKeyOverride">
    /// Public key to use instead of the compiled in one. Used only by the automated tests,
    /// which need to sign with a key pair they generate themselves.
    /// </param>
    /// <param name="nowProvider">
    /// Supplies the current date, so the expiry and grace period rules can be tested
    /// deterministically.
    /// </param>
    public LicenceService(
        IAppLogger? logger = null,
        string? publicKeyOverride = null,
        Func<DateTime>? nowProvider = null)
    {
        _logger = logger;
        _publicKey = publicKeyOverride ?? PublisherPublicKey;
        _nowProvider = nowProvider ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public bool IsEnforced => !string.IsNullOrWhiteSpace(_publicKey);

    /// <inheritdoc />
    public string MachineId => MachineFingerprint.Current;

    /// <inheritdoc />
    public LicenceStatus Check(string? licenceCode)
    {
        // ---------------------------------------------------------- Dormant
        if (!IsEnforced)
        {
            return new LicenceStatus(
                LicenceState.NotEnforced, null, null, 0, false,
                "Licensing is not enabled in this build.");
        }

        // ---------------------------------------------------------- Present?
        if (string.IsNullOrWhiteSpace(licenceCode))
        {
            return new LicenceStatus(
                LicenceState.Missing, null, null, 0, false,
                "No licence code has been entered. Documents can be generated and saved, " +
                "but not transmitted. Enter your licence code in Settings.");
        }

        try
        {
            return CheckCore(licenceCode);
        }
        catch (Exception ex)
        {
            // Nothing about a licence check should ever be able to take the application
            // down, so an unanticipated fault degrades to "invalid" and is logged.
            _logger?.Error("The licence code could not be checked.", ex);

            return new LicenceStatus(
                LicenceState.Invalid, null, null, 0, false,
                "The licence code could not be checked. Contact your supplier.");
        }
    }

    private LicenceStatus CheckCore(string licenceCode)
    {
        // ---------------------------------------------------------- Usable key?
        // Checked before anything else, so that a build packaged with a broken key is
        // diagnosed as a build problem rather than reported to the customer as a bad
        // code. Refusing to transmit on the basis of our own packaging mistake would
        // punish the wrong party.
        using ECDsa? publicKey = TryImportPublicKey();

        if (publicKey is null)
        {
            _logger?.Error(
                "The publisher public key compiled into this build is not valid, so licence " +
                "codes cannot be checked. Licensing has been treated as disabled.");

            return new LicenceStatus(
                LicenceState.NotEnforced, null, null, 0, false,
                "Licensing could not be initialised in this build.");
        }

        // ---------------------------------------------------------- Well formed?
        if (!LicenceCodec.TryDecode(licenceCode, out byte[] payloadBytes, out byte[] signature))
        {
            return new LicenceStatus(
                LicenceState.Invalid, null, null, 0, false,
                "The licence code is not in the expected format. Check that it was copied in full.");
        }

        // ---------------------------------------------------------- Genuine?
        if (!LicenceCodec.Verify(payloadBytes, signature, publicKey))
        {
            return new LicenceStatus(
                LicenceState.Invalid, null, null, 0, false,
                "The licence code is not valid for this product. Check it with your supplier.");
        }

        // ---------------------------------------------------------- Readable?
        LicencePayload? payload = LicencePayload.FromBytes(payloadBytes);

        if (payload is null)
        {
            return new LicenceStatus(
                LicenceState.Invalid, null, null, 0, false,
                "The licence code was issued for a different version of this product.");
        }

        // ---------------------------------------------------------- Right machine?
        if (payload.IsMachineBound && !MachineFingerprint.Matches(payload.MachineHash))
        {
            return new LicenceStatus(
                LicenceState.WrongMachine, payload.Licensee, payload.ExpiresOn, payload.Seats, true,
                $"This licence is registered to a different workstation. This machine is " +
                $"{MachineFingerprint.Current}. Quote that identifier to your supplier to have " +
                "the licence reissued.");
        }

        // ---------------------------------------------------------- In date?
        DateTime today = _nowProvider().Date;

        if (payload.ExpiresOn is { } expiry)
        {
            if (today > expiry.AddDays(GracePeriodDays))
            {
                return new LicenceStatus(
                    LicenceState.Expired, payload.Licensee, expiry, payload.Seats, payload.IsMachineBound,
                    $"The licence expired on {expiry:yyyy-MM-dd} and the grace period has ended. " +
                    "Documents can still be generated and saved, but not transmitted.");
            }

            if (today > expiry)
            {
                int remaining = GracePeriodDays - (today - expiry).Days;

                return new LicenceStatus(
                    LicenceState.ExpiringSoon, payload.Licensee, expiry, payload.Seats, payload.IsMachineBound,
                    $"The licence expired on {expiry:yyyy-MM-dd}. Transmission continues for a further " +
                    $"{remaining} day(s). Please renew.");
            }

            if ((expiry - today).TotalDays <= 30)
            {
                return new LicenceStatus(
                    LicenceState.Valid, payload.Licensee, expiry, payload.Seats, payload.IsMachineBound,
                    $"Licensed to {payload.Licensee}. Expires on {expiry:yyyy-MM-dd}.");
            }
        }

        return new LicenceStatus(
            LicenceState.Valid, payload.Licensee, payload.ExpiresOn, payload.Seats, payload.IsMachineBound,
            payload.ExpiresOn is { } end
                ? $"Licensed to {payload.Licensee} until {end:yyyy-MM-dd}."
                : $"Licensed to {payload.Licensee}.");
    }

    /// <summary>
    /// Imports the compiled in public key, returning null when it cannot be read rather
    /// than raising.
    /// </summary>
    private ECDsa? TryImportPublicKey()
    {
        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_publicKey), out _);
            return key;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
