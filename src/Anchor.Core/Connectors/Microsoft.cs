using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anchor.Core.Auth;

namespace Anchor.Core.Connectors;

public static class MicrosoftAuth
{
    public const string ProviderName = "microsoft";

    public static OAuthProvider Provider(MicrosoftSettings s) => new(
        ProviderName,
        $"https://login.microsoftonline.com/{s.Tenant}/oauth2/v2.0/authorize",
        $"https://login.microsoftonline.com/{s.Tenant}/oauth2/v2.0/token",
        s.ClientId, null,
        string.Join(' ', new[]
        {
            "offline_access", "User.Read",
            s.Calendar ? "Calendars.Read" : null,
            s.Mail ? "Mail.Read" : null,
            s.Teams ? "Chat.Read" : null,
        }.Where(x => x is not null)));
}

public sealed class OutlookCalendarSource(OAuthSession session) : ICalendarSource
{
    public string Name => "Outlook";

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var result = new List<CalendarEvent>();
        string? url = "https://graph.microsoft.com/v1.0/me/calendarView" +
            $"?startDateTime={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&endDateTime={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}" +
            "&$top=100&$orderby=start/dateTime&$select=id,subject,start,end,isAllDay,isCancelled,location,onlineMeeting,onlineMeetingUrl,bodyPreview";

        while (url is not null)
        {
            using var doc = await session.GetJsonAsync(url, ct, r => r.Headers.Add("Prefer", "outlook.timezone=\"UTC\""));
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                // Times were requested in UTC, but are returned without an offset.
                DateTimeOffset Utc(JsonElement t) => new(DateTime.SpecifyKind(DateTime.Parse(t.GetProperty("dateTime").GetString()!), DateTimeKind.Utc));
                string? join = null;
                if (e.TryGetProperty("onlineMeeting", out var om) && om.ValueKind == JsonValueKind.Object && om.TryGetProperty("joinUrl", out var ju)) join = ju.GetString();
                if (join is null && e.TryGetProperty("onlineMeetingUrl", out var omu) && omu.ValueKind == JsonValueKind.String) join = omu.GetString();
                var loc = e.TryGetProperty("location", out var l) && l.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                join ??= GoogleCalendarSource.FindUrl(loc) ?? GoogleCalendarSource.FindUrl(e.TryGetProperty("bodyPreview", out var bp) ? bp.GetString() : null);

                result.Add(new CalendarEvent(
                    e.GetProperty("id").GetString()!, "Outlook",
                    e.TryGetProperty("subject", out var s) ? s.GetString() ?? "(no title)" : "(no title)",
                    Utc(e.GetProperty("start")), Utc(e.GetProperty("end")), join, loc,
                    e.TryGetProperty("isAllDay", out var ad) && ad.GetBoolean(),
                    e.TryGetProperty("isCancelled", out var ic) && ic.GetBoolean()));
            }
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
        return result;
    }
}

public sealed class OutlookMailSource(OAuthSession session, Func<ISet<string>> seen) : IMessageSource
{
    public string Name => "Outlook mail";
    public bool IsWork => true;

    public async Task<IReadOnlyList<InboxItem>> GetRecentAsync(DateTimeOffset since, CancellationToken ct)
    {
        var url = "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages" +
                  $"?$filter=receivedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}&$orderby=receivedDateTime desc&$top=30" +
                  "&$select=id,subject,from,receivedDateTime,bodyPreview,webLink,isRead";
        using var doc = await session.GetJsonAsync(url, ct);
        var known = seen();
        var items = new List<InboxItem>();
        foreach (var m in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (m.TryGetProperty("isRead", out var rd) && rd.GetBoolean()) continue;
            var id = "outlook:" + m.GetProperty("id").GetString();
            if (known.Contains(id)) continue;
            var from = m.TryGetProperty("from", out var f) && f.TryGetProperty("emailAddress", out var ea)
                ? $"{ea.GetProperty("name").GetString()} <{ea.GetProperty("address").GetString()}>" : "";
            items.Add(new InboxItem(id, "Outlook", true, from,
                m.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "",
                m.TryGetProperty("bodyPreview", out var bp) ? bp.GetString() ?? "" : "",
                m.GetProperty("receivedDateTime").GetDateTimeOffset(),
                m.TryGetProperty("webLink", out var wl) ? wl.GetString() : null));
        }
        return items;
    }
}

/// <summary>
/// Latest message of each recent Teams chat that someone else sent. Needs Chat.Read, which many
/// tenants gate behind admin consent: if it is blocked this source just reports an error.
/// </summary>
public sealed class TeamsChatSource(OAuthSession session, Func<ISet<string>> seen) : IMessageSource
{
    public string Name => "Teams";
    public bool IsWork => true;

    public async Task<IReadOnlyList<InboxItem>> GetRecentAsync(DateTimeOffset since, CancellationToken ct)
    {
        string meId;
        using (var me = await session.GetJsonAsync("https://graph.microsoft.com/v1.0/me?$select=id", ct))
            meId = me.RootElement.GetProperty("id").GetString()!;

        using var doc = await session.GetJsonAsync(
            "https://graph.microsoft.com/v1.0/me/chats?$expand=lastMessagePreview&$orderby=lastMessagePreview/createdDateTime desc&$top=25", ct);
        var known = seen();
        var items = new List<InboxItem>();
        foreach (var chat in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (!chat.TryGetProperty("lastMessagePreview", out var lm) || lm.ValueKind != JsonValueKind.Object) continue;
            var created = lm.GetProperty("createdDateTime").GetDateTimeOffset();
            if (created < since) continue;
            var user = lm.TryGetProperty("from", out var fr) && fr.ValueKind == JsonValueKind.Object && fr.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
            if (user is null || user.Value.GetProperty("id").GetString() == meId) continue; // skip system events and my own messages
            var id = "teams:" + lm.GetProperty("id").GetString();
            if (known.Contains(id)) continue;

            var html = lm.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")).Trim();
            if (text.Length == 0) continue;
            var topic = chat.TryGetProperty("topic", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            items.Add(new InboxItem(id, "Teams", true, user.Value.GetProperty("displayName").GetString() ?? "", topic ?? "Chat", text, created,
                chat.TryGetProperty("webUrl", out var w) ? w.GetString() : null));
        }
        return items;
    }
}
