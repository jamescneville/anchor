namespace Anchor.Core;

/// <summary>When the Claude sync tasks next run (mirrors their cron schedules: weekdays hourly 7-18, weekends 9/13/17/21). Claude adds a few minutes of jitter.</summary>
public static class SyncSchedule
{
    static readonly int[] WeekendHours = [9, 13, 17, 21];

    public static DateTime Next(DateTime now)
    {
        for (var d = 0; d <= 8; d++)
        {
            var day = now.Date.AddDays(d);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            IEnumerable<int> hours = weekend ? WeekendHours : Enumerable.Range(7, 12);
            foreach (var h in hours)
            {
                var t = day.AddHours(h);
                if (t > now) return t;
            }
        }
        return now.AddHours(1);
    }
}
