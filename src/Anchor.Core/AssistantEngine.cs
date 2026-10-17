using Anchor.Core.AI;
using Anchor.Core.Auth;
using Anchor.Core.Connectors;
using Anthropic;

namespace Anchor.Core;

/// <summary>
/// Orchestrates connectors, local alert logic and AI. Meeting alerts depend only on the cached
/// calendar and the clock, never on the network or on Claude.
/// </summary>
public sealed class AssistantEngine : IDisposable
{
    readonly AnchorSettings _settings;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly List<ICalendarSource> _calendars = new();
    readonly List<IMessageSource> _messages = new();
    readonly Dictionary<string, OAuthSession> _sessions = new();
    readonly HashSet<string> _activeAlerts = new();
    readonly CancellationTokenSource _cts = new();
    readonly object _refreshGate = new();
    DateTimeOffset _lastMessageRefresh = DateTimeOffset.MinValue;
    DateTimeOffset _lastCheckIn = DateTimeOffset.Now;
    bool _refreshing;
    FileSystemWatcher? _bridgeWatcher;
    Timer? _bridgeDebounce;
    readonly List<CalendarEvent> _testEvents = new();

    public StateStore Store { get; }
    public TokenStore Tokens { get; } = new();
    public BudgetTracker Budget { get; }
    public AiBackend? Claude { get; private set; }
    int _aiBusy;
    public bool AiBusy => _aiBusy > 0;
    public bool UsingApi => Claude is ClaudeService;
    public Dictionary<string, string> SourceErrors { get; } = new();

    public event Action<DueAlert>? AlertDue;
    public event Action? Changed;
    public event Action? CheckInDue;
    /// <summary>"morning" or "evening".</summary>
    public event Action<string>? BriefDue;

    public AssistantEngine(AnchorSettings settings, StateStore store)
    {
        _settings = settings;
        Store = store;
        Budget = new BudgetTracker(store, settings.Ai);
        BuildAi();
        BuildSources();
    }

    public bool CliMissing { get; private set; }
    public const string AnthropicKeyName = "anthropic";
    public AnchorSettings Settings => _settings;

    /// <summary>The API path and the Claude-login path have separate work-content switches.</summary>
    bool WorkAllowedForAi => Claude is ClaudeService ? _settings.Ai.SendWorkContentToClaude : _settings.ClaudeDesktop.IncludeWorkContext;

    async Task<T> Busy<T>(Func<Task<T>> work)
    {
        Interlocked.Increment(ref _aiBusy); Changed?.Invoke();
        try { return await work(); }
        finally { Interlocked.Decrement(ref _aiBusy); Changed?.Invoke(); }
    }

    IReadOnlySet<string> WorkIcsSources => _settings.IcsFeeds.Where(f => f.IsWork).Select(f => "ICS/" + f.Name).ToHashSet();
    IReadOnlyList<CalendarEvent> ForAi(IEnumerable<CalendarEvent> e) => AiPrivacy.Events(e, WorkAllowedForAi, WorkIcsSources);
    IReadOnlyList<SuggestedTask> ForAi(IEnumerable<SuggestedTask> t) => AiPrivacy.Tasks(t, WorkAllowedForAi, WorkIcsSources);

