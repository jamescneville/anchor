using System.Text.Json;

namespace Anchor.Core;

public sealed class AnchorState
{
    public HashSet<string> FiredAlerts { get; set; } = new();
    public Dictionary<string, DateTimeOffset> SnoozedUntil { get; set; } = new();
    public HashSet<string> SeenItemIds { get; set; } = new();
    public List<SuggestedTask> Tasks { get; set; } = new();
    public List<CalendarEvent> CachedEvents { get; set; } = new();
    public DateTimeOffset? EventsRefreshed { get; set; }

    // Focus / recovery
    public string CurrentTask { get; set; } = "";
    public DateTimeOffset? CurrentTaskSince { get; set; }
    public string Checkpoint { get; set; } = "";
    public DateTimeOffset? CheckpointAt { get; set; }

    // AI budget ledger (USD), keyed by yyyy-MM-dd
    public Dictionary<string, double> SpendByDay { get; set; } = new();

    public string LastMorningBrief { get; set; } = "";
    public string LastEveningBrief { get; set; } = "";

    /// <summary>Latest plan.md dropped by Claude desktop into the bridge folder.</summary>
    public string? BridgePlan { get; set; }
    public DateTimeOffset? BridgePlanAt { get; set; }

    /// <summary>Calendar events dropped into the bridge by Claude desktop (work Outlook via the Microsoft 365 connector).</summary>
    public List<CalendarEvent> BridgeEvents { get; set; } = new();
    public ClaudeUsage? AccountUsage { get; set; }
    public List<DoneItem> Done { get; set; } = new();
    public SweepResult? Sweep { get; set; }
    /// <summary>Sweep items the user has dealt with (done, dismissed, queued for trash, or moved to the to-do list).</summary>
    public HashSet<string> SweepHandled { get; set; } = new();
    public List<string> TrashQueue { get; set; } = new();
    /// <summary>How each handled sweep item was set aside: handled | today | trash.</summary>
    public Dictionary<string, string> SweepHow { get; set; } = new();
    public List<TodoItem> Todos { get; set; } = new();
    /// <summary>Main-window layout the user dragged to: agenda share of the agenda+to-do width (0..1) and the plan card height in pixels.</summary>
    public double? AgendaRatio { get; set; }
    /// <summary>To-do filter: All | Personal | Work.</summary>
    public string TodoView { get; set; } = "All";
    public double? PlanHeight { get; set; }
    public List<KnownCalendar> KnownCalendars { get; set; } = new();
    public DateTimeOffset? BridgeEventsAt { get; set; }

    public string? LastPlan { get; set; }
    public DateTimeOffset? LastPlanAt { get; set; }
}

public sealed class StateStore
{
    readonly string _path;
    readonly object _gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AnchorState State { get; private set; }

    public StateStore(string? path = null)
    {
        _path = path ?? AppPaths.StateFile;
        State = Load();
    }

    AnchorState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AnchorState>(File.ReadAllText(_path), Json) ?? new();
        }
        catch { /* corrupt state must never stop the assistant; start clean */ }
        return new();
    }

    public void Mutate(Action<AnchorState> change)
    {
        lock (_gate)
        {
            change(State);
            Prune();
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(State, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    void Prune()
    {
        var cutoff = DateTimeOffset.Now.AddDays(-3).ToUnixTimeSeconds();
        State.FiredAlerts.RemoveWhere(k =>
        {
            var parts = k.Split('|');
            return parts.Length >= 4 && long.TryParse(parts[^2], out var ts) && ts < cutoff;
        });
        foreach (var k in State.SnoozedUntil.Where(kv => kv.Value < DateTimeOffset.Now.AddDays(-1)).Select(kv => kv.Key).ToList())
            State.SnoozedUntil.Remove(k);
        if (State.SeenItemIds.Count > 2000)
            State.SeenItemIds = State.SeenItemIds.TakeLast(1000).ToHashSet();
        State.Done.RemoveAll(d => d.At < DateTimeOffset.Now.AddDays(-7));
        State.Tasks.RemoveAll(t => t.Status is SuggestionStatus.Dismissed or SuggestionStatus.Done && t.Created < DateTimeOffset.Now.AddDays(-14));
        foreach (var k in State.SpendByDay.Keys.Where(k => string.CompareOrdinal(k, DateTime.Now.AddDays(-62).ToString("yyyy-MM-dd")) < 0).ToList())
            State.SpendByDay.Remove(k);
    }
}
