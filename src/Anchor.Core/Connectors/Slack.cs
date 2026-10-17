using System.Net.Http.Headers;
using System.Text.Json;

namespace Anchor.Core.Connectors;

/// <summary>
/// Direct messages and @mentions via search.messages (user token, scope search:read).
/// One request pair per refresh, so it stays well inside Slack rate limits.
/// </summary>
public sealed class SlackSource(HttpClient http, string userToken, Func<ISet<string>> seen) : IMessageSource
{
    public string Name => "Slack";
    public bool IsWork => true;

    public async Task<IReadOnlyList<InboxItem>> GetRecentAsync(DateTimeOffset since, CancellationToken ct)
    {
        string meId;
        using (var auth = await CallAsync("auth.test", null, ct))
            meId = auth.RootElement.GetProperty("user_id").GetString()!;

        var after = since.LocalDateTime.Date.AddDays(-1).ToString("yyyy-MM-dd");
        var known = seen();
        var items = new Dictionary<string, InboxItem>();

        foreach (var query in new[] { $"is:dm after:{after}", $"<@{meId}> after:{after}" })
        {
            using var doc = await CallAsync("search.messages", $"query={Uri.EscapeDataString(query)}&sort=timestamp&sort_dir=desc&count=30", ct);
            foreach (var m in doc.RootElement.GetProperty("messages").GetProperty("matches").EnumerateArray())
            {
                if (m.TryGetProperty("user", out var u) && u.GetString() == meId) continue;
                var ts = m.GetProperty("ts").GetString()!;
                var channel = m.GetProperty("channel").GetProperty("id").GetString();
                var id = $"slack:{channel}:{ts}";
                if (known.Contains(id) || items.ContainsKey(id)) continue;
                var when = DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(ts, System.Globalization.CultureInfo.InvariantCulture));
                if (when < since) continue;
                var chName = m.GetProperty("channel").TryGetProperty("name", out var n) ? n.GetString() : "DM";
                items[id] = new InboxItem(id, "Slack", true, m.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
                    chName ?? "", m.GetProperty("text").GetString() ?? "", when,
                    m.TryGetProperty("permalink", out var pl) ? pl.GetString() : null);
            }
        }
        return items.Values.OrderByDescending(i => i.Received).ToList();
    }

    async Task<JsonDocument> CallAsync(string method, string? query, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://slack.com/api/{method}" + (query is null ? "" : "?" + query));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        using var resp = await http.SendAsync(req, ct);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.GetProperty("ok").GetBoolean())
        {
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            doc.Dispose();
            throw new HttpRequestException($"Slack {method}: {err}");
        }
        return doc;
    }
}
