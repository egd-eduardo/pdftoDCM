using System.Numerics;
using System.Security.Cryptography;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Produces DICOM Unique Identifiers using either a UUID derived form or the publisher's
/// registered UID root.
/// </summary>
/// <remarks>
/// <para>
/// The default strategy follows DICOM PS3.5 Annex B.2: a version 4 UUID is converted to
/// its 128 bit unsigned integer value and appended to the "2.25." prefix. This yields a
/// UID that is globally unique by construction, requires no registration, and cannot
/// collide with identifiers produced by any other system.
/// </para>
/// <para>
/// The alternative strategy appends a timestamp, an entity discriminator and a
/// cryptographically random component to <see cref="RegisteredUidRoot"/>, the root
/// registered to the publisher. The components are combined so that the total length
/// never exceeds the 64 character limit imposed by the UI Value Representation.
/// </para>
/// </remarks>
public sealed class UidGenerator : IUidGenerator
{
    /// <summary>
    /// Implementation Class UID identifying this software. It sits beneath the same
    /// UUID derived arc so that it needs no registered root, and it is a constant so that
    /// an archive can recognise objects produced by this application.
    /// </summary>
    public const string DefaultImplementationClassUid = "2.25.164837465109381728394657283910473625";

    /// <summary>
    /// Implementation Version Name. The standard limits this to 16 characters, so the
    /// value is kept deliberately short.
    /// </summary>
    public const string DefaultImplementationVersionName = "PDFTODICOM_2_0";

    /// <summary>
    /// The UID root registered to the author of this software, beneath which every UID
    /// produced by the registered root strategy is created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issued by Medical Connections Ltd. It is fixed in the software and cannot be
    /// configured, because it identifies the publisher rather than the site: a hospital
    /// generating UIDs beneath someone else's registered arc would be claiming an
    /// identifier space it does not own.
    /// </para>
    /// <para>
    /// Two conditions attach to the registration, and both are met here. UIDs beneath the
    /// prefix must not be capable of duplication, which is why the suffix combines a
    /// millisecond timestamp with a large cryptographically random component and why the
    /// uniqueness of that construction is asserted by the test suite. Sub-delegation of
    /// ranges is permitted where it is not for profit or forms part of another product;
    /// this is the latter, and the software is supplied free of charge.
    /// </para>
    /// <para>
    /// It is not a secret. Every DICOM object this software produces carries UIDs built
    /// from it, so anyone holding one of those objects can read it. The settings dialog
    /// shows it partly masked to discourage it being copied off a screen, which is a
    /// different and much weaker claim.
    /// </para>
    /// </remarks>
    public const string RegisteredUidRoot = "1.2.826.0.1.3680043.9.6008";

    /// <summary>
    /// How the registered root is shown in the user interface: the arcs assigned to this
    /// publisher are replaced by asterisks, leaving the issuer's own public arc legible so
    /// that a PACS administrator can still see which registry it comes from.
    /// </summary>
    public const string RegisteredUidRootForDisplay = "1.2.826.0.1.3680043.*.****";

    /// <summary>
    /// Exclusive upper bound of the random component of a registered root UID. Fifteen
    /// decimal digits give a thousand million million values within a single millisecond,
    /// which is what makes duplication beneath the registered prefix infeasible rather
    /// than merely unlikely, while keeping the component to a known maximum width.
    /// </summary>
    private const ulong RandomComponentBound = 1_000_000_000_000_000UL;

    /// <summary>Width of the timestamp component, "yyyyMMddHHmmssfff".</summary>
    private const int TimestampWidth = 17;

    /// <summary>Maximum width of the random component, derived from its bound.</summary>
    private const int RandomComponentWidth = 15;

    /// <summary>
    /// Longest organisation root that still leaves room for a suffix.
    /// </summary>
    /// <remarks>
    /// The suffix is a dot, a single digit discriminator, a dot, the timestamp, a dot and
    /// the random component. Deriving the budget from those widths rather than hard coding
    /// it means a change to any component cannot silently produce an over long UID.
    /// </remarks>
    private const int MaximumRootLength =
        DicomValueRules.MaxUidLength - (1 + 1 + 1 + TimestampWidth + 1 + RandomComponentWidth);

    private readonly UidGenerationStrategy _strategy;
    private readonly string _organizationRoot;

    /// <summary>
    /// Creates a generator.
    /// </summary>
    /// <param name="strategy">Which construction method to use.</param>
    /// <param name="organizationUidRoot">
    /// Root to build beneath when the registered root strategy is selected. Leave it unset
    /// for <see cref="RegisteredUidRoot"/>, which is what the application always does; it
    /// is accepted only so that the format and length budget can be exercised directly.
    /// Ignored by the UUID derived strategy.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The registered root strategy was requested with a root that is missing, malformed
    /// or too long to leave room for a unique suffix.
    /// </exception>
    public UidGenerator(
        UidGenerationStrategy strategy = UidGenerationStrategy.UuidDerived,
        string? organizationUidRoot = null)
    {
        _strategy = strategy;

        // An unspecified root means the publisher's registered one. That is the only path
        // the application itself uses; the parameter remains so that the length and format
        // budget can still be exercised by the tests.
        string requested = (organizationUidRoot ?? string.Empty).Trim().TrimEnd('.');
        _organizationRoot = requested.Length == 0 && strategy == UidGenerationStrategy.RegisteredRoot
            ? RegisteredUidRoot
            : requested;

        if (_strategy == UidGenerationStrategy.RegisteredRoot)
        {
            if (!DicomValueRules.IsValidUid(_organizationRoot, out string error))
            {
                throw new ArgumentException(
                    $"The configured organisation UID root is not usable. {error}",
                    nameof(organizationUidRoot));
            }

            if (_organizationRoot.Length > MaximumRootLength)
            {
                throw new ArgumentException(
                    $"The organisation UID root must not exceed {MaximumRootLength} characters, " +
                    "otherwise the generated UIDs would breach the 64 character limit.",
                    nameof(organizationUidRoot));
            }
        }
    }

