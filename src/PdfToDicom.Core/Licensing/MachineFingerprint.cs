using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace PdfToDicom.Core.Licensing;

/// <summary>
/// Derives a stable identifier for the workstation, so a licence can be tied to it.
/// </summary>
/// <remarks>
/// <para>
/// The identifier combines the machine name with the hardware address of a physical
/// network adapter. Both are stable across restarts, reinstalls of the
/// application and changes of user, but will change if the network card is replaced or
/// the machine is renamed. A small proportion of licences will therefore need reissuing
/// over the life of a deployment, which is expected and is why the reissue path is
/// documented.
/// </para>
/// <para>
/// This is a deterrent, not a secret. Anyone able to modify the application could report
/// a different identifier. Its purpose is to stop a licence being copied from one
/// workstation to twenty, which is the realistic risk.
/// </para>
/// </remarks>
public static class MachineFingerprint
{
    /// <summary>Displayed with this many characters, grouped for legibility.</summary>
    private const int DisplayCharacters = 16;

    private static string? _cachedId;
    private static byte[]? _cachedHash;
    private static readonly object CacheLock = new();

    /// <summary>
    /// The identifier shown in Settings and quoted when requesting a bound licence.
    /// Formatted in groups of four so it can be read aloud or typed without error.
    /// </summary>
    public static string Current
    {
        get
        {
            lock (CacheLock)
            {
                return _cachedId ??= Format(CachedHash());
            }
        }
    }

    /// <summary>
    /// The truncated hash that a machine bound licence carries, matching
    /// <see cref="LicencePayload.MachineHashLength"/>.
    /// </summary>
    public static byte[] CurrentHash() =>
        CachedHash()[..LicencePayload.MachineHashLength];

    /// <summary>
    /// The full hash, computed once. Enumerating network adapters costs milliseconds and
    /// the answer cannot change while the process is running, so it is not repeated on
    /// every licence check.
    /// </summary>
    private static byte[] CachedHash()
    {
        lock (CacheLock)
        {
            return _cachedHash ??= ComputeHash();
        }
    }

    /// <summary>
    /// Recomputes the hash for a supplied identifier, so the licence generator can produce
    /// a bound licence from an identifier a customer has quoted, without needing access to
    /// their machine.
    /// </summary>
    public static byte[] HashOfDisplayedId(string displayedId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayedId);

        // The displayed form is the hash itself, so it is decoded rather than rehashed.
        string cleaned = new(displayedId.Where(char.IsAsciiLetterOrDigit).ToArray());

        if (cleaned.Length != DisplayCharacters)
        {
            throw new ArgumentException(
                $"A machine identifier is {DisplayCharacters} characters once separators are removed, " +
                $"but '{displayedId}' has {cleaned.Length}.",
                nameof(displayedId));
        }

        var bytes = new byte[LicencePayload.MachineHashLength];

        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    /// <summary>Confirms that a licence's machine hash matches this workstation.</summary>
    public static bool Matches(byte[]? machineHash)
    {
        if (machineHash is null || machineHash.Length != LicencePayload.MachineHashLength)
        {
            return false;
        }

        // A fixed time comparison is not required here, because the value is not a secret
        // and there is no oracle to exploit, but it costs nothing.
        return CryptographicOperations.FixedTimeEquals(machineHash, CurrentHash());
    }

    /// <summary>
    /// Hashes the machine name together with a physical adapter's hardware address.
    /// Failure to read the adapter is not fatal: the name alone still yields a usable,
    /// if weaker, identifier.
    /// </summary>
    private static byte[] ComputeHash()
    {
        var material = new StringBuilder();

        try
        {
            material.Append(Environment.MachineName);
        }
        catch
        {
            material.Append("unknown-machine");
        }

        material.Append('|').Append(TryGetPrimaryAdapterAddress() ?? "no-adapter");

        return SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
    }

    /// <summary>
    /// Returns one hardware address from the adapters that look physical, ignoring
    /// loopback and tunnel interfaces, whose addresses are not machine specific. The
    /// candidates are ordered before one is taken, because the order the operating system
    /// reports adapters in is not guaranteed to be stable, and an identifier that changed
    /// between restarts would be useless.
    /// </summary>
    private static string? TryGetPrimaryAdapterAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.NetworkInterfaceType
                    is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(nic => nic.GetPhysicalAddress().ToString())
                .Where(address => !string.IsNullOrEmpty(address) && address.Any(c => c != '0'))
                .OrderBy(address => address, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch
        {
            // Some locked down environments deny enumeration of network interfaces.
            return null;
        }
    }

    /// <summary>Renders the leading bytes of the hash as hexadecimal, in groups of four.</summary>
    private static string Format(byte[] hash)
    {
        string hex = Convert.ToHexString(hash[..(DisplayCharacters / 2)]);

        return string.Join(
            '-',
            Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }
}
