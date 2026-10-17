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