    /// <summary>Creates a generator from the supplied configuration.</summary>
    /// <remarks>
    /// <see cref="AppConfiguration.OrganizationUidRoot"/> is deliberately not consulted.
    /// The registered root is a property of the publisher, not of the site, so it is fixed
    /// in the software; a value left in an older configuration file is ignored rather than
    /// obeyed.
    /// </remarks>
    public static UidGenerator FromConfiguration(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new UidGenerator(configuration.UidStrategy);
    }

    /// <inheritdoc />
    public string ImplementationClassUid => DefaultImplementationClassUid;

    /// <inheritdoc />
    public string ImplementationVersionName => DefaultImplementationVersionName;

    /// <inheritdoc />
    public string NewStudyInstanceUid() => Generate("1");

    /// <inheritdoc />
    public string NewSeriesInstanceUid() => Generate("2");

    /// <inheritdoc />
    public string NewSopInstanceUid() => Generate("3");

    /// <summary>
    /// Produces one new UID.
    /// </summary>
    /// <param name="entityDiscriminator">
    /// A single digit distinguishing study, series and instance identifiers when the
    /// registered root strategy is used. It has no effect on the UUID derived strategy,
    /// where the UUID alone guarantees uniqueness.
    /// </param>
    private string Generate(string entityDiscriminator)
    {
        string uid = _strategy == UidGenerationStrategy.RegisteredRoot
            ? GenerateFromRegisteredRoot(entityDiscriminator)
            : GenerateFromUuid();

        // A defect here would be silent and damaging, so the invariant is asserted rather
        // than assumed.
        if (!DicomValueRules.IsValidUid(uid, out string error))
        {
            throw new InvalidOperationException(
                $"The UID generator produced a value that is not standard conformant ('{uid}'). {error}");
        }

        return uid;
    }

    /// <summary>
    /// Builds a UID of the form 2.25.&lt;integer&gt; where the integer is the unsigned
    /// 128 bit value of a freshly created version 4 UUID, as permitted by PS3.5 B.2.
    /// </summary>
    private static string GenerateFromUuid()
    {
        Guid guid = Guid.NewGuid();

        // The UUID is interpreted as a single big endian 128 bit unsigned integer. A
        // trailing zero byte forces BigInteger to treat the value as positive.
        Span<byte> bigEndian = stackalloc byte[16];
        WriteUuidBigEndian(guid, bigEndian);

        Span<byte> unsigned = stackalloc byte[17];
        for (int i = 0; i < 16; i++)
        {
            unsigned[i] = bigEndian[15 - i];
        }

        unsigned[16] = 0;

        var value = new BigInteger(unsigned);
        return DicomConstants.UuidDerivedUidPrefix + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes the canonical big endian byte order of a UUID, which differs from the
    /// little endian layout that <see cref="Guid.ToByteArray()"/> produces on Windows.
    /// </summary>
    private static void WriteUuidBigEndian(Guid guid, Span<byte> destination)
    {
        byte[] bytes = guid.ToByteArray();

        // Reverse the first three components, which are stored little endian.
        destination[0] = bytes[3];
        destination[1] = bytes[2];
        destination[2] = bytes[1];
        destination[3] = bytes[0];
        destination[4] = bytes[5];
        destination[5] = bytes[4];
        destination[6] = bytes[7];
        destination[7] = bytes[6];

        // The remaining eight bytes are already in network order.
        for (int i = 8; i < 16; i++)
        {
            destination[i] = bytes[i];
        }
    }

    /// <summary>
    /// Builds a UID beneath the organisation's registered root. The suffix combines the
    /// current UTC time to the millisecond, the discriminator and 64 bits of
    /// cryptographic randomness, which makes a collision infeasible even when several
    /// workstations generate identifiers in the same millisecond.
    /// </summary>
    private string GenerateFromRegisteredRoot(string entityDiscriminator)
    {
        string timestamp = DateTime.UtcNow.ToString(
            "yyyyMMddHHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture);

        // RandomNumberGenerator is used rather than Random so that identifiers generated
        // by concurrent processes started at the same instant cannot coincide. The value
        // is reduced modulo the bound so the component has a known maximum width, which
        // is what keeps the whole UID inside the 64 character limit.
        ulong randomComponent = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) % RandomComponentBound;

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{_organizationRoot}.{entityDiscriminator}.{timestamp}.{randomComponent}");
    }
}
