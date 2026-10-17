namespace Anchor.Core.AI;

/// <summary>
/// Redacts work-sourced items before anything is put into an AI prompt, unless the user has explicitly allowed
/// work content to go to Claude (Ai.SendWorkContentToClaude). Work = Outlook, Teams, Slack and ICS feeds not marked personal.
/// </summary>
public static class AiPrivacy
{
    public static bool IsWorkSource(string source, IReadOnlySet<string> workIcsSources) =>
        source.Contains("Outlook", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("Teams", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("Slack", StringComparison.OrdinalIgnoreCase) ||
        workIcsSources.Contains(source);

    /// <summary>Work meetings keep their times (so the plan respects them) but lose title, location and link.</summary>
    public static IReadOnlyList<CalendarEvent> Events(IEnumerable<CalendarEvent> events, bool allowWork, IReadOnlySet<string> workIcsSources) =>
        events.Select(e => allowWork || !IsWorkSource(e.Source, workIcsSources)
            ? e
            : e with { Title = "Busy (work meeting)", Location = null, JoinUrl = null }).ToList();

    /// <summary>Work follow-ups collapse into one count line; personal ones pass through.</summary>
    public static IReadOnlyList<SuggestedTask> Tasks(IEnumerable<SuggestedTask> tasks, bool allowWork, IReadOnlySet<string> workIcsSources)
    {
        var all = tasks.ToList();
        if (allowWork) return all;
        var work = all.Where(t => IsWorkSource(t.Source, workIcsSources)).ToList();
        var result = all.Except(work).ToList();
        if (work.Count > 0)
            result.Add(new SuggestedTask { Id = "work-redacted", Title = $"{work.Count} work follow-up{(work.Count == 1 ? "" : "s")} (details withheld)", Source = "Work", Status = SuggestionStatus.Accepted });
        return result;
    }
}
