using System.Globalization;
using System.Text;

namespace Anchor.Core.Connectors;

/// <summary>
/// Read-only fallback for calendars published as an ICS URL (no OAuth, no admin consent).
/// Supports DAILY/WEEKLY(+BYDAY)/MONTHLY/YEARLY RRULEs with INTERVAL/COUNT/UNTIL and EXDATE;
/// more exotic rules are ignored (the master event is still shown once).
/// </summary>
public sealed class IcsCalendarSource(HttpClient http, IcsFeed feed) : ICalendarSource
{
    public string Name => "ICS/" + feed.Name;

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var text = await http.GetStringAsync(feed.Url, ct);
        return IcsParser.Parse(text, Name, from, to);
    }
}

public static class IcsParser
{
    sealed class Raw
    {
        public Dictionary<string, (string Value, Dictionary<string, string> Params)> Props = new();
        public List<DateTimeOffset> Exdates = new();
    }

    public static List<CalendarEvent> Parse(string ics, string source, DateTimeOffset from, DateTimeOffset to)
    {
        var events = new List<CalendarEvent>();
        foreach (var raw in ReadEvents(ics))
        {
            if (!raw.Props.TryGetValue("DTSTART", out var ds)) continue;
            var (start, allDay) = ParseDate(ds.Value, ds.Params);
            var end = raw.Props.TryGetValue("DTEND", out var de) ? ParseDate(de.Value, de.Params).Item1
                    : allDay ? start.AddDays(1) : start;
            var duration = end - start;
            var title = Unescape(raw.Props.TryGetValue("SUMMARY", out var s) ? s.Value : "(no title)");
            var uid = raw.Props.TryGetValue("UID", out var u) ? u.Value : title + start.ToUnixTimeSeconds();
            var cancelled = raw.Props.TryGetValue("STATUS", out var st) && st.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
            var loc = raw.Props.TryGetValue("LOCATION", out var l) ? Unescape(l.Value) : null;
            var join = Connectors.GoogleCalendarSource.FindUrl(loc) ??
                       Connectors.GoogleCalendarSource.FindUrl(raw.Props.TryGetValue("DESCRIPTION", out var d) ? Unescape(d.Value) : null) ??
                       (raw.Props.TryGetValue("URL", out var url) ? url.Value : null);

            IEnumerable<DateTimeOffset> starts = raw.Props.TryGetValue("RRULE", out var rr)
                ? Expand(start, rr.Value, from - duration, to)
                : new[] { start };

            foreach (var occ in starts)
            {
                if (occ + duration < from || occ > to || raw.Exdates.Any(x => x == occ)) continue;
                events.Add(new CalendarEvent($"{uid}@{occ.ToUnixTimeSeconds()}", source, title, occ, occ + duration, join, loc, allDay, cancelled));
            }
        }
        return events;
    }

    static IEnumerable<Raw> ReadEvents(string ics)
    {
        // Unfold continuation lines.
        var lines = new List<string>();
        foreach (var line in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && lines.Count > 0) lines[^1] += line[1..];
            else lines.Add(line);
        }

        Raw? cur = null;
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { cur = new Raw(); continue; }
            if (line == "END:VEVENT") { if (cur is not null) yield return cur; cur = null; continue; }
            if (cur is null) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var head = line[..colon].Split(';');
            var name = head[0].ToUpperInvariant();
            var prm = head.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                          .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].Trim('"'));
            var value = line[(colon + 1)..];
            if (name == "EXDATE")
                foreach (var v in value.Split(',')) cur.Exdates.Add(ParseDate(v, prm).Item1);
            else
                cur.Props[name] = (value, prm);
        }
    }

    static (DateTimeOffset, bool) ParseDate(string v, Dictionary<string, string> prm)
    {
        if (v.Length == 8 || prm.GetValueOrDefault("VALUE") == "DATE")
            return (new DateTimeOffset(DateTime.ParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture), TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)), true);
        if (v.EndsWith('Z'))
            return (DateTimeOffset.ParseExact(v, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), false);
        var local = DateTime.ParseExact(v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        var tz = TimeZoneInfo.Local;
        if (prm.TryGetValue("TZID", out var id))
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* unknown TZID: fall back to local */ }
        return (new DateTimeOffset(local, tz.GetUtcOffset(local)), false);
    }

    static IEnumerable<DateTimeOffset> Expand(DateTimeOffset start, string rule, DateTimeOffset from, DateTimeOffset to)
    {
        var p = rule.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].ToUpperInvariant(), x => x[1]);
        var freq = p.GetValueOrDefault("FREQ", "");
        var interval = int.TryParse(p.GetValueOrDefault("INTERVAL"), out var i) && i > 0 ? i : 1;
        var count = int.TryParse(p.GetValueOrDefault("COUNT"), out var c) ? c : int.MaxValue;
        var until = p.TryGetValue("UNTIL", out var u) ? ParseDate(u, new()).Item1 : DateTimeOffset.MaxValue;
        var days = p.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(d => d[^2..]).Select(d => d switch { "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday }).ToHashSet()
            : new HashSet<DayOfWeek> { start.DayOfWeek };

        var emitted = 0;
        var cursor = start;
        var guard = 0;
        while (cursor <= to && cursor <= until && emitted < count && guard++ < 5000)
        {
            switch (freq)
            {
                case "DAILY":
                    emitted++; if (cursor >= from) yield return cursor; cursor = cursor.AddDays(interval); break;
                case "WEEKLY":
                    {
                        var weekStart = cursor.AddDays(-(int)cursor.DayOfWeek);
                        for (var d = 0; d < 7; d++)
                        {
                            var day = weekStart.AddDays(d);
                            if (!days.Contains(day.DayOfWeek) || day < start || day > until || day > to) continue;
                            if (emitted >= count) break;
                            emitted++; if (day >= from) yield return day;
                        }
                        cursor = weekStart.AddDays(7 * interval); break;
                    }
                case "MONTHLY":
                    emitted++; if (cursor >= from) yield return cursor; cursor = cursor.AddMonths(interval); break;
                case "YEARLY":
                    emitted++; if (cursor >= from) yield return cursor; cursor = cursor.AddYears(interval); break;
                default:
                    if (cursor >= from) yield return cursor; yield break;
            }
        }
    }

    static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
}
