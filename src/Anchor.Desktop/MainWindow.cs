using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Anchor.Core;

namespace Anchor.Desktop;

public sealed class MainWindow : Window
{
    readonly AssistantEngine _engine;
    readonly TextBlock _date = new() { FontSize = 12, Foreground = Ui.B("Dim") };
    readonly WrapPanel _chips = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _now = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"] };
    readonly TextBlock _inProgress = new() { FontSize = 13, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _next = new() { FontSize = 15, Margin = new Thickness(0, 4, 0, 0) };
    readonly StackPanel _agenda = new();
    readonly StackPanel _tasks = new();
    readonly TextBlock _plan = new() { FontSize = 13.5, LineHeight = 21 };
    readonly TextBlock _planWhen = new() { FontSize = 11.5, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 8, 0, 0) };
    readonly WrapPanel _problems = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Button _planBtn = Ui.Primary("Plan my day");
    string _planOverride = "";

    public MainWindow(AssistantEngine engine)
    {
        _engine = engine;
        Title = "Anchor";
        Width = 1080; Height = 800; MinWidth = 820; MinHeight = 600;
        Ui.Style(this);
        // Our own header acts as the title bar; Windows still draws the minimise/maximise/close buttons.
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 68, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0),
        });

        var root = new Grid { Margin = new Thickness(26, 0, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: title + source chips on the left, actions on the right
        var header = new DockPanel { Height = 68, Margin = new Thickness(0, 0, 138, 8), LastChildFill = false };
        var titleText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        titleText.Children.Add(new TextBlock { Text = "Anchor", FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"] });
        titleText.Children.Add(_date);
        var logo = new Image { Source = Ui.AppIcon, Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var titleBox = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0), Children = { logo, titleText } };
        DockPanel.SetDock(titleBox, Dock.Left);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var where = Ui.Primary("Where was I?");
        where.ToolTip = $"Global hotkey: {_engine.Settings.Hotkey}";
        where.Click += (_, _) => new RecoveryWindow(_engine, false).Show();
        var refresh = Ui.IconButton("\uE72C", "Refresh calendars and mail now");
        refresh.Click += (_, _) => _ = _engine.RefreshAllAsync(forceMessages: true);
        var settings = Ui.IconButton("\uE713", "Settings");
        settings.Click += (_, _) => new SettingsWindow(_engine).Show();
        settings.Margin = new Thickness(0);
        actions.Children.Add(where); actions.Children.Add(refresh); actions.Children.Add(settings);
        DockPanel.SetDock(actions, Dock.Right);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(actions, true);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_chips, true);
        header.Children.Add(titleBox);
        header.Children.Add(actions);
        header.Children.Add(_chips);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Hero: what am I doing right now
        var hero = new StackPanel { Children = { Ui.Label("Right now"), _now, _inProgress, _next } };
        var heroCard = Ui.Card(hero, 22);
        heroCard.Background = Ui.B("CardRaised");
        Grid.SetRow(heroCard, 1);
        root.Children.Add(heroCard);

        // Agenda + attention
        var cols = new Grid { Margin = new Thickness(0, 14, 0, 14) };
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        var left = Ui.Card(Scroll("Agenda", _agenda));
        var right = Ui.Card(Scroll("Needs attention", _tasks));
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
        cols.Children.Add(left); cols.Children.Add(right);
        Grid.SetRow(cols, 2);
        root.Children.Add(cols);

        // Plan
        _planBtn.Margin = new Thickness(0);
        _planBtn.Click += async (_, _) =>
        {
            _planBtn.IsEnabled = false; _planOverride = "Planning…"; RenderPlan();
            try { _planOverride = await _engine.PlanDayAsync(); }
            catch (Exception ex) { _planOverride = "Could not plan: " + ex.Message; }
            _planBtn.IsEnabled = true; RenderPlan(); RenderChips();
        };
        var planHead = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var planLabel = Ui.Label("Plan"); planLabel.Margin = new Thickness(0); planLabel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_planBtn, Dock.Right);
        planHead.Children.Add(_planBtn);
        planHead.Children.Add(planLabel);
        var planBody = new ScrollViewer { MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _plan };
        var planCard = Ui.Card(new StackPanel { Children = { planHead, planBody, _planWhen } });
        Grid.SetRow(planCard, 3);
        root.Children.Add(planCard);

        Grid.SetRow(_problems, 4);
        root.Children.Add(_problems);
        Content = new Grid { Children = { root, Ui.CaptionButtons(this) } };
        StateChanged += (_, _) => root.Margin = WindowState == WindowState.Maximized
            ? new Thickness(34, 8, 34, 28) : new Thickness(26, 0, 26, 20); // WindowChrome overhangs 8px when maximised

        _engine.Changed += () => Dispatcher.BeginInvoke(Render);
        _clock.Tick += (_, _) => RenderNow();
        _clock.Start();
        Closing += (_, e) => { if (App.Exiting) { _clock.Stop(); return; } e.Cancel = true; Hide(); }; // lives in the tray
        Render();
    }

    static UIElement Scroll(string label, Panel body)
    {
        var dock = new DockPanel();
        var l = Ui.Label(label);
        DockPanel.SetDock(l, Dock.Top);
        dock.Children.Add(l);
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return dock;
    }

    // ------------------------------------------------------------------ rendering

    void RenderNow()
    {
        var st = _engine.Store.State;
        var now = DateTimeOffset.Now;
        _date.Text = now.LocalDateTime.ToString("dddd, d MMMM") + (st.EventsRefreshed is { } r ? $"  ·  synced {Fmt.Time(r.LocalDateTime)}" : "  ·  not synced yet");

        var hasTask = st.CurrentTask.Length > 0;
        _now.Text = hasTask ? st.CurrentTask : "Nothing picked";
        _now.Foreground = hasTask ? Ui.B("Fg") : Ui.B("Dim");

        var live = st.CachedEvents.Where(e => !e.Cancelled && !e.IsAllDay && e.Start <= now && e.End > now).OrderBy(e => e.End).FirstOrDefault();
        _inProgress.Text = live is null
            ? (hasTask ? $"since {Fmt.Time(st.CurrentTaskSince?.LocalDateTime)}" : $"Press {_engine.Settings.Hotkey} and choose one thing.")
            : $"In progress: {live.Title} · ends {Fmt.Time(live.End.LocalDateTime)}";

        var next = st.CachedEvents.Where(e => !e.Cancelled && !e.IsAllDay && e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
        if (next is null)
        {
            _next.Text = "No more meetings today.";
            _next.Foreground = Ui.B("Dim");
        }
        else
        {
            var mins = (next.Start - now).TotalMinutes;
            _next.Text = $"Next: {next.Title} · {AlertScheduler.Describe(next, now)} ({Fmt.Time(next.Start.LocalDateTime)})";
            _next.Foreground = Ui.B(mins <= 5 ? "Alarm" : mins <= 15 ? "Warn" : "Accent");
        }
    }

    void Render()
    {
        RenderNow();
        RenderChips();
        RenderAgenda();
        RenderTasks();
        RenderPlan();
        RenderProblems();
    }

    void RenderChips()
    {
        _chips.Children.Clear();
        foreach (var p in _engine.Providers)
        {
            var name = char.ToUpper(p[0]) + p[1..];
            var ok = _engine.IsConnected(p);
            var chip = Ui.Chip(name, ok ? "ok" : "warn", ok ? "●" : "○");
            chip.ToolTip = ok ? "Connected" : $"Not connected. Click to connect {name}.";
            if (!ok)
            {
                chip.Cursor = Cursors.Hand;
                chip.MouseLeftButtonUp += async (_, _) => await ConnectAsync(p);
            }
            _chips.Children.Add(chip);
        }
        if (!_engine.Providers.Any())
        {
            var c = Ui.Chip("No accounts connected", "warn", "○"); c.Cursor = Cursors.Hand;
            c.MouseLeftButtonUp += (_, _) => new SettingsWindow(_engine).Show();
            _chips.Children.Add(c);
        }
        if (_engine.Claude is null)
        {
            var off = Ui.Chip("AI off", "neutral", "✦");
            off.Cursor = Cursors.Hand;
            off.ToolTip = _engine.CliMissing ? "Claude app not found on this PC. Click for Settings." : "Click to turn on AI in Settings.";
            off.MouseLeftButtonUp += (_, _) => new SettingsWindow(_engine).Show();
            _chips.Children.Add(off);
        }
        else if (_engine.AiBusy) _chips.Children.Add(Ui.Chip("Claude is working…", "accent", "✦"));
        else if (_engine.UsingApi)
            _chips.Children.Add(Ui.Chip($"${_engine.Budget.SpentToday:0.00} today", _engine.Budget.CutOff ? "alarm" : _engine.Budget.Warn ? "warn" : "accent", "✦"));
        else
        {
            var c = Ui.Chip("Claude", "accent", "✦");
            c.ToolTip = "AI runs silently through your signed-in Claude account.";
            _chips.Children.Add(c);
        }
    }

    async Task ConnectAsync(string provider)
    {
        try { await _engine.ConnectAsync(provider, CancellationToken.None); await _engine.RefreshAllAsync(true); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Anchor"); }
        Render();
    }

    void RenderAgenda()
    {
        _agenda.Children.Clear();
        var now = DateTimeOffset.Now;
        var events = _engine.Store.State.CachedEvents.Where(e => !e.Cancelled && e.End > now)
            .OrderBy(e => e.Start.LocalDateTime.Date).ThenBy(e => e.IsAllDay ? 0 : 1).ThenBy(e => e.Start).ToList();
        if (events.Count == 0)
        {
            _agenda.Children.Add(Empty("Nothing scheduled", "Connect a calendar, or enjoy the space."));
            return;
        }

        DateTime? day = null;
        foreach (var e in events)
        {
            if (day != e.Start.LocalDateTime.Date)
            {
                day = e.Start.LocalDateTime.Date;
                _agenda.Children.Add(new TextBlock
                {
                    Text = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(1) ? "Tomorrow" : day.Value.ToString("dddd d MMM"),
                    FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Accent"), Margin = new Thickness(0, _agenda.Children.Count == 0 ? 0 : 16, 0, 6),
                });
            }
            var live = e.Start <= now && !e.IsAllDay;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var time = new TextBlock { Text = e.IsAllDay ? "all day" : Fmt.Time(e.Start), Foreground = Ui.B("Dim"), VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = e.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            row.Children.Add(time);
            Grid.SetColumn(title, 1); row.Children.Add(title);
            if (live) { var c = Ui.Chip("NOW", "accent"); c.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(c, 2); row.Children.Add(c); }
            else if (!e.IsAllDay && (e.Start - now).TotalMinutes <= 30)
            {
                var c = Ui.Chip($"in {Math.Ceiling((e.Start - now).TotalMinutes):0}m", (e.Start - now).TotalMinutes <= 5 ? "alarm" : "warn");
                c.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(c, 2); row.Children.Add(c);
            }
            row.ToolTip = $"{Fmt.Time(e.Start.LocalDateTime)}–{Fmt.Time(e.End.LocalDateTime)} · {e.Source}" + (e.Location is { Length: > 0 } l ? $" · {l}" : "");
            _agenda.Children.Add(row);
        }
    }

    void RenderTasks()
    {
        _tasks.Children.Clear();
        var st = _engine.Store.State;
        var open = st.Tasks.Where(t => t.Status is SuggestionStatus.Pending or SuggestionStatus.Accepted)
                           .OrderByDescending(t => t.Status == SuggestionStatus.Accepted).ThenByDescending(t => t.Confidence).ToList();
        if (open.Count == 0)
        {
            _tasks.Children.Add(Empty("You're clear", "Anything that needs a reply or action shows up here after each mail and chat refresh."));
            return;
        }
        foreach (var t in open) _tasks.Children.Add(TaskRow(t));
    }

    void RenderPlan()
    {
        var st = _engine.Store.State;
        // Newest of: live API result this session, API plan, or a plan.md dropped by Claude desktop.
        var fromClaude = st.BridgePlan is { Length: > 0 } && (st.BridgePlanAt ?? DateTimeOffset.MinValue) >= (st.LastPlanAt ?? DateTimeOffset.MinValue);
        var text = _planOverride.Length > 0 ? _planOverride : fromClaude ? st.BridgePlan! : st.LastPlan ?? "";
        if (text.Length > 0)
        {
            _plan.Text = text; _plan.Foreground = Ui.B("Fg");
            _planWhen.Text = _planOverride.Length > 0 ? "" : fromClaude ? $"From Claude desktop · {Fmt.Time(st.BridgePlanAt!.Value)}" : st.LastPlanAt is { } t ? $"{(_engine.UsingApi ? "Anthropic API" : "Claude")} · {Fmt.Time(t)}" : "";
            return;
        }
        _planWhen.Text = "";
        _plan.Foreground = Ui.B("Dim");
        _plan.Text = _engine.Claude is null
            ? "Turn on AI in Settings (use your Claude login, or add an API key) to get a plan built around your meetings."
            : "Press “Plan my day” for a realistic plan around your meetings and open follow-ups.";
    }

    void RenderProblems()
    {
        _problems.Children.Clear();
        foreach (var kv in _engine.SourceErrors.Take(4))
        {
            var notConnected = kv.Value.EndsWith("is not connected.", StringComparison.OrdinalIgnoreCase);
            var chip = Ui.Chip(notConnected ? $"{kv.Key}: not connected yet" : $"{kv.Key}: {Short(kv.Value)}", notConnected ? "neutral" : "warn", notConnected ? "○" : "⚠");
            chip.ToolTip = kv.Value;
            _problems.Children.Add(chip);
        }
        _problems.Visibility = _problems.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static string Short(string s) => s.Length <= 80 ? s : s[..80] + "…";

    UIElement TaskRow(SuggestedTask t)
    {
        var row = new StackPanel();
        row.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        var meta = $"{t.Source} · {Clean(t.From)}{(t.Due is { } d ? $" · due {Fmt.DayTime(d.LocalDateTime)}" : "")}";
        row.Children.Add(new TextBlock { Text = meta, FontSize = 12, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-9, 8, 0, -4) };
        void Add(string text, Action act, bool accent = false)
        {
            var b = Ui.Ghost(text);
            if (accent) b.Foreground = Ui.B("Accent");
            b.Click += (_, _) => act();
            btns.Children.Add(b);
        }
        if (Bridge.SafeLink(t.Link) is { } link) Add("Open", () => Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }), accent: true);
        Add("Doing now", () => { _engine.SetCurrentTask(t.Title); Render(); });
        if (t.Status == SuggestionStatus.Pending) Add("Keep", () => { _engine.SetStatus(t.Id, SuggestionStatus.Accepted); Render(); });
        Add("Done", () => { _engine.SetStatus(t.Id, SuggestionStatus.Done); Render(); });
        Add("Not a task", () => { _engine.SetStatus(t.Id, SuggestionStatus.Dismissed); Render(); });
        row.Children.Add(btns);

        var card = new Border
        {
            Background = Ui.B("CardRaised"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 10),
            Margin = new Thickness(0, 0, 0, 8), Child = row,
        };
        if (t.Confidence < 0.6) card.ToolTip = "Flagged by keywords, not AI. Confidence is low.";
        return card;
    }

    static string Clean(string from)
    {
        var i = from.IndexOf('<');
        return (i > 0 ? from[..i] : from).Trim().Trim('"');
    }

    static UIElement Empty(string title, string body) => new StackPanel
    {
        Margin = new Thickness(0, 24, 0, 0),
        Children =
        {
            new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center },
            new TextBlock { Text = body, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 300 },
        },
    };
}
