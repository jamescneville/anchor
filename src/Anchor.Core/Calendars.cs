namespace Anchor.Core;

public enum CalGroup { Personal, Work }

/// <summary>A user override for one calendar source (e.g. "Google/Family"): shown or hidden, and Personal or Work.</summary>
public sealed class CalendarPref
{
    public string Source { get; set; } = "";
    public string Group { get; set; } = "Personal";
    public bool Enabled { get; set; } = true;
}

/// <summary>A calendar discovered on an account (with the colour its owner gave it), so Settings can list it before it has events.</summary>
public sealed record KnownCalendar(string Source, string Name, string? Color);

public static class CalendarPrefs
{
    public const string PersonalColor = "#4ADE9A";
    public const string WorkColor = "#5B9DFF";

    /// <summary>Outlook and Teams are work; Google (which is where Skylight lives) is personal; ICS follows its feed's IsWork flag.</summary>
    public static CalGroup DefaultGroup(string source, IEnumerable<IcsFeed> feeds)
    {
        if (source.Contains("Outlook", StringComparison.OrdinalIgnoreCase) || source.Contains("Teams", StringComparison.OrdinalIgnoreCase)) return CalGroup.Work;
        if (source.StartsWith("ICS/", StringComparison.OrdinalIgnoreCase))
        {
            var f = feeds.FirstOrDefault(x => "ICS/" + x.Name == source);
            return f is null || f.IsWork ? CalGroup.Work : CalGroup.Personal;
        }
        return CalGroup.Personal;
    }

    static CalendarPref? Pref(AnchorSettings s, string source) => s.Calendars.FirstOrDefault(p => p.Source == source);

    public static CalGroup GroupOf(AnchorSettings s, string source) =>
        Pref(s, source) is { } p ? (p.Group.Equals("Work", StringComparison.OrdinalIgnoreCase) ? CalGroup.Work : CalGroup.Personal) : DefaultGroup(source, s.IcsFeeds);

    public static bool IsEnabled(AnchorSettings s, string source) => Pref(s, source)?.Enabled ?? true;

    /// <summary>The calendar's own colour when it has one (Google does); otherwise a colour for its group.</summary>
    public static string ColorOf(AnchorSettings s, CalendarEvent e) =>
        !string.IsNullOrWhiteSpace(e.Color) ? e.Color! : GroupOf(s, e.Source) == CalGroup.Work ? WorkColor : PersonalColor;
}
