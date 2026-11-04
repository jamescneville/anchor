namespace Anchor.Core;

public sealed record CalendarEvent(
    string Id,
    string Source,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? JoinUrl = null,
    string? Location = null,
    bool IsAllDay = false,
    bool Cancelled = false,
    string? OpenUrl = null,    // link to open the event itself (e.g. in Outlook on the web)
    string? Color = null);     // the calendar's own colour (#RRGGBB) when its provider gives one

/// <summary>Account-level Claude spend as last reported by the sync task (the app's own usage card figures).</summary>
public sealed record ClaudeUsage(string Plan, double Spent, double Limit, double PercentUsed, string Currency, DateTimeOffset CheckedAt);

public sealed record InboxItem(
    string Id,
    string Source,
    bool IsWork,
    string From,
    string Subject,
    string Snippet,
    DateTimeOffset Received,
    string? Link = null);

public enum SuggestionStatus { Pending, Accepted, Dismissed, Done }

public sealed class SuggestedTask
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string From { get; set; } = "";
    public string? Link { get; set; }
    public double Confidence { get; set; }
    public DateTimeOffset? Due { get; set; }
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
}

public interface ICalendarSource
{
    string Name { get; }
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public interface IMessageSource
{
    string Name { get; }
    bool IsWork { get; }
    Task<IReadOnlyList<InboxItem>> GetRecentAsync(DateTimeOffset since, CancellationToken ct);
}

/// <summary>One loose end found by the inbox sweep. Priority 1 (most pressing) to 5.</summary>
public sealed class SweepItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string From { get; set; } = "";
    public DateTimeOffset? Received { get; set; }
    public string? Link { get; set; }
    public string Category { get; set; } = "Other";
    public int Priority { get; set; } = 3;
    public string Why { get; set; } = "";
    public string Action { get; set; } = "";
    public DateTimeOffset? Due { get; set; }
}

public sealed class SweepResult
{
    public DateTimeOffset GeneratedAt { get; set; }
    public int Scanned { get; set; }
    public string Scope { get; set; } = "";
    public List<SweepItem> Items { get; set; } = new();
}

/// <summary>An entry on today's to-do list (shown on the main window).</summary>
public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string? Link { get; set; }
    public string Source { get; set; } = "";
    public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? DoneAt { get; set; }
    /// <summary>Personal | Work. Empty means "work out from the source".</summary>
    public string Group { get; set; } = "";
    /// <summary>1 (urgent) to 5, from the inbox sweep; 0 when not ranked.</summary>
    public int Priority { get; set; }
}
