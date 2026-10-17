namespace Anchor.Core;

public sealed record DoneItem(string Title, DateTimeOffset At);

/// <summary>
/// The "Right now" model: one current task, an optional breadcrumb note (what was on my mind), and a short
/// history of what got finished so completing things can be seen and celebrated.
/// </summary>
public sealed class FocusTracker(StateStore store)
{
    public void SetTask(string title, DateTimeOffset now)
    {
        title = title.Trim();
        if (title.Length == 0) return;
        store.Mutate(s => { s.CurrentTask = title; s.CurrentTaskSince = now; });
    }

    /// <summary>"Still on it": same task, fresh start time (clears the stale marker).</summary>
    public void KeepTask(DateTimeOffset now) =>
        store.Mutate(s => { if (s.CurrentTask.Length > 0) s.CurrentTaskSince = now; });

    /// <summary>Marks the current task finished (logged in history) and clears it. False when there was none.</summary>
    public bool Finish(DateTimeOffset now)
    {
        var had = false;
        store.Mutate(s =>
        {
            if (s.CurrentTask.Length == 0) return;
            s.Done.Add(new DoneItem(s.CurrentTask, now));
            s.CurrentTask = ""; s.CurrentTaskSince = null;
            had = true;
        });
        return had;
    }

    /// <summary>Drops the current task without counting it as finished.</summary>
    public void ClearTask() => store.Mutate(s => { s.CurrentTask = ""; s.CurrentTaskSince = null; });

    /// <summary>Saves the breadcrumb; an empty note clears it.</summary>
    public void SetNote(string note, DateTimeOffset now)
    {
        note = note.Trim();
        store.Mutate(s => { s.Checkpoint = note; s.CheckpointAt = note.Length == 0 ? null : now; });
    }

    public void ClearNote() => SetNote("", DateTimeOffset.Now);

    /// <summary>A task from an earlier day, or untouched for 12+ hours, is probably not what you are doing now.</summary>
    public static bool IsStale(DateTimeOffset? since, DateTimeOffset now) =>
        since is { } s && (s.LocalDateTime.Date < now.LocalDateTime.Date || now - s > TimeSpan.FromHours(12));

    public IReadOnlyList<DoneItem> DoneToday(DateTimeOffset now) =>
        store.State.Done.Where(d => d.At.LocalDateTime.Date == now.LocalDateTime.Date).OrderBy(d => d.At).ToList();
}
