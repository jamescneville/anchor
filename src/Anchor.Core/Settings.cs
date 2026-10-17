using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anchor.Core;

public static class AppPaths
{
    public static string Dir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anchor");
            Directory.CreateDirectory(d);
            return d;
        }
    }
    public static string SettingsFile => Path.Combine(Dir, "settings.json");
    public static string StateFile => Path.Combine(Dir, "state.json");
    public static string TokensFile => Path.Combine(Dir, "tokens.bin");
}

public sealed class GoogleSettings
{
    public bool Enabled { get; set; }
    /// <summary>OAuth client of type "Desktop app" from Google Cloud Console.</summary>
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool Calendar { get; set; } = true;
    public bool Gmail { get; set; } = true;
}

public sealed class MicrosoftSettings
{
    public bool Enabled { get; set; }
    /// <summary>Public-client app registration (Entra ID). Redirect URI: http://localhost</summary>
    public string ClientId { get; set; } = "";
    /// <summary>"organizations" for work accounts, "common" for both, or your tenant id.</summary>
    public string Tenant { get; set; } = "organizations";
    public bool Calendar { get; set; } = true;
    public bool Mail { get; set; } = true;
    public bool Teams { get; set; } = true;
}

public sealed class SlackSettings
{
    public bool Enabled { get; set; }
    /// <summary>User token (xoxp-...) with search:read. Stored in settings; prefer env ANCHOR_SLACK_TOKEN.</summary>
    public string UserToken { get; set; } = "";
}

public sealed class IcsFeed
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Work calendars are redacted in AI prompts unless Ai.SendWorkContentToClaude is on. Set false for a personal feed.</summary>
    public bool IsWork { get; set; } = true;
}

public sealed class ClaudeDesktopSettings
{
    /// <summary>Use the Claude account already signed in on this PC (through the local Claude Code CLI), silently, with no API key.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Work meetings (Outlook, work ICS) and work follow-ups/messages (Outlook, Teams, Slack) are included in what Claude sees.
    /// Off by default: work items are redacted first. Turn on only if your organisation allows it for its Claude account.
    /// </summary>
    public bool IncludeWorkContext { get; set; }
    /// <summary>Optional full path to claude.exe; auto-detected when empty.</summary>
    public string CliPath { get; set; } = "";
    public string FastModel { get; set; } = "haiku";
    public string PlanModel { get; set; } = "sonnet";
    /// <summary>Anchor's own cap on what its background Claude calls may cost (USD, as the CLI reports them). Background work stops past the daily soft cap.</summary>
    public double DailySoftCapUsd { get; set; } = 1.00;
    /// <summary>Anchor stops using Claude entirely past this monthly amount.</summary>
    public double MonthlyCapUsd { get; set; } = 15;
}

public sealed class AiSettings
{
    public bool Enabled { get; set; } = true;
    public string FastModel { get; set; } = "claude-haiku-5-5";
    public string PlanModel { get; set; } = "claude-sonnet-5-5";
    public double MonthlyBudgetUsd { get; set; } = 20;
    public double DailySoftCapUsd { get; set; } = 0.60;
    public double WarnUsd { get; set; } = 15;
    public double CutoffUsd { get; set; } = 19;
    /// <summary>
    /// When false (default) work items (Outlook, Teams, Slack) are never sent to Claude;
    /// they are still shown in the inbox, with local heuristics only.
    /// </summary>
    public bool SendWorkContentToClaude { get; set; }
    /// <summary>USD per million tokens. Verify against current published pricing.</summary>
    public Dictionary<string, double[]> PricePerMTok { get; set; } = new()
    {
        ["claude-haiku-5-5"] = [0.10, 0.50],
        ["claude-sonnet-5-5"] = [2.00, 10.00],
    };
}

public sealed class AnchorSettings
{
    public int[] AlertOffsetsMinutes { get; set; } = [15, 5, 1, 0];
    /// <summary>Standard | Strong | Maximum</summary>
    public string AlertIntensity { get; set; } = "Strong";
    public int SnoozeMinutes { get; set; } = 2;
    public int CheckInMinutes { get; set; } = 30;
    public bool SpeakAlerts { get; set; } = true;
    /// <summary>false = 2:30 PM, true = 14:30.</summary>
    public bool Use24HourClock { get; set; }
    /// <summary>Extra early alert (minutes) for in-person meetings with a physical location, to leave time to travel.</summary>
    public int InPersonLeadMinutes { get; set; } = 30;
    /// <summary>Morning plan at workday start and a wrap-up sweep 30 min before it ends (weekdays).</summary>
    public bool BriefsEnabled { get; set; } = true;
    /// <summary>Global "Where was I?" shortcut, e.g. "Ctrl+Space", "Ctrl+Shift+W", "Win+Shift+A".</summary>
    public string Hotkey { get; set; } = "Ctrl+Space";
    /// <summary>Local time, HH:mm. Focus check-ins only run inside this window on weekdays; meeting alerts always run.</summary>
    public string WorkdayStart { get; set; } = "08:30";
    public string WorkdayEnd { get; set; } = "17:30";
    public int CalendarRefreshMinutes { get; set; } = 3;
    public int MessageRefreshMinutes { get; set; } = 10;
    public GoogleSettings Google { get; set; } = new();
    public MicrosoftSettings Microsoft { get; set; } = new();
    public SlackSettings Slack { get; set; } = new();
    public List<IcsFeed> IcsFeeds { get; set; } = new();
    /// <summary>Per-calendar overrides: shown/hidden and Personal vs Work. Calendars without an entry use the defaults.</summary>
    public List<CalendarPref> Calendars { get; set; } = new();
    /// <summary>All | Personal | Work | Split</summary>
    public string AgendaView { get; set; } = "All";
    public AiSettings Ai { get; set; } = new();
    public ClaudeDesktopSettings ClaudeDesktop { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static AnchorSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        if (!File.Exists(path))
        {
            var fresh = new AnchorSettings();
            fresh.Save(path);
            return fresh;
        }
        return JsonSerializer.Deserialize<AnchorSettings>(File.ReadAllText(path), Json) ?? new AnchorSettings();
    }

    public void Save(string? path = null) =>
        File.WriteAllText(path ?? AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
}
