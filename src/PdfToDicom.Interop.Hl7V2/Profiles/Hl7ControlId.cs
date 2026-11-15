using System.Security.Cryptography;

namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// Produces the identifier that goes in MSH-10 and comes back in an acknowledgement.
/// </summary>
/// <remarks>
/// <para>
/// It has to be unique, short enough for a field of twenty characters, and traceable back
/// to a moment in time when somebody is reading a log. A counter alone repeats after a
/// restart and a timestamp alone repeats when two messages are built in the same second,
/// so the two are combined.
/// </para>
/// <para>
/// A counter that starts at zero is not enough, which was found by the interoperability
/// script rather than reasoned about: two processes starting in the same second each
/// produced <c>...0001</c>, so two workstations, or two runs of the command line a moment
/// apart, could send different documents under the same identifier. A receiver that
/// discards a repeated MSH-10, which is a reasonable thing for a receiver to do, would then
/// drop the second report. The counter therefore starts somewhere random.
/// </para>
/// <para>
/// The sequence is four characters of base 36 rather than four digits. Both fit the field;
/// decimal would have left a one in ten thousand chance that two processes in the same
/// second agreed, and this leaves one in 1,679,616 for the same width. A wholly random
/// identifier would also be unique and would tell a reader nothing.
/// </para>
/// </remarks>
public sealed class Hl7ControlIdGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int SequenceLength = 4;
    private const int SequenceSpace = 36 * 36 * 36 * 36;

    private readonly Func<DateTimeOffset> _clock;
    private int _counter;

    /// <summary>Creates a generator.</summary>
    /// <param name="clock">The clock, or the system one.</param>
    /// <param name="startAt">
    /// Where the sequence starts. Random when omitted, which is what keeps two processes
    /// apart; a test supplies one so that it can prove the behaviour rather than observe it
    /// probably happening.
    /// </param>
    public Hl7ControlIdGenerator(Func<DateTimeOffset>? clock = null, int? startAt = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _counter = startAt ?? RandomNumberGenerator.GetInt32(SequenceSpace);
    }

    /// <summary>The next identifier, with an optional prefix from the profile.</summary>
    public string Next(string prefix = "")
    {
        // Unsigned, because the counter wraps past int.MaxValue in a long running process
        // and a negative remainder would produce an identifier with no digits in it at all.
        uint sequence = unchecked((uint)Interlocked.Increment(ref _counter)) % SequenceSpace;

        return $"{prefix}{_clock():yyyyMMddHHmmss}{Encode(sequence)}";
    }

    private static string Encode(uint value)
    {
        var characters = new char[SequenceLength];

        for (int position = SequenceLength - 1; position >= 0; position--)
        {
            characters[position] = Alphabet[(int)(value % 36)];
            value /= 36;
        }

        return new string(characters);
    }
}
