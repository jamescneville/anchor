using System.Text.RegularExpressions;

namespace Anchor.Core;

/// <summary>Turns an alert into something that sounds like a person said it, instead of reading a calendar title and "min" verbatim.</summary>
public static partial class SpeechText
{
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")] private static partial Regex Bracketed();
    [GeneratedRegex(@"[\p{So}\p{Cs}|*_#~^]")] private static partial Regex Noise();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex Spaces();
    [GeneratedRegex(@"\b1:1\b")] private static partial Regex OneOnOne();

    public static string Clean(string title)
    {
        var t = Bracketed().Replace(title, " ");
        t = OneOnOne().Replace(t, "one on one").Replace("&", " and ").Replace("w/", "with ").Replace("w/o", "without ").Replace(" vs. ", " versus ").Replace(" vs ", " versus ");
        t = Noise().Replace(t, " ");
        t = Spaces().Replace(t, " ").Trim(' ', '-', ':', '.');
        // Long titles: keep the part before the first separator that leaves something meaningful.
        if (t.Length > 60)
            foreach (var sep in new[] { " - ", " : ", ": ", " – ", " — " })
            {
                var i = t.IndexOf(sep, StringComparison.Ordinal);
                if (i >= 12) { t = t[..i]; break; }
            }
        if (t.Length > 70) t = t[..70].TrimEnd() ;
        return t.Length > 0 ? t : "your meeting";
    }

    static string Minutes(int n) => n == 1 ? "one minute" : $"{n} minutes";

    public static string Phrase(string title, DateTimeOffset start, DateTimeOffset now)
    {
        var name = Clean(title);
        var delta = start - now;
        if (delta.TotalMinutes >= 120)
        {
            var h = (int)delta.TotalHours; var m = delta.Minutes;
            return $"Heads up. {name} starts in {h} hours" + (m > 0 ? $" and {Minutes(m)}." : ".");
        }
        if (delta.TotalSeconds > 60) return $"{name} starts in {Minutes((int)Math.Ceiling(delta.TotalMinutes))}.";
        if (delta.TotalSeconds > -30) return $"{name} is starting now.";
        return $"{name} started {Minutes(Math.Max(1, (int)Math.Floor(-delta.TotalMinutes)))} ago.";
    }
}
