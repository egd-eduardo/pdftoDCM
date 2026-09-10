using System.Text;

namespace PdfToDicom.Core.Licensing;

/// <summary>
/// The information carried inside a licence code, before it is signed.
/// </summary>
/// <remarks>
/// The payload is deliberately packed into a compact binary form rather than JSON. The
/// signature alone is 64 bytes, so every byte saved in the payload shortens the code that
/// a customer has to paste into the Settings dialog.
/// </remarks>
public sealed class LicencePayload
{
    /// <summary>Format version, so a future change can be recognised rather than misread.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Longest licensee name that fits the single length byte.</summary>
    public const int MaxLicenseeLength = 200;

    /// <summary>Length of the truncated machine hash carried by a bound licence.</summary>
    public const int MachineHashLength = 8;

    /// <summary>
    /// Dates are stored as whole days since this epoch, which keeps them to four bytes
    /// and removes any time zone ambiguity from a licence that is valid by the day.
    /// </summary>
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const byte FlagMachineBound = 0b0000_0001;
    private const byte FlagHasExpiry = 0b0000_0010;

    /// <summary>Organisation the licence was issued to.</summary>
    public string Licensee { get; set; } = string.Empty;

    /// <summary>Date the licence was issued.</summary>
    public DateTime IssuedOn { get; set; } = DateTime.UtcNow.Date;

    /// <summary>Expiry date, or null for a perpetual licence.</summary>
    public DateTime? ExpiresOn { get; set; }

    /// <summary>Number of workstations covered. Recorded for the record, not enforced by the application.</summary>
    public int Seats { get; set; }

    /// <summary>
    /// Truncated hash of the machine this licence is tied to, or null when the licence
    /// may be used on any machine.
    /// </summary>
    public byte[]? MachineHash { get; set; }

    /// <summary>True when the licence names a specific machine.</summary>
    public bool IsMachineBound => MachineHash is { Length: MachineHashLength };

    /// <summary>
    /// Packs the payload into its binary form. This is the exact byte sequence that gets
    /// signed, so encoding must be stable across versions of the tool and the application.
    /// </summary>
    public byte[] ToBytes()
    {
        byte[] licensee = Encoding.UTF8.GetBytes(Licensee ?? string.Empty);

        if (licensee.Length > MaxLicenseeLength)
        {
            throw new ArgumentException(
                $"The licensee name must not exceed {MaxLicenseeLength} bytes when encoded as UTF-8.",
                nameof(Licensee));
        }

        byte flags = 0;
        if (IsMachineBound)
        {
            flags |= FlagMachineBound;
        }

        if (ExpiresOn.HasValue)
        {
            flags |= FlagHasExpiry;
        }

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        writer.Write(CurrentVersion);
        writer.Write(flags);
        writer.Write(ToDayNumber(IssuedOn));
        writer.Write(ExpiresOn.HasValue ? ToDayNumber(ExpiresOn.Value) : 0u);
        writer.Write((ushort)Math.Clamp(Seats, 0, ushort.MaxValue));
        writer.Write((byte)licensee.Length);
        writer.Write(licensee);

        if (IsMachineBound)
        {
            writer.Write(MachineHash!);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>
    /// Unpacks a payload produced by <see cref="ToBytes"/>.
    /// </summary>
    /// <returns>The payload, or null when the bytes are not a payload this version understands.</returns>
    public static LicencePayload? FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            using var buffer = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);

            byte version = reader.ReadByte();
            if (version != CurrentVersion)
            {
                return null;
            }

            byte flags = reader.ReadByte();
            uint issued = reader.ReadUInt32();
            uint expires = reader.ReadUInt32();
            ushort seats = reader.ReadUInt16();
            byte licenseeLength = reader.ReadByte();

            byte[] licensee = reader.ReadBytes(licenseeLength);
            if (licensee.Length != licenseeLength)
            {
                return null;
            }

            byte[]? machineHash = null;
            if ((flags & FlagMachineBound) != 0)
            {
                machineHash = reader.ReadBytes(MachineHashLength);
                if (machineHash.Length != MachineHashLength)
                {
                    return null;
                }
            }

            return new LicencePayload
            {
                Licensee = Encoding.UTF8.GetString(licensee),
                IssuedOn = FromDayNumber(issued),
                ExpiresOn = (flags & FlagHasExpiry) != 0 ? FromDayNumber(expires) : null,
                Seats = seats,
                MachineHash = machineHash
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException)
        {
            // A truncated or corrupt code is simply not a licence. It is never an error
            // that should reach the operator as an exception.
            return null;
        }
    }

    private static uint ToDayNumber(DateTime value) =>
        (uint)Math.Max(0, (value.Date - Epoch.Date).TotalDays);

    private static DateTime FromDayNumber(uint days) =>
        Epoch.AddDays(days).Date;
}