    public void SaveAnthropicKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) Tokens.Remove(AnthropicKeyName);
        else Tokens.Set(AnthropicKeyName, new TokenSet { AccessToken = key.Trim(), ExpiresAt = DateTimeOffset.MaxValue });
    }

    public string? CurrentAnthropicKey() =>
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } e ? e : Tokens.Get(AnthropicKeyName)?.AccessToken;

    public bool HasAnthropicKey => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) || Tokens.Get(AnthropicKeyName) is not null;

    void BuildAi()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) key = Tokens.Get(AnthropicKeyName)?.AccessToken; // saved via the settings window (DPAPI)
        if (!string.IsNullOrWhiteSpace(key) && _settings.Ai.Enabled)
        {
            Claude = new ClaudeService(new AnthropicClient { ApiKey = key }, _settings.Ai, Budget);   // an API key takes priority
            return;
        }
        var cd = _settings.ClaudeDesktop;
        if (cd.Enabled && ClaudeCliBackend.Locate(cd.CliPath) is { } exe) Claude = new ClaudeCliBackend(exe, cd);
        else CliMissing = cd.Enabled;
    }

    void BuildSources()
    {
        ISet<string> Seen() => Store.State.SeenItemIds;

        var g = _settings.Google;
        if (g.Enabled && g.ClientId.Length > 0)
        {
            var s = Session(GoogleAuth.Provider(g));
            if (g.Calendar) _calendars.Add(new GoogleCalendarSource(s));
            if (g.Gmail) _messages.Add(new GmailSource(s, Seen));
        }

        var m = _settings.Microsoft;
        if (m.Enabled && m.ClientId.Length > 0)
        {
            var s = Session(MicrosoftAuth.Provider(m));
            if (m.Calendar) _calendars.Add(new OutlookCalendarSource(s));
            if (m.Mail) _messages.Add(new OutlookMailSource(s, Seen));
            if (m.Teams) _messages.Add(new TeamsChatSource(s, Seen));
        }

        var slackToken = Environment.GetEnvironmentVariable("ANCHOR_SLACK_TOKEN") is { Length: > 0 } t ? t : _settings.Slack.UserToken;
        if (_settings.Slack.Enabled && slackToken.Length > 0)
            _messages.Add(new SlackSource(_http, slackToken, Seen));

        foreach (var f in _settings.IcsFeeds.Where(f => f.Url.Length > 0))
            _calendars.Add(new IcsCalendarSource(_http, f));
    }

    OAuthSession Session(OAuthProvider p)
    {
        var s = new OAuthSession(_http, Tokens, p);
        _sessions[p.Name] = s;
        return s;
    }

    /// <summary>Dummy meeting for testing the alert pipeline end to end (tray menu: Test alert).</summary>
    public void AddTestMeeting(int startsInMinutes)
    {
        var start = DateTimeOffset.Now.AddMinutes(startsInMinutes);
        _testEvents.Add(new CalendarEvent("test-" + Guid.NewGuid().ToString("N")[..6], "Test", "Test meeting (Anchor)", start, start.AddMinutes(15),
            "https://example.com/join"));
        Store.Mutate(s => s.CachedEvents = Dedupe(s.CachedEvents.Concat(_testEvents)).ToList());
        Tick(DateTimeOffset.Now);
        Changed?.Invoke();
    }

    public bool IsConnected(string provider) => _sessions.TryGetValue(provider, out var s) && s.IsConnected;
    public IReadOnlyCollection<string> Providers => _sessions.Keys;

    public Task ConnectAsync(string provider, CancellationToken ct) =>
        _sessions.TryGetValue(provider, out var s)
            ? s.ConnectAsync(OAuthSession.OpenInBrowser, ct)
            : throw new InvalidOperationException($"{provider} is not enabled in settings.json (set Enabled and ClientId, then restart).");

    // ---------------------------------------------------------------- loop

    public void Start()
    {
        StartBridge();
        _ = Task.Run(LoopAsync);
    }

    // ---------------------------------------------------------------- Claude desktop bridge

    void StartBridge()
    {
        try
        {
            IngestBridge();
            _bridgeWatcher = new FileSystemWatcher(Bridge.Dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            void Kick(object? _, FileSystemEventArgs __) => _bridgeDebounce?.Change(700, Timeout.Infinite);
            _bridgeDebounce = new Timer(_ => IngestBridge(), null, Timeout.Infinite, Timeout.Infinite);
            _bridgeWatcher.Created += Kick; _bridgeWatcher.Changed += Kick; _bridgeWatcher.Renamed += (s, e) => Kick(s, e);
        }
        catch (Exception ex) { SourceErrors["Claude bridge"] = ex.Message; }
    }

    /// <summary>Reads plan.md and tasks.json written by Claude desktop. Existing tasks keep their status.</summary>
    public void IngestBridge()
    {
        try
        {
            var plan = Bridge.ReadPlan(Bridge.Dir, out var at);
            var tasks = Bridge.ReadTasks(Bridge.Dir, DateTimeOffset.Now);
            var events = Bridge.ReadEvents(Bridge.Dir, DateTimeOffset.Now);
            var eventsAt = File.Exists(Path.Combine(Bridge.Dir, Bridge.EventsFile)) ? new DateTimeOffset(File.GetLastWriteTime(Path.Combine(Bridge.Dir, Bridge.EventsFile))) : (DateTimeOffset?)null;
            var changed = false;
            Store.Mutate(s =>
            {
                if (plan is not null && (plan != s.BridgePlan || at != s.BridgePlanAt)) { s.BridgePlan = plan; s.BridgePlanAt = at; changed = true; }
                foreach (var t in tasks.Where(t => s.Tasks.All(x => x.Id != t.Id))) { s.Tasks.Add(t); changed = true; }
                if (eventsAt is not null && eventsAt != s.BridgeEventsAt)
                {
                    s.BridgeEvents = events.ToList(); s.BridgeEventsAt = eventsAt;
                    // Swap them into the live calendar right away so alerts and the agenda update without waiting for a refresh.
                    s.CachedEvents = Dedupe(s.CachedEvents.Where(e => e.Source != Bridge.EventSource).Concat(events)).ToList();
                    changed = true;
                }
            });
            SourceErrors.Remove("Claude bridge");
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex) { SourceErrors["Claude bridge"] = ex.Message; }
    }

    async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        _ = RefreshAllAsync(forceMessages: true);
        try
        {
            do
            {
                Tick(DateTimeOffset.Now);
                if (DateTimeOffset.Now - (Store.State.EventsRefreshed ?? DateTimeOffset.MinValue) > TimeSpan.FromMinutes(_settings.CalendarRefreshMinutes))
                    _ = RefreshAllAsync(forceMessages: false);
            } while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Called by the UI after resume from sleep or unlock: reconcile immediately.</summary>
    public void OnResume()
    {
        Tick(DateTimeOffset.Now);
        _ = RefreshAllAsync(forceMessages: true);
    }

    public void Tick(DateTimeOffset now)
    {
        var fired = Store.State.FiredAlerts;
        var due = EvaluateAlerts(now, fired);
        foreach (var d in due)
        {
            var k = EventKey(d.Event);
            if (_activeAlerts.Contains(k)) continue;
            if (Store.State.SnoozedUntil.TryGetValue(k, out var until) && until > now) continue;
            _activeAlerts.Add(k);
            AlertDue?.Invoke(d);
        }

        if (BriefIsDue(now) is { } kind)
        {
            Store.Mutate(s => { if (kind == "morning") s.LastMorningBrief = now.ToString("yyyy-MM-dd"); else s.LastEveningBrief = now.ToString("yyyy-MM-dd"); });
            BriefDue?.Invoke(kind);
        }
        else if (CheckInIsDue(now)) { _lastCheckIn = now; CheckInDue?.Invoke(); }
    }

    IReadOnlyList<DueAlert> EvaluateAlerts(DateTimeOffset now, IReadOnlySet<string> fired)
    {
        var events = Store.State.CachedEvents;
        var offsets = _settings.AlertOffsetsMinutes;
        if (_settings.InPersonLeadMinutes <= 0) return AlertScheduler.Evaluate(events, now, fired, offsets);

        // In-person meetings get one extra, earlier alert so there is time to actually get there.
        var inPerson = events.Where(IsInPerson).ToList();
        var rest = events.Except(inPerson);
        return AlertScheduler.Evaluate(rest, now, fired, offsets)
            .Concat(AlertScheduler.Evaluate(inPerson, now, fired, offsets.Append(_settings.InPersonLeadMinutes).ToArray()))
            .OrderBy(a => a.Event.Start).ToList();
    }

    public static bool IsInPerson(CalendarEvent e) =>
        e.JoinUrl is null && !string.IsNullOrWhiteSpace(e.Location) && !e.Location.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
        !new[] { "teams", "zoom", "meet", "webex", "online", "virtual", "call" }.Any(w => e.Location.Contains(w, StringComparison.OrdinalIgnoreCase));

    string? BriefIsDue(DateTimeOffset now)
    {
        if (!_settings.BriefsEnabled || _activeAlerts.Count > 0 || now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;
        if (!TimeOnly.TryParse(_settings.WorkdayStart, out var s) || !TimeOnly.TryParse(_settings.WorkdayEnd, out var e)) return null;
        var t = TimeOnly.FromDateTime(now.LocalDateTime);
        var today = now.ToString("yyyy-MM-dd");
        var st = Store.State;
        if (t >= e.AddMinutes(-30)) return t <= e.AddMinutes(60) && st.LastEveningBrief != today ? "evening" : null;
        if (t >= s && st.LastMorningBrief != today) return "morning";
        return null;
    }

    bool CheckInIsDue(DateTimeOffset now)
    {
        if (_settings.CheckInMinutes <= 0 || _activeAlerts.Count > 0) return false;
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (!TimeOnly.TryParse(_settings.WorkdayStart, out var s) || !TimeOnly.TryParse(_settings.WorkdayEnd, out var e)) return false;
        var t = TimeOnly.FromDateTime(now.LocalDateTime);
        if (t < s || t > e) return false;
        // Don't nag right before a meeting; the meeting alerts already cover that.
        var next = AlertScheduler.NextEvent(Store.State.CachedEvents, now);
        if (next is not null && next.Start - now < TimeSpan.FromMinutes(10)) return false;
        return now - _lastCheckIn >= TimeSpan.FromMinutes(_settings.CheckInMinutes);
    }

    static string EventKey(CalendarEvent e) => $"{e.Source}|{e.Id}|{e.Start.ToUnixTimeSeconds()}";

    public void Acknowledge(DueAlert alert)
    {
        Store.Mutate(s =>
        {
            foreach (var k in alert.KeysToMark) s.FiredAlerts.Add(k);
            s.SnoozedUntil.Remove(EventKey(alert.Event));
        });
        _activeAlerts.Remove(EventKey(alert.Event));
        Tick(DateTimeOffset.Now); // a closer offset may already be due
    }

    public void Snooze(DueAlert alert, int minutes)
    {
        var until = DateTimeOffset.Now.AddMinutes(minutes);
        // Never snooze past the meeting start unless it already started.
        if (alert.Event.Start > DateTimeOffset.Now && until > alert.Event.Start) until = alert.Event.Start;
        Store.Mutate(s => s.SnoozedUntil[EventKey(alert.Event)] = until);
        _activeAlerts.Remove(EventKey(alert.Event));
    }

    // ---------------------------------------------------------------- refresh

    public async Task RefreshAllAsync(bool forceMessages)
    {
        lock (_refreshGate) { if (_refreshing) return; _refreshing = true; }
        try
        {
            await RefreshCalendarsAsync();
            if (forceMessages || DateTimeOffset.Now - _lastMessageRefresh > TimeSpan.FromMinutes(_settings.MessageRefreshMinutes))
                await RefreshMessagesAsync();
        }
        finally { lock (_refreshGate) _refreshing = false; }
        Changed?.Invoke();
    }

    async Task RefreshCalendarsAsync()
    {
        var from = DateTimeOffset.Now.AddHours(-2);
        var to = DateTimeOffset.Now.AddHours(48);
        var fresh = new List<CalendarEvent>();
        var failed = new List<string>();

        foreach (var src in _calendars)
        {
            try
            {
                fresh.AddRange(await src.GetEventsAsync(from, to, _cts.Token));
                SourceErrors.Remove(src.Name);
            }
            catch (Exception ex)
            {
                SourceErrors[src.Name] = ex.Message;
                failed.Add(src.Name);
            }
        }

        // A failing source keeps its last good events so alerts survive an outage.
        var keep = Store.State.CachedEvents.Where(e => failed.Any(f => e.Source.StartsWith(f, StringComparison.OrdinalIgnoreCase)) && e.End > from);
        _testEvents.RemoveAll(e => e.End < DateTimeOffset.Now);
        var merged = Dedupe(fresh.Concat(keep.Where(e => e.Source != Bridge.EventSource)).Concat(_testEvents).Concat(Store.State.BridgeEvents)).Where(e => e.End > from && e.Start < to).ToList();

        if (_calendars.Count > 0 || _testEvents.Count > 0 || Store.State.BridgeEvents.Count > 0)
            Store.Mutate(s => { s.CachedEvents = merged; if (failed.Count < _calendars.Count) s.EventsRefreshed = DateTimeOffset.Now; });
    }

    public static IEnumerable<CalendarEvent> Dedupe(IEnumerable<CalendarEvent> events) =>
        events.GroupBy(e => (Norm(e.Title), e.Start, e.IsAllDay))
              .Select(g => g.OrderByDescending(e => e.JoinUrl is not null).First());

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    async Task RefreshMessagesAsync()
    {
        _lastMessageRefresh = DateTimeOffset.Now;
        var since = DateTimeOffset.Now.AddDays(-2);
        var newItems = new List<InboxItem>();

        foreach (var src in _messages)
        {
            try
            {
                newItems.AddRange(await src.GetRecentAsync(since, _cts.Token));
                SourceErrors.Remove(src.Name);
            }
            catch (Exception ex) { SourceErrors[src.Name] = ex.Message; }
        }
        if (newItems.Count == 0) return;

        var created = new List<SuggestedTask>();
        var aiEligible = newItems.Where(i => !i.IsWork || WorkAllowedForAi).ToList();
        var local = newItems.Except(aiEligible).ToList();

        // Local heuristics for anything we will not (or cannot) send to Claude.
        foreach (var i in local.Where(LooksActionable))
            created.Add(ToTask(i, i.Subject.Length > 0 ? i.Subject : Shorten(i.Snippet), 0.4, null));

        var classifiedIds = new HashSet<string>();
        if (Claude is not null && aiEligible.Count > 0)
        {
            foreach (var chunk in aiEligible.Chunk(15))
            {
                try
                {
                    var byId = chunk.ToDictionary(i => i.Id);
                    foreach (var c in await Busy(() => Claude.ClassifyAsync(chunk, _cts.Token)))
                    {
                        if (!byId.TryGetValue(c.Id, out var item)) continue;
                        classifiedIds.Add(c.Id);
                        if (c.Action && c.Confidence >= 0.6 && c.Title.Length > 0)
                            created.Add(ToTask(item, c.Title, c.Confidence, c.Due));
                    }
                }
                catch (Exception ex) { SourceErrors["Claude"] = ex.Message; }
            }
        }
        // AI unavailable, over budget or failed: fall back to heuristics so nothing is silently lost.
        foreach (var i in aiEligible.Where(i => !classifiedIds.Contains(i.Id) && LooksActionable(i)))
            created.Add(ToTask(i, i.Subject.Length > 0 ? i.Subject : Shorten(i.Snippet), 0.4, null));

        Store.Mutate(s =>
        {
            foreach (var i in newItems) s.SeenItemIds.Add(i.Id);
            foreach (var t in created.Where(t => s.Tasks.All(x => x.Id != t.Id))) s.Tasks.Add(t);
        });
    }

    static readonly string[] ActionWords =
        ["please", "can you", "could you", "action required", "deadline", "by eod", "by end of day", "urgent", "asap", "reminder", "rsvp", "approve", "review", "?"];

    public static bool LooksActionable(InboxItem i)
    {
        var t = (i.Subject + " " + i.Snippet).ToLowerInvariant();
        if (t.Contains("unsubscribe") || i.From.Contains("noreply", StringComparison.OrdinalIgnoreCase) || i.From.Contains("no-reply", StringComparison.OrdinalIgnoreCase)) return false;
        return ActionWords.Any(t.Contains);
    }

    static SuggestedTask ToTask(InboxItem i, string title, double conf, DateTimeOffset? due) => new()
    {
        Id = i.Id, Title = title, Source = i.Source, From = i.From, Link = i.Link, Confidence = conf, Due = due,
    };

    static string Shorten(string s) => s.Length <= 80 ? s : s[..80] + "…";

    // ---------------------------------------------------------------- tasks & focus

    public void SetStatus(string id, SuggestionStatus status) =>
        Store.Mutate(s => { var t = s.Tasks.FirstOrDefault(x => x.Id == id); if (t is not null) t.Status = status; });

    public void SetCurrentTask(string title) =>
        Store.Mutate(s => { s.CurrentTask = title; s.CurrentTaskSince = DateTimeOffset.Now; });

    public void SaveCheckpoint(string note) =>
        Store.Mutate(s => { s.Checkpoint = note; s.CheckpointAt = DateTimeOffset.Now; });

    public void CheckInAnswered() => _lastCheckIn = DateTimeOffset.Now;

    public async Task<string> PlanDayAsync(bool eveningSweep = false)
    {
        if (Claude is null) return CliMissing ? "Couldn't find the Claude app on this PC. Install it and sign in, or add an Anthropic API key in Settings." : "AI is off. In Settings, turn on 'Use my Claude login' or add an Anthropic API key.";
        if (!Claude.Allow(background: false)) return "AI budget limit reached for this month. " + Budget.Summary;
        var now = DateTimeOffset.Now;
        var plan = await Busy(() => Claude.PlanDayAsync(
            ForAi(Store.State.CachedEvents.Where(e => e.End > now && e.Start < now.AddHours(36))),
            ForAi(Store.State.Tasks.Where(t => t.Status is SuggestionStatus.Pending or SuggestionStatus.Accepted).Take(12)),
            Store.State.CurrentTask, eveningSweep, _cts.Token));
        if (!eveningSweep) Store.Mutate(s => { s.LastPlan = plan; s.LastPlanAt = DateTimeOffset.Now; });
        return plan;
    }

    /// <summary>Local summary first (works offline); AI polish only when available.</summary>
    public string RecoverLocal()
    {
        var now = DateTimeOffset.Now;
        var st = Store.State;
        var live = st.CachedEvents.Where(e => !e.Cancelled && !e.IsAllDay && e.Start <= now && e.End > now).OrderBy(e => e.End).FirstOrDefault();
        var next = st.CachedEvents.Where(e => !e.Cancelled && !e.IsAllDay && e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
        var lines = new List<string>
        {
            st.CurrentTask.Length > 0
                ? $"You were working on: {st.CurrentTask}" + (st.CurrentTaskSince is { } since ? $" (since {Fmt.Time(since.LocalDateTime)})" : "")
                : "No current task set. Pick one thing and type it below.",
        };
        if (st.Checkpoint.Length > 0) lines.Add($"Last note ({Fmt.Time(st.CheckpointAt?.LocalDateTime)}): {st.Checkpoint}");
        if (live is not null) lines.Add($"In progress: {live.Title} (ends {Fmt.Time(live.End.LocalDateTime)}).");
        lines.Add(next is null ? "No more meetings on the calendar." : $"Next: {next.Title} {AlertScheduler.Describe(next, now)} ({Fmt.Time(next.Start.LocalDateTime)}).");
        var overdue = st.Tasks.Where(t => t.Status is SuggestionStatus.Pending or SuggestionStatus.Accepted && t.Due is { } d && d < now).ToList();
        if (overdue.Count > 0) lines.Add($"Overdue: {string.Join("; ", overdue.Take(3).Select(t => t.Title))}");
        var top = st.Tasks.Where(t => t.Status == SuggestionStatus.Accepted || t.Status == SuggestionStatus.Pending).OrderByDescending(t => t.Confidence).FirstOrDefault();
        if (top is not null) lines.Add($"Open follow-up: {top.Title}");
        return string.Join(Environment.NewLine, lines);
    }

    public async Task<string?> RecoverAiAsync()
    {
        if (Claude is null || !Claude.Allow(background: false)) return null;
        var st = Store.State;
        var next = AlertScheduler.NextEvent(ForAi(st.CachedEvents), DateTimeOffset.Now);
        return await Busy(() => Claude.RecoverAsync(st.CurrentTask, st.Checkpoint, next,
            ForAi(st.Tasks.Where(t => t.Status is SuggestionStatus.Pending or SuggestionStatus.Accepted)), _cts.Token));
    }

    public void Dispose()
    {
        _bridgeWatcher?.Dispose();
        _bridgeDebounce?.Dispose();
        _cts.Cancel();
        _http.Dispose();
    }
}
