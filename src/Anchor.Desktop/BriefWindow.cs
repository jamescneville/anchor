using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Anchor.Core;

namespace Anchor.Desktop;

/// <summary>Morning plan / end-of-day wrap-up. Local facts appear instantly; the AI plan fills in if available.</summary>
public sealed class BriefWindow : Window
{
    public BriefWindow(AssistantEngine engine, string kind)
    {
        var morning = kind == "morning";
        Title = morning ? "Anchor · Morning plan" : "Anchor · End-of-day wrap-up";
        Topmost = true;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.Style(this);

        var st = engine.Store.State;
        var now = DateTimeOffset.Now;
        var today = st.CachedEvents.Where(e => !e.Cancelled && !e.IsAllDay && e.Start.LocalDateTime.Date == DateTime.Today && e.Start > now).OrderBy(e => e.Start).ToList();
        var open = st.Tasks.Where(t => t.Status is SuggestionStatus.Pending or SuggestionStatus.Accepted).ToList();
        var facts = morning
            ? $"{today.Count} meeting{(today.Count == 1 ? "" : "s")} still to come today" + (today.Count > 0 ? $", first at {Fmt.Time(today[0].Start.LocalDateTime)} ({today[0].Title})" : "") + $". {open.Count} open follow-up{(open.Count == 1 ? "" : "s")}."
            : $"{open.Count} open follow-up{(open.Count == 1 ? "" : "s")}" + (open.Count > 0 ? ": " + string.Join("; ", open.Take(4).Select(t => t.Title)) : "") + ".";

        var ai = new TextBlock { Text = "Thinking…", Foreground = (Brush)Application.Current.Resources["Accent"], FontSize = 14.5, LineHeight = 22, Margin = new Thickness(0, 14, 0, 18) };
        var ok = Ui.Primary(morning ? "Let's go" : "Done for today", 22, 10);
        ok.HorizontalAlignment = HorizontalAlignment.Left;
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(28),
            Children =
            {
                new TextBlock { Text = morning ? "Good morning" : "Wrapping up", FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"] },
                new TextBlock { Text = facts, FontSize = 14.5, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 8, 0, 0) },
                new ScrollViewer { MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = ai },
                ok,
            },
        };

        Loaded += async (_, _) =>
        {
            try { ai.Text = await engine.PlanDayAsync(eveningSweep: !morning); }
            catch (Exception ex) { ai.Text = "(AI unavailable: " + ex.Message + ")"; }
        };
    }
}
