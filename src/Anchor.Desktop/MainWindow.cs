using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Anchor.Core;
using Anchor.Core.Connectors;

namespace Anchor.Desktop;

public sealed class MainWindow : Window
{
    readonly AssistantEngine _engine;
    readonly TextBlock _now = new() { FontSize = 22, FontWeight = FontWeights.Bold };
    readonly TextBlock _next = new() { FontSize = 16, Margin = new Thickness(0, 4, 0, 0) };
    readonly StackPanel _agenda = new();
    readonly StackPanel _tasks = new();
    readonly TextBox _plan = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock _status = new() { FontSize = 12, Foreground = (Brush)Application.Current.Resources["Dim"] };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(AssistantEngine engine)
    {
        _engine = engine;
        Title = "Anchor";
        Width = 980; Height = 760;
        Background = (Brush)Application.Current.Resources["Bg"];

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Right now
        var hero = Card(new StackPanel { Children = { Label("RIGHT NOW"), _now, _next } });
        Grid.SetRow(hero, 0);
        root.Children.Add(hero);

        // Today + needs attention
        var cols = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        var left = Card(new DockPanel { Children = { Docked(Label("TODAY & TOMORROW"), Dock.Top), new ScrollViewer { Content = _agenda, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } });
        var right = Card(new DockPanel { Children = { Docked(Label("NEEDS ATTENTION"), Dock.Top), new ScrollViewer { Content = _tasks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } });
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
        cols.Children.Add(left); cols.Children.Add(right);
        Grid.SetRow(cols, 1);
        root.Children.Add(cols);

        // Plan
        var planBtn = new Button { Content = "Plan my day" };
        planBtn.Click += async (_, _) =>
        {
            planBtn.IsEnabled = false; _plan.Text = "Planning…";
            try { _plan.Text = await _engine.PlanDayAsync(); }
            catch (Exception ex) { _plan.Text = "Could not plan: " + ex.Message; }
            planBtn.IsEnabled = true; RenderStatus();
        };
        var plan = Card(new StackPanel { Children = { Label("PLAN"), _plan, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { planBtn } } } });
        Grid.SetRow(plan, 2);
        root.Children.Add(plan);

        // Toolbar
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(Tool($"Where was I?  ({_engine.Settings.Hotkey})", () => new RecoveryWindow(_engine, false).Show()));
        bar.Children.Add(Tool("Refresh now", () => _ = _engine.RefreshAllAsync(forceMessages: true)));
        foreach (var p in _engine.Providers)
        {
            var provider = p;
            bar.Children.Add(Tool($"{(_engine.IsConnected(provider) ? "Reconnect" : "Connect")} {provider}", async () =>
            {
                try { await _engine.ConnectAsync(provider, CancellationToken.None); await _engine.RefreshAllAsync(true); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Anchor"); }
                Render();
            }));
        }
        bar.Children.Add(Tool("Settings", () => new SettingsWindow(_engine).Show()));
        bar.Children.Add(_status);
        _status.VerticalAlignment = VerticalAlignment.Center;
        var barHost = new StackPanel { Children = { bar } };
        Grid.SetRow(barHost, 3);
        root.Children.Add(barHost);

        Content = root;

        _engine.Changed += () => Dispatcher.BeginInvoke(Render);
        _clock.Tick += (_, _) => RenderNow();
        _clock.Start();
        Closing += (_, e) => { e.Cancel = true; Hide(); }; // lives in the tray
        Render();
    }

    void RenderNow()
    {
        var st = _engine.Store.State;
        var now = DateTimeOffset.Now;
        _now.Text = st.CurrentTask.Length > 0 ? st.CurrentTask : $"No task set. Press {_engine.Settings.Hotkey} and pick one thing.";
        var next = AlertScheduler.NextEvent(st.CachedEvents, now);
        _next.Text = next is null ? "No upcoming meetings." : $"Next: {next.Title}, {AlertScheduler.Describe(next, now)} ({next.Start.LocalDateTime:HH:mm})";
        _next.Foreground = next is not null && next.Start - now < TimeSpan.FromMinutes(5)
            ? (Brush)Application.Current.Resources["Alarm"] : (Brush)Application.Current.Resources["Fg"];
    }

    void Render()
    {
        RenderNow();
        var st = _engine.Store.State;
        var now = DateTimeOffset.Now;

        _agenda.Children.Clear();
        var events = st.CachedEvents.Where(e => !e.Cancelled && e.End > now).OrderBy(e => e.IsAllDay ? 0 : 1).ThenBy(e => e.Start).ToList();
        if (events.Count == 0) _agenda.Children.Add(Dim("Nothing scheduled. Connect a calendar, or you are free."));
        DateTime? lastDay = null;
        foreach (var e in events)
        {
            if (lastDay != e.Start.LocalDateTime.Date)
            {
                lastDay = e.Start.LocalDateTime.Date;
                _agenda.Children.Add(new TextBlock { Text = lastDay == DateTime.Today ? "Today" : lastDay == DateTime.Today.AddDays(1) ? "Tomorrow" : lastDay.Value.ToString("dddd"), Foreground = (Brush)Application.Current.Resources["Accent"], Margin = new Thickness(0, 8, 0, 2), FontWeight = FontWeights.SemiBold });
            }
            _agenda.Children.Add(new TextBlock
            {
                Text = (e.IsAllDay ? "all day  " : $"{e.Start.LocalDateTime:HH:mm}  ") + e.Title,
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = e.Start <= now ? (Brush)Application.Current.Resources["Alarm"] : (Brush)Application.Current.Resources["Fg"],
            });
        }

        _tasks.Children.Clear();
        var open = st.Tasks.Where(t => t.Status is SuggestionStatus.Pending or SuggestionStatus.Accepted)
                           .OrderByDescending(t => t.Status == SuggestionStatus.Accepted).ThenByDescending(t => t.Confidence).ToList();
        if (open.Count == 0) _tasks.Children.Add(Dim("Nothing flagged. New items appear here after each mail/chat refresh."));
        foreach (var t in open) _tasks.Children.Add(TaskRow(t));

        if (st.LastPlan is { Length: > 0 } && _plan.Text.Length == 0) _plan.Text = st.LastPlan;
        RenderStatus();
    }

    void RenderStatus()
    {
        var errs = _engine.SourceErrors.Count == 0 ? "" : "  ⚠ " + string.Join("; ", _engine.SourceErrors.Select(kv => $"{kv.Key}: {Short(kv.Value)}"));
        var cal = _engine.Store.State.EventsRefreshed is { } r ? $"calendar synced {r.LocalDateTime:HH:mm}" : "calendar not synced";
        _status.Text = $"{cal}  ·  {_engine.Budget.Summary}{(_engine.Claude is null ? "  ·  AI off (add API key in Settings)" : "")}{errs}";
    }

    static string Short(string s) => s.Length <= 90 ? s : s[..90] + "…";

    UIElement TaskRow(SuggestedTask t)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold });
        row.Children.Add(Dim($"{t.Source} · {t.From}{(t.Due is { } d ? $" · due {d.LocalDateTime:ddd HH:mm}" : "")} · {(t.Confidence >= 0.6 ? "AI" : "keyword")} {t.Confidence:P0}"));
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (t.Link is { Length: > 0 } link)
        {
            var open = new Button { Content = "Open", Padding = new Thickness(8, 3, 8, 3) };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            btns.Children.Add(open);
        }
        if (t.Status == SuggestionStatus.Pending)
        {
            var keep = new Button { Content = "Keep", Padding = new Thickness(8, 3, 8, 3) };
            keep.Click += (_, _) => { _engine.SetStatus(t.Id, SuggestionStatus.Accepted); Render(); };
            btns.Children.Add(keep);
        }
        var done = new Button { Content = "Done", Padding = new Thickness(8, 3, 8, 3) };
        done.Click += (_, _) => { _engine.SetStatus(t.Id, SuggestionStatus.Done); Render(); };
        var no = new Button { Content = "Not a task", Padding = new Thickness(8, 3, 8, 3) };
        no.Click += (_, _) => { _engine.SetStatus(t.Id, SuggestionStatus.Dismissed); Render(); };
        var doing = new Button { Content = "Doing now", Padding = new Thickness(8, 3, 8, 3) };
        doing.Click += (_, _) => { _engine.SetCurrentTask(t.Title); Render(); };
        btns.Children.Add(doing); btns.Children.Add(done); btns.Children.Add(no);
        row.Children.Add(btns);
        return row;
    }

    static Border Card(UIElement child) => new() { Background = (Brush)Application.Current.Resources["Card"], Padding = new Thickness(16), Child = child };
    static TextBlock Label(string s) => new() { Text = s, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["Dim"], Margin = new Thickness(0, 0, 0, 6) };
    static TextBlock Dim(string s) => new() { Text = s, FontSize = 12, Foreground = (Brush)Application.Current.Resources["Dim"] };
    static UIElement Docked(UIElement e, Dock d) { DockPanel.SetDock(e, d); return e; }
    static Button Tool(string text, Action click) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) }; b.Click += (_, _) => click(); return b; }
}
