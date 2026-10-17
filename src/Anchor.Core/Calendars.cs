namespace Anchor.Core;

public enum CalGroup { Personal, Work }

/// <summary>How much Anchor cares about a calendar. Full: alerts, "Next up" and AI planning. ViewOnly: shown in the agenda only. Hidden: not shown.</summary>
public enum CalMode { Full, ViewOnly, Hidden }

/// <summary>A user override for one calendar source (e.g. "Google/Family"): shown or hidden, and Personal or Work.</summary>
public sealed class CalendarPref
{
    public string Source { get; set; } = "";
    public string Group { get; set; } = "Personal";
    /// <summary>Kept for older settings files: false meant hidden.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Full | ViewOnly | Hidden. Empty means "derive from Enabled".</summary>
    public string Mode { get; set; } = "";
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

    public static CalMode ModeOf(AnchorSettings s, string source)
    {
        var p = Pref(s, source);
        if (p is null) return CalMode.Full;
        if (Enum.TryParse<CalMode>(p.Mode, ignoreCase: true, out var m)) return m;
        return p.Enabled ? CalMode.Full : CalMode.Hidden;
    }

    /// <summary>Shown in the agenda (Full or ViewOnly).</summary>
    public static bool IsEnabled(AnchorSettings s, string source) => ModeOf(s, source) != CalMode.Hidden;

    /// <summary>Counts for alerts, "Next up", check-ins and everything AI sees.</summary>
    public static bool IsActive(AnchorSettings s, string source) => ModeOf(s, source) == CalMode.Full;

    /// <summary>The calendar's own colour when it has one (Google does); otherwise a colour for its group.</summary>
    public static string ColorOf(AnchorSettings s, CalendarEvent e) =>
        !string.IsNullOrWhiteSpace(e.Color) ? e.Color! : GroupOf(s, e.Source) == CalGroup.Work ? WorkColor : PersonalColor;
}
