namespace Anchor.Core;

public sealed record DueAlert(CalendarEvent Event, int OffsetMinutes, IReadOnlyList<string> KeysToMark);

/// <summary>
/// Pure, clock-injected alert logic. Alerts are computed against absolute event times, so a
/// laptop waking from sleep fires only the most relevant missed alert instead of a burst.
/// </summary>
public static class AlertScheduler
{
    public static string Key(CalendarEvent e, int offsetMinutes) => $"{e.Source}|{e.Id}|{e.Start.ToUnixTimeSeconds()}|{offsetMinutes}";

    public static IReadOnlyList<DueAlert> Evaluate(
        IEnumerable<CalendarEvent> events,
        DateTimeOffset now,
        IReadOnlySet<string> fired,
        IReadOnlyList<int> offsetsMinutes)
    {
        var offsets = offsetsMinutes.Distinct().OrderByDescending(o => o).ToArray(); // 15, 5, 1, 0
        var due = new List<DueAlert>();

        foreach (var e in events)
        {
            if (e.Cancelled || e.IsAllDay || now >= e.End) continue;

            // Offsets whose fire time has passed and that have not fired yet.
            var pending = offsets
                .Where(o => e.Start - TimeSpan.FromMinutes(o) <= now && !fired.Contains(Key(e, o)))
                .ToArray();
            if (pending.Length == 0) continue;

            // Fire only the closest-to-start one; mark every earlier one as handled.
            var show = pending.Min();
            due.Add(new DueAlert(e, show, pending.Select(o => Key(e, o)).ToArray()));
        }

        return due.OrderBy(d => d.Event.Start).ToList();
    }

    public static CalendarEvent? NextEvent(IEnumerable<CalendarEvent> events, DateTimeOffset now) =>
        events.Where(e => !e.Cancelled && !e.IsAllDay && e.End > now)
              .OrderBy(e => e.Start)
              .FirstOrDefault();

    public static string Describe(CalendarEvent e, DateTimeOffset now)
    {
        var delta = e.Start - now;
        if (delta.TotalMinutes >= 120) return $"starts in {(int)delta.TotalHours}h {delta.Minutes}m";
        if (delta.TotalSeconds > 60) return $"starts in {Math.Ceiling(delta.TotalMinutes):0} min";
        if (delta.TotalSeconds > -30) return "is starting NOW";
        return $"started {Math.Max(1, Math.Floor(-delta.TotalMinutes)):0} min ago";
    }
}
