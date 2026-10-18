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
    readonly TextBlock _inProgress = new() { FontSize = 13, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock _next = new() { FontSize = 15, Margin = new Thickness(0, 4, 0, 0) };
    readonly Grid _agendaHost = new();
    readonly DockPanel _heroTop = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock _since = new() { FontSize = 13, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel _crumb = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    readonly WrapPanel _heroButtons = new() { Margin = new Thickness(0, 14, 0, 0) };
    Grid? _cols;
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
        Width = 1100; Height = 900; MinWidth = 820; MinHeight = 640;
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
        var logo = new Image { Source = Ui.Logo, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var titleBox = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0), Children = { logo, titleText } };
        DockPanel.SetDock(titleBox, Dock.Left);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var where = Ui.Primary("Where was I?");
        where.ToolTip = $"Global hotkey: {_engine.Settings.Hotkey}";
        where.Click += (_, _) => new RecoveryWindow(_engine, false).Show();
        var refresh = Ui.IconButton("\uE72C", "Refresh calendars and mail now");
        refresh.Click += (_, _) => _ = _engine.RefreshAllAsync(forceMessages: true);
        var sweep = Ui.IconButton("\uE715", "Inbox sweep: every loose end in your mail, ranked");
        sweep.Click += (_, _) => new SweepWindow(_engine).Show();
        var settings = Ui.IconButton("\uE713", "Settings");
        settings.Click += (_, _) => new SettingsWindow(_engine).Show();
        settings.Margin = new Thickness(0);
        actions.Children.Add(where); actions.Children.Add(sweep); actions.Children.Add(refresh); actions.Children.Add(settings);
        DockPanel.SetDock(actions, Dock.Right);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(actions, true);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_chips, true);
        header.Children.Add(titleBox);
        header.Children.Add(actions);
        header.Children.Add(_chips);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Hero: what am I doing right now
        var hero = new StackPanel { Children = { _heroTop, _now, _since, _crumb, _heroButtons, _inProgress, _next } };
        var heroCard = Ui.Card(hero, 22);
        heroCard.Background = Ui.B("CardRaised");
        Grid.SetRow(heroCard, 1);
        root.Children.Add(heroCard);

        // Agenda + attention
        var cols = new Grid { Margin = new Thickness(0, 14, 0, 14) };
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        _cols = cols;
        var agendaHead = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var agendaLabel = Ui.Label("Agenda"); agendaLabel.Margin = new Thickness(0); agendaLabel.VerticalAlignment = VerticalAlignment.Center;
        var switcher = ViewSwitcher();
        DockPanel.SetDock(switcher, Dock.Right);
        agendaHead.Children.Add(switcher);
        agendaHead.Children.Add(agendaLabel);
        var agendaDock = new DockPanel();
        DockPanel.SetDock(agendaHead, Dock.Top);
        agendaDock.Children.Add(agendaHead);
        agendaDock.Children.Add(_agendaHost);
        var left = Ui.Card(agendaDock);
        var right = Ui.Card(Scroll("To do", _tasks));
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
        var planBody = new ScrollViewer { MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _plan };
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
        var stale = hasTask && FocusTracker.IsStale(st.CurrentTaskSince, now);
        _since.Text = !hasTask ? $"Press {_engine.Settings.Hotkey} and choose one thing."
            : st.CurrentTaskSince is { } since ? (stale ? $"since {Fmt.DayTime(since.LocalDateTime)}" : $"since {Fmt.Time(since)}") : "";

        var events = _engine.ActiveEvents;
        var live = events.Where(e => !e.Cancelled && !e.IsAllDay && e.Start <= now && e.End > now).OrderBy(e => e.End).FirstOrDefault();
        _inProgress.Text = live is null ? "" : $"In progress: {live.Title} · ends {Fmt.Time(live.End.LocalDateTime)}";
        _inProgress.Visibility = live is null ? Visibility.Collapsed : Visibility.Visible;

        var next = events.Where(e => !e.Cancelled && !e.IsAllDay && e.Start > now).OrderBy(e => e.Start).FirstOrDefault();
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

    /// <summary>The "Right now" card: task, breadcrumb and the Done / Switch / Clear actions.</summary>
    void RenderHero()
    {
        var st = _engine.Store.State;
        var now = DateTimeOffset.Now;
        var hasTask = st.CurrentTask.Length > 0;
        var stale = hasTask && FocusTracker.IsStale(st.CurrentTaskSince, now);

        _heroTop.Children.Clear();
        var label = Ui.Label("Right now"); label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center;
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        if (stale) chips.Children.Add(Ui.Chip("From earlier", "warn"));
        var done = _engine.Focus.DoneToday(now);
        if (done.Count > 0)
        {
            var c = Ui.Chip($"{done.Count} done today", "ok", "✓");
            c.ToolTip = string.Join(Environment.NewLine, done.Select(d => $"{Fmt.Time(d.At)}  {d.Title}"));
            c.Margin = new Thickness(0);
            chips.Children.Add(c);
        }
        DockPanel.SetDock(chips, Dock.Right);
        _heroTop.Children.Add(chips);
        _heroTop.Children.Add(label);

        _now.Text = hasTask ? st.CurrentTask : "Nothing picked";
        _now.Foreground = hasTask ? Ui.B("Fg") : Ui.B("Dim");

        _crumb.Children.Clear();
        if (st.Checkpoint.Length > 0)
        {
            _crumb.Children.Add(new TextBlock { Text = "↳  " + st.Checkpoint, FontSize = 13, FontStyle = FontStyles.Italic, Foreground = Ui.B("Dim"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 700 });
            var x = Ui.Ghost("✕"); x.ToolTip = "Clear breadcrumb"; x.Margin = new Thickness(6, 0, 0, 0);
            x.Click += (_, _) => _engine.ClearNote();
            _crumb.Children.Add(x);
        }

        _heroButtons.Children.Clear();
        void Add(Button b) { _heroButtons.Children.Add(b); }
        Button Act(string text, Action act, bool primary = false, string? tip = null)
        {
            var b = primary ? Ui.Primary(text, 16, 7) : Ui.Secondary(text);
            if (!primary) b.Padding = new Thickness(14, 7, 14, 7);
            if (tip is not null) b.ToolTip = tip;
            b.Click += (_, _) => act();
            return b;
        }
        if (!hasTask) Add(Act("Pick one thing", () => new RecoveryWindow(_engine, false).Show(), primary: true));
        else
        {
            if (stale) Add(Act("Still on it", () => _engine.KeepTask(), primary: true, tip: "Same task, starting fresh from now"));
            Add(Act("Done ✓", () => _engine.FinishTask(), primary: !stale, tip: "Finish it and clear the card"));
            Add(Act("Switch", () => new RecoveryWindow(_engine, false).Show(), tip: "Change task or add a breadcrumb"));
            var clear = Ui.Ghost("Clear"); clear.ToolTip = "Drop it without counting it as done"; clear.Margin = new Thickness(0, 0, 0, 0);
            clear.Click += (_, _) => _engine.ClearTask();
            Add(clear);
        }
    }

    void Render()
    {
        RenderNow();
        RenderHero();
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
            var cli = _engine.CliBudget;
            var u = _engine.Store.State.AccountUsage;
            var fresh = u is not null && DateTimeOffset.Now - u.CheckedAt < TimeSpan.FromHours(26);
            var tip = new System.Text.StringBuilder();
            if (fresh) tip.AppendLine($"Your Claude account this month: ${u!.Spent:0.00} of ${u.Limit:0.00} ({u.PercentUsed:0}%), checked {Fmt.Time(u.CheckedAt)}");
            else tip.AppendLine("Account-wide usage appears after the Outlook sync task runs.");
            tip.Append($"Anchor's own Claude calls: ${cli.SpentToday:0.00} today · ${cli.SpentMonth:0.00} this month (cap ${_engine.Settings.ClaudeDesktop.MonthlyCapUsd:0})");
            var kind = cli.CutOff ? "alarm" : fresh && u!.PercentUsed >= 90 ? "alarm" : fresh && u!.PercentUsed >= 75 ? "warn" : "accent";
            var c = Ui.Chip(fresh ? $"{u!.PercentUsed:0}% · ${u.Spent:0}/{u.Limit:0}" : $"${cli.SpentToday:0.00} today", kind, "✦");
            c.ToolTip = tip.ToString();
            _chips.Children.Add(c);
        }
    }

    async Task ConnectAsync(string provider)
    {
        try { await _engine.ConnectAsync(provider, CancellationToken.None); await _engine.RefreshAllAsync(true); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Anchor"); }
        Render();
    }

    // ------------------------------------------------------------------ agenda views

    UIElement ViewSwitcher()
    {
        var group = Guid.NewGuid().ToString("N");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, text, tip) in new[] { ("All", "All", "Both calendars together"), ("Personal", "Personal", "Personal / Skylight only"), ("Work", "Work", "Work only"), ("Split", "Side by side", "Personal and Work in two columns") })
        {
            var rb = new RadioButton
            {
                Content = text, GroupName = group, Style = (Style)Application.Current.Resources["Segment"], ToolTip = tip,
                Padding = new Thickness(10, 5, 10, 5), FontSize = 12, IsChecked = _engine.Settings.AgendaView == key,
            };
            rb.Checked += (_, _) =>
            {
                if (_engine.Settings.AgendaView == key) return;
                _engine.Settings.AgendaView = key;
                try { _engine.Settings.Save(); } catch { /* a read-only settings file must not break the view switch */ }
                RenderAgenda();
            };
            panel.Children.Add(rb);
        }
        if (!panel.Children.OfType<RadioButton>().Any(r => r.IsChecked == true)) ((RadioButton)panel.Children[0]).IsChecked = true;
        return new Border { Background = Ui.B("Field"), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), Child = panel };
    }

    static Brush Hex(string hex, string fallback)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return (Brush)new BrushConverter().ConvertFromString(fallback)!; }
    }

    void RenderAgenda()
    {
        _agendaHost.Children.Clear();
        _agendaHost.ColumnDefinitions.Clear();
        var now = DateTimeOffset.Now;
        var all = _engine.VisibleEvents.Where(e => !e.Cancelled && e.End > now).ToList();
        var view = _engine.Settings.AgendaView;
        var split = view == "Split";

        if (_cols is not null)
        {
            _cols.ColumnDefinitions[0].Width = new GridLength(split ? 2.2 : 1, GridUnitType.Star);
            _cols.ColumnDefinitions[2].Width = new GridLength(split ? 1 : 1.25, GridUnitType.Star);
        }

        if (all.Count == 0)
        {
            _agendaHost.Children.Add(Empty("Nothing scheduled", "Connect a calendar, or enjoy the space."));
            return;
        }

        if (split)
        {
            _agendaHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _agendaHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            _agendaHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var i = 0;
            foreach (var g in new[] { CalGroup.Personal, CalGroup.Work })
            {
                var col = new DockPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                head.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = Hex(g == CalGroup.Work ? CalendarPrefs.WorkColor : CalendarPrefs.PersonalColor, "#888"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                head.Children.Add(new TextBlock { Text = g.ToString(), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                DockPanel.SetDock(head, Dock.Top);
                col.Children.Add(head);
                var mine = all.Where(e => _engine.GroupOf(e) == g).ToList();
                col.Children.Add(new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = mine.Count == 0 ? Empty($"No {g} events", "") : Days(mine, now, tagGroup: false, compact: true),
                });
                Grid.SetColumn(col, i); _agendaHost.Children.Add(col); i += 2;
            }
            return;
        }

        var shown = view switch
        {
            "Personal" => all.Where(e => _engine.GroupOf(e) == CalGroup.Personal).ToList(),
            "Work" => all.Where(e => _engine.GroupOf(e) == CalGroup.Work).ToList(),
            _ => all,
        };
        _agendaHost.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = shown.Count == 0 ? Empty($"No {view} events", "Nothing in this calendar for now.") : Days(shown, now, tagGroup: view == "All", compact: false),
        });
    }

    StackPanel Days(IEnumerable<CalendarEvent> events, DateTimeOffset now, bool tagGroup, bool compact)
    {
        var panel = new StackPanel();
        DateTime? day = null;
        foreach (var e in events.OrderBy(e => e.Start.LocalDateTime.Date).ThenBy(e => e.IsAllDay ? 0 : 1).ThenBy(e => e.Start))
        {
            if (day != e.Start.LocalDateTime.Date)
            {
                day = e.Start.LocalDateTime.Date;
                panel.Children.Add(new TextBlock
                {
                    Text = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(1) ? "Tomorrow" : day.Value.ToString("dddd d MMM"),
                    FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Accent"), Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 16, 0, 6),
                });
            }
            panel.Children.Add(AgendaRow(e, now, tagGroup, compact));
        }
        return panel;
    }

    UIElement AgendaRow(CalendarEvent e, DateTimeOffset now, bool tagGroup, bool compact)
    {
        var viewOnly = _engine.ModeOf(e) == CalMode.ViewOnly;       // shown for reference, but Anchor does not act on it
        var live = e.Start <= now && !e.IsAllDay && !viewOnly;
        var work = _engine.GroupOf(e) == CalGroup.Work;
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3), Opacity = viewOnly ? 0.55 : 1 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 74 : 86) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = new Border { CornerRadius = new CornerRadius(2), Background = Hex(_engine.ColorOf(e), work ? CalendarPrefs.WorkColor : CalendarPrefs.PersonalColor), Margin = new Thickness(0, 1, 0, 1) };
        var time = new TextBlock { Text = e.IsAllDay ? "all day" : Fmt.Time(e.Start), Foreground = Ui.B("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.NoWrap };
        var title = new TextBlock
        {
            Text = e.Title, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = compact ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextWrapping = compact ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        row.Children.Add(bar);
        Grid.SetColumn(time, 1); row.Children.Add(time);
        Grid.SetColumn(title, 2); row.Children.Add(title);

        var tail = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (tagGroup && work) tail.Children.Add(new TextBlock { Text = "WORK", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Hex(CalendarPrefs.WorkColor, "#5B9DFF"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (live) { var c = Ui.Chip("NOW", "accent"); c.Margin = new Thickness(8, 0, 0, 0); tail.Children.Add(c); }
        else if (!e.IsAllDay && !viewOnly && (e.Start - now).TotalMinutes <= 30 && e.Start > now)
        {
            var c = Ui.Chip($"in {Math.Ceiling((e.Start - now).TotalMinutes):0}m", (e.Start - now).TotalMinutes <= 5 ? "alarm" : "warn");
            c.Margin = new Thickness(8, 0, 0, 0); tail.Children.Add(c);
        }
        Grid.SetColumn(tail, 3); row.Children.Add(tail);
        row.ToolTip = $"{Fmt.Time(e.Start.LocalDateTime)}–{Fmt.Time(e.End.LocalDateTime)} · {e.Source}" + (e.Location is { Length: > 0 } l ? $" · {l}" : "") + (viewOnly ? " · view only: no alerts or planning" : "");
        return row;
    }

    void RenderTasks()
    {
        _tasks.Children.Clear();
        var st = _engine.Store.State;
        var open = st.Tasks.Where(t => t.Status is SuggestionStatus.Pending or SuggestionStatus.Accepted)
                           .OrderByDescending(t => t.Status == SuggestionStatus.Accepted).ThenByDescending(t => t.Confidence).ToList();
        RenderTodos();
        if (open.Count == 0 && _engine.TodayTodos(DateTimeOffset.Now).Count == 0)
        {
            _tasks.Children.Add(Empty("You're clear", "Anything that needs a reply or action shows up here after each mail and chat refresh."));
            return;
        }
        if (open.Count > 0)
        {
            var l = Ui.Label("Needs attention"); l.Margin = new Thickness(2, 6, 0, 8);
            _tasks.Children.Add(l);
        }
        foreach (var t in open) _tasks.Children.Add(TaskRow(t));
    }

    /// <summary>Today's to-do list: things I chose to do today (from the inbox sweep or typed here).</summary>
    void RenderTodos()
    {
        var now = DateTimeOffset.Now;
        var todos = _engine.TodayTodos(now);
        var head = Ui.Label($"Today's list{(todos.Count > 0 ? $" · {todos.Count(t => t.DoneAt is null)} left" : "")}");
        head.Margin = new Thickness(2, 0, 0, 8);
        _tasks.Children.Add(head);

        foreach (var t in todos)
        {
            var done = t.DoneAt is not null;
            var check = new Button { Content = done ? "\uE73E" : "\uEA3A", Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 10, 0), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, ToolTip = done ? "Mark not done" : "Done", Foreground = done ? Ui.B("Ok") : Ui.B("Dim") };
            check.Click += (_, _) => _engine.SetTodoDone(t.Id, !done);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = t.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextDecorations = done ? TextDecorations.Strikethrough : null, Opacity = done ? 0.5 : 1 });
            if (t.Source.Length > 0 && !done) text.Children.Add(new TextBlock { Text = t.Source, FontSize = 11.5, Foreground = Ui.B("Dim") });
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (Bridge.SafeLink(t.Link) is { } link && !done)
            {
                var o = Ui.Ghost("Open"); o.Foreground = Ui.B("Accent");
                o.Click += (_, _) => Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                tools.Children.Add(o);
            }
            var x = new Button { Content = "\uE711", Width = 28, Height = 28, Padding = new Thickness(0), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, ToolTip = "Remove from today's list", Foreground = Ui.B("Dim"), Margin = new Thickness(0) };
            x.Click += (_, _) => _engine.RemoveTodo(t.Id);
            tools.Children.Add(x);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 1); Grid.SetColumn(tools, 2);
            g.Children.Add(check); g.Children.Add(text); g.Children.Add(tools);
            _tasks.Children.Add(new Border { Background = Ui.B("CardRaised"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), Child = g });
        }

        var add = new TextBox { Height = 36, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 12), ToolTip = "Type something to do today and press Enter" };
        var hint = new TextBlock { Text = "+ Add to today's list", Foreground = Ui.B("Dim"), IsHitTestVisible = false, Margin = new Thickness(13, 9, 0, 0), FontSize = 13 };
        add.TextChanged += (_, _) => hint.Visibility = add.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        add.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter || add.Text.Trim().Length == 0) return;
            e.Handled = true;
            _engine.AddTodo(add.Text, null, "Added by me");
        };
        _tasks.Children.Add(new Grid { Children = { add, hint } });
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

        var btns = new WrapPanel { Margin = new Thickness(-9, 8, 0, -4) };
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
