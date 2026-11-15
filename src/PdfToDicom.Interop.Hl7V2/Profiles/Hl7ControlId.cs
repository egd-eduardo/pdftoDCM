namespace PdfToDicom.Interop.Hl7V2.Profiles;

/// <summary>
/// Produces the identifier that goes in MSH-10 and comes back in an acknowledgement.
/// </summary>
/// <remarks>
/// It has to be unique, short enough for a field of twenty characters, and traceable back
/// to a moment in time when somebody is reading a log. A counter alone repeats after a
/// restart; a timestamp alone repeats when two messages are built in the same second; the
/// two together do neither. A random identifier would also be unique and would tell a
/// reader nothing.
/// </remarks>
public sealed class Hl7ControlIdGenerator
{
    private readonly Func<DateTimeOffset> _clock;
    private int _counter;

    /// <summary>Creates a generator, optionally with a clock of your choosing.</summary>
    public Hl7ControlIdGenerator(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The next identifier, with an optional prefix from the profile.</summary>
    public string Next(string prefix = "")
    {
        int sequence = Interlocked.Increment(ref _counter) % 10000;

        return $"{prefix}{_clock():yyyyMMddHHmmss}{sequence:D4}";
    }
}
