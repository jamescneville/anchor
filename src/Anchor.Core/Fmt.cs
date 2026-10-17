using System.Globalization;

namespace Anchor.Core;

/// <summary>User-facing time formatting; honours the 12/24-hour setting.</summary>
public static class Fmt
{
    public static bool Use24Hour { get; set; }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Time(DateTime t) => t.ToString(Use24Hour ? "HH:mm" : "h:mm tt", Inv);
    public static string Time(DateTime? t) => t is { } v ? Time(v) : "";
    public static string Time(DateTimeOffset t) => Time(t.LocalDateTime);
    public static string DayTime(DateTime t) => t.ToString("ddd ", Inv) + Time(t);
}
