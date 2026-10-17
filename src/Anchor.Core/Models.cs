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
    bool Cancelled = false);

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
