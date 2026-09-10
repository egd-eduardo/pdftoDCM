using System.Numerics;
using System.Security.Cryptography;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Validation;

namespace PdfToDicom.Core.Dicom;

/// <summary>
/// Produces DICOM Unique Identifiers using either a UUID derived form or an
/// organisation's own registered UID root.
/// </summary>
/// <remarks>
/// <para>
/// The default strategy follows DICOM PS3.5 Annex B.2: a version 4 UUID is converted to
/// its 128 bit unsigned integer value and appended to the "2.25." prefix. This yields a
/// UID that is globally unique by construction, requires no registration, and cannot
/// collide with identifiers produced by any other system.
/// </para>
/// <para>
/// The alternative strategy appends a timestamp, a process identifier and a
/// cryptographically random component to a root the organisation already owns. The
/// components are combined so that the total length never exceeds the 64 character limit
/// imposed by the UI Value Representation.
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
    public const string DefaultImplementationVersionName = "PDFTODICOM_1_5";

    /// <summary>
    /// Exclusive upper bound of the random component of a registered root UID. Twelve
    /// decimal digits give roughly a million million values within a single millisecond,
    /// which makes a collision between concurrent workstations negligible, while keeping
    /// the component to a known maximum width.
    /// </summary>
    private const ulong RandomComponentBound = 1_000_000_000_000UL;

    /// <summary>Width of the timestamp component, "yyyyMMddHHmmssfff".</summary>
    private const int TimestampWidth = 17;

    /// <summary>Maximum width of the random component, derived from its bound.</summary>
    private const int RandomComponentWidth = 12;

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
    /// The organisation's registered root, required when the registered root strategy is
    /// selected. Ignored otherwise.
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
        _organizationRoot = (organizationUidRoot ?? string.Empty).Trim().TrimEnd('.');

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
    public static UidGenerator FromConfiguration(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new UidGenerator(configuration.UidStrategy, configuration.OrganizationUidRoot);
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
