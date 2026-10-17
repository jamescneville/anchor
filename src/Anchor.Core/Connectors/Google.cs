using System.Text.Json;
using Anchor.Core.Auth;

namespace Anchor.Core.Connectors;

public static class GoogleAuth
{
    public const string ProviderName = "google";

    public static OAuthProvider Provider(GoogleSettings s) => new(
        ProviderName,
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        s.ClientId, s.ClientSecret,
        string.Join(' ', new[]
        {
            s.Calendar ? "https://www.googleapis.com/auth/calendar.readonly" : null,
            s.Gmail ? "https://www.googleapis.com/auth/gmail.readonly" : null,
        }.Where(x => x is not null)),
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" });
}

/// <summary>
/// Reads every calendar the account can see (primary, shared and the family/home calendar that
/// Skylight syncs from) so the Skylight source of truth is covered without a Skylight API.
/// </summary>
public sealed class GoogleCalendarSource(OAuthSession session, Action<IReadOnlyList<KnownCalendar>>? onCalendars = null) : ICalendarSource
{
    public string Name => "Google";

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var result = new List<CalendarEvent>();
        var known = new List<KnownCalendar>();
        using var list = await session.GetJsonAsync("https://www.googleapis.com/calendar/v3/users/me/calendarList?minAccessRole=reader", ct);

        foreach (var cal in list.RootElement.GetProperty("items").EnumerateArray())
        {
            if (cal.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.False) continue;
            var id = cal.GetProperty("id").GetString()!;
            if (id.Contains("#holiday@") || id.Contains("#contacts@")) continue;
            var calName = cal.TryGetProperty("summaryOverride", out var so) ? so.GetString()
                        : cal.TryGetProperty("summary", out var s) ? s.GetString() : id;
            var color = cal.TryGetProperty("backgroundColor", out var bc) ? bc.GetString() : null;
            known.Add(new KnownCalendar($"Google/{calName}", calName ?? id, color));

            var url = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(id)}/events" +
                      $"?singleEvents=true&orderBy=startTime&maxResults=250" +
                      $"&timeMin={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&timeMax={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}";
            using var ev = await session.GetJsonAsync(url, ct);
            foreach (var e in ev.RootElement.GetProperty("items").EnumerateArray())
            {
                var parsed = Parse(e, $"Google/{calName}", color);
                if (parsed is not null) result.Add(parsed);
            }
        }
        onCalendars?.Invoke(known);
        return result;
    }

    public static CalendarEvent? Parse(JsonElement e, string source, string? color = null)
    {
        if (!e.TryGetProperty("start", out var st) || !e.TryGetProperty("end", out var en)) return null;
        var allDay = st.TryGetProperty("date", out _);
        var start = allDay ? DateTimeOffset.Parse(st.GetProperty("date").GetString()!) : DateTimeOffset.Parse(st.GetProperty("dateTime").GetString()!);
        var end = allDay ? DateTimeOffset.Parse(en.GetProperty("date").GetString()!) : DateTimeOffset.Parse(en.GetProperty("dateTime").GetString()!);

        string? join = e.TryGetProperty("hangoutLink", out var hl) ? hl.GetString() : null;
        if (join is null && e.TryGetProperty("conferenceData", out var cd) && cd.TryGetProperty("entryPoints", out var eps))
            join = eps.EnumerateArray().Select(x => x.TryGetProperty("uri", out var u) ? u.GetString() : null).FirstOrDefault(u => u?.StartsWith("http") == true);
        var location = e.TryGetProperty("location", out var loc) ? loc.GetString() : null;
        join ??= FindUrl(location) ?? FindUrl(e.TryGetProperty("description", out var d) ? d.GetString() : null);

        return new CalendarEvent(
            e.GetProperty("id").GetString()!, source,
            e.TryGetProperty("summary", out var sum) ? sum.GetString() ?? "(no title)" : "(no title)",
            start, end, join, location, allDay,
            e.TryGetProperty("status", out var status) && status.GetString() == "cancelled", null, color);
    }

    static readonly string[] JoinHosts = ["zoom.us", "teams.microsoft.com", "teams.live.com", "meet.google.com", "webex.com", "gotomeet"];
    public static string? FindUrl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"https?://[^\s""<>]+"))
            if (JoinHosts.Any(h => m.Value.Contains(h, StringComparison.OrdinalIgnoreCase))) return m.Value;
        return null;
    }
}

public sealed class GmailSource(OAuthSession session, Func<ISet<string>> seen) : IMessageSource
{
    public string Name => "Gmail";
    public bool IsWork => false;

    public async Task<IReadOnlyList<InboxItem>> GetRecentAsync(DateTimeOffset since, CancellationToken ct)
    {
        var days = Math.Max(1, (int)Math.Ceiling((DateTimeOffset.Now - since).TotalDays));
        var q = Uri.EscapeDataString($"in:inbox is:unread newer_than:{days}d -category:promotions -category:social -category:forums");
        using var list = await session.GetJsonAsync($"https://gmail.googleapis.com/gmail/v1/users/me/messages?q={q}&maxResults=30", ct);
        var items = new List<InboxItem>();
        if (!list.RootElement.TryGetProperty("messages", out var msgs)) return items;

        var known = seen();
        foreach (var m in msgs.EnumerateArray())
        {
            var id = m.GetProperty("id").GetString()!;
            if (known.Contains("gmail:" + id)) continue;
            using var d = await session.GetJsonAsync(
                $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}?format=metadata&metadataHeaders=From&metadataHeaders=Subject", ct);
            var r = d.RootElement;
            string Header(string n) => r.GetProperty("payload").GetProperty("headers").EnumerateArray()
                .Where(h => string.Equals(h.GetProperty("name").GetString(), n, StringComparison.OrdinalIgnoreCase))
                .Select(h => h.GetProperty("value").GetString()).FirstOrDefault() ?? "";
            var ms = long.Parse(r.GetProperty("internalDate").GetString()!);
            items.Add(new InboxItem("gmail:" + id, "Gmail", false, Header("From"), Header("Subject"),
                System.Net.WebUtility.HtmlDecode(r.TryGetProperty("snippet", out var sn) ? sn.GetString() ?? "" : ""),
                DateTimeOffset.FromUnixTimeMilliseconds(ms), $"https://mail.google.com/mail/u/0/#inbox/{id}"));
        }
        return items;
    }
}
