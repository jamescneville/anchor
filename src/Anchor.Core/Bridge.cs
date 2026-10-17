using System.Text.Json;

namespace Anchor.Core;

/// <summary>
/// File drop between Claude desktop and Anchor. A scheduled Claude task (or an "Ask Claude" run started from Anchor)
/// writes into the bridge folder; Anchor only ever reads. Everything in these files is treated as untrusted text:
/// it is displayed, never executed, and links must be http(s).
/// </summary>
public static class Bridge
{
    public const string PlanFile = "plan.md";
    public const string TasksFile = "tasks.json";
    public const string EventsFile = "events.json";
    /// <summary>Source label for calendar events dropped by Claude. Contains the product name so work redaction applies.</summary>
    public const string EventSource = "Claude/Outlook";
    const int MaxBytes = 200_000;

    public static string Dir
    {
        get
        {
            var d = Path.Combine(AppPaths.Dir, "bridge");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string? ReadPlan(string dir, out DateTimeOffset writtenAt)
    {
        writtenAt = default;
        var f = Path.Combine(dir, PlanFile);
        if (!File.Exists(f) || new FileInfo(f).Length > MaxBytes) return null;
        writtenAt = new DateTimeOffset(File.GetLastWriteTime(f));
        var text = ReadShared(f).Trim();
        return text.Length == 0 ? null : text;
    }

    public static IReadOnlyList<SuggestedTask> ReadTasks(string dir, DateTimeOffset now)
    {
        var f = Path.Combine(dir, TasksFile);
        if (!File.Exists(f) || new FileInfo(f).Length > MaxBytes) return [];
        return ParseTasks(ReadShared(f), now);
    }

    public static IReadOnlyList<CalendarEvent> ReadEvents(string dir, DateTimeOffset now)
    {
        var f = Path.Combine(dir, EventsFile);
        if (!File.Exists(f) || new FileInfo(f).Length > MaxBytes) return [];
        return ParseEvents(ReadShared(f), now);
    }

    static readonly string[] VirtualWords = ["teams meeting", "microsoft teams", "zoom", "google meet", "webex", "online", "virtual"];

    /// <summary>
    /// events.json: [{"id","title","start","end","allDay","location","joinUrl","cancelled"}] with ISO 8601 times
    /// (an offset is best; without one the time is read as this PC's local time). Only events from yesterday
    /// to a week ahead are kept.
    /// </summary>
    public static IReadOnlyList<CalendarEvent> ParseEvents(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                    : doc.RootElement.TryGetProperty("events", out var t) && t.ValueKind == JsonValueKind.Array ? t
                    : default;
            if (arr.ValueKind != JsonValueKind.Array) return [];

            var list = new List<CalendarEvent>();
            foreach (var e in arr.EnumerateArray().Take(300))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var title = Str(e, "title", 160);
                if (title.Length == 0 || !TryTime(e, "start", out var start)) continue;
                var end = TryTime(e, "end", out var en) && en > start ? en : start.AddMinutes(30);
                if (end < now.AddDays(-1) || start > now.AddDays(7)) continue;

                var loc = Str(e, "location", 120);
                if (VirtualWords.Any(w => loc.Contains(w, StringComparison.OrdinalIgnoreCase))) loc = "";   // a video call is not a place to travel to
                var id = Str(e, "id", 200);
                if (id.Length == 0) id = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(title + start.ToUnixTimeSeconds())))[..12];
                list.Add(new CalendarEvent("bridge:" + id, EventSource, title, start, end, SafeLink(Str(e, "joinUrl", 500)),
                    loc.Length > 0 ? loc : null,
                    e.TryGetProperty("allDay", out var ad) && ad.ValueKind == JsonValueKind.True,
                    e.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True));
            }
            return list;
        }
        catch (JsonException) { return []; }
    }

    static bool TryTime(JsonElement e, string name, out DateTimeOffset t)
    {
        t = default;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        // No offset in the text means wall-clock time on this PC.
        return DateTimeOffset.TryParse(p.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out t);
    }

    // Claude may still be writing the file when we notice it.
    static string ReadShared(string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < 5) { Thread.Sleep(150); }
        }
    }

    public static IReadOnlyList<SuggestedTask> ParseTasks(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                    : doc.RootElement.TryGetProperty("tasks", out var t) && t.ValueKind == JsonValueKind.Array ? t
                    : default;
            if (arr.ValueKind != JsonValueKind.Array) return [];

            var list = new List<SuggestedTask>();
            foreach (var e in arr.EnumerateArray().Take(50))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var title = Str(e, "title", 140);
                if (title.Length == 0) continue;
                var source = Str(e, "source", 30);
                var id = Str(e, "id", 80);
                if (id.Length == 0) id = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(source + "|" + title)))[..12];

                DateTimeOffset? due = e.TryGetProperty("due", out var d) && d.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(d.GetString(), out var dv) ? dv : null;
                var conf = e.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cv) ? Math.Clamp(cv, 0, 1) : 0.7;
                list.Add(new SuggestedTask
                {
                    Id = "claude:" + id,
                    Title = title,
                    Source = source.Length > 0 ? source : "Claude",
                    From = Str(e, "from", 80),
                    Link = SafeLink(Str(e, "link", 500)),
                    Due = due,
                    Confidence = conf,
                    Created = now,
                });
            }
            return list;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Only plain web links may be opened from bridge data (never file:, custom protocols, etc.).</summary>
    public static string? SafeLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : null;

    static string Str(JsonElement e, string name, int max)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return "";
        var s = (p.GetString() ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }
}
