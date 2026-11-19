using System.Globalization;

namespace WorklistExplorer.Core.Query;

public enum DateMode
{
    AnyDate,
    Today,
    YesterdayAndToday,
    LastSevenDays,
    Explicit
}

/// <summary>
/// Turns a date mode into the exact Scheduled Procedure Step Start Date value to send.
/// </summary>
public sealed record DateSelection(DateMode Mode, DateTime? From = null, DateTime? To = null)
{
    public static DateSelection Any { get; } = new(DateMode.AnyDate);

    /// <summary>
    /// The DA matching value. Empty for any date, which is universal matching: a zero
    /// length value, not "*", and not a wide range, so no date can be excluded by it.
    /// </summary>
    public string ToDicomValue(DateTime today)
    {
        today = today.Date;
        return Mode switch
        {
            DateMode.AnyDate => "",
            DateMode.Today => Day(today),
            DateMode.YesterdayAndToday => Range(today.AddDays(-1), today),
            DateMode.LastSevenDays => Range(today.AddDays(-6), today),
            DateMode.Explicit => ExplicitValue(),
            _ => throw new ArgumentOutOfRangeException(nameof(Mode))
        };
    }

    private string ExplicitValue()
    {
        if (From is null && To is null)
            throw new InvalidOperationException("An explicit date range needs a from date, a to date, or both.");
        if (From is { } f && To is { } t && f.Date > t.Date)
            throw new InvalidOperationException($"The from date {f:yyyy-MM-dd} is after the to date {t:yyyy-MM-dd}.");

        // Open ended ranges are legal DA range matching (PS3.4 C.2.2.2.5).
        if (From is null) return "-" + Day(To!.Value);
        if (To is null) return Day(From.Value) + "-";
        return Range(From.Value, To.Value);
    }

    /// <summary>
    /// A range whose two ends are the same day is sent as a single date, because some
    /// servers handle the degenerate range poorly.
    /// </summary>
    private static string Range(DateTime from, DateTime to) =>
        from.Date == to.Date ? Day(from) : $"{Day(from)}-{Day(to)}";

    private static string Day(DateTime d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public string Describe(DateTime today)
    {
        var value = ToDicomValue(today);
        return Mode switch
        {
            DateMode.AnyDate => "any date (no date restriction, empty key)",
            _ => $"{ModeName(Mode)}, sent as '{value}'"
        };
    }

    public static string ModeName(DateMode mode) => mode switch
    {
        DateMode.AnyDate => "any date",
        DateMode.Today => "today",
        DateMode.YesterdayAndToday => "yesterday and today",
        DateMode.LastSevenDays => "the last seven days",
        DateMode.Explicit => "an explicit range",
        _ => mode.ToString()
    };
}
