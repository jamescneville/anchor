namespace Anchor.Core;

/// <summary>Which side (Personal / Work) a to-do belongs to, and how pressing it is (1 = most, 4 = least).</summary>
public static class TaskGroups
{
    public static string GroupOf(SuggestedTask t) =>
        t.Source.Contains("Outlook", StringComparison.OrdinalIgnoreCase) || t.Source.Contains("Teams", StringComparison.OrdinalIgnoreCase)
        || t.Source.Contains("Slack", StringComparison.OrdinalIgnoreCase) || t.Source.Contains("Claude", StringComparison.OrdinalIgnoreCase) ? "Work" : "Personal";

    public static string GroupOf(TodoItem t) =>
        t.Group.Length > 0 ? t.Group : t.Source.Equals("Inbox sweep", StringComparison.OrdinalIgnoreCase) ? "Work" : "Personal";

    public static int PriorityOf(SuggestedTask t, DateTimeOffset now)
    {
        if (t.Due is { } d)
        {
            if (d < now) return 1;
            if (d - now <= TimeSpan.FromDays(2)) return 2;
        }
        return t.Status == SuggestionStatus.Accepted || t.Confidence >= 0.7 ? 3 : 4;
    }

    /// <summary>Priority for a to-do: what the sweep gave it, otherwise middling so it sorts after urgent things but before the rest.</summary>
    public static int PriorityOf(TodoItem t) => t.Priority is >= 1 and <= 5 ? Math.Min(t.Priority, 4) : 3;
}
