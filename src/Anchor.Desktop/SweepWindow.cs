using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Anchor.Core;

namespace Anchor.Desktop;

/// <summary>
/// The deep inbox sweep: every loose end Claude found in the mailbox, ranked. Each row can be opened in Outlook,
/// put on today's list, marked handled, or queued to move to Deleted Items (recoverable, done by Claude on its next run).
/// </summary>
public sealed class SweepWindow : Window
{
    readonly AssistantEngine _engine;
    readonly StackPanel _list = new();
    readonly WrapPanel _summary = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly WrapPanel _filters = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock _footer = new() { FontSize = 12, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 12, 0, 0) };
    Button? _run, _full;
    string _category = "All";
    string _tab = "todo";      // todo | queue | cleared
    int _maxPriority = 5;

    public SweepWindow(AssistantEngine engine)
    {
        _engine = engine;
        Title = "Anchor · Inbox sweep";
        Width = 940; Height = 760; MinWidth = 700; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.Style(this);
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 60, ResizeBorderThickness = new Thickness(8), GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0),
        });

        var root = new Grid { Margin = new Thickness(26, 0, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Margin = new Thickness(0, 4, 0, 14) };
        var titleRow = new DockPanel();
        async void Go(bool quick) { var m = await _engine.RunSweepAsync(quick); if (m.StartsWith("Sweep failed")) MessageBox.Show(this, m, "Inbox sweep"); }
        var run = Ui.Primary("Quick sweep"); run.Margin = new Thickness(0, 0, 130, 0); run.VerticalAlignment = VerticalAlignment.Center;
        run.ToolTip = "Only mail since the last sweep (usually 1 to 3 minutes). Earlier findings are kept.";
        run.Click += (_, _) => Go(true);
        var full = Ui.Secondary("Full sweep"); full.Margin = new Thickness(0, 0, 10, 0); full.VerticalAlignment = VerticalAlignment.Center;
        full.ToolTip = "Re-read the last 45 days of mail and rebuild the list (usually 3 to 8 minutes; uses more of your Claude usage)";
        full.Click += (_, _) => Go(false);
        _run = run; _full = full;
        var runs = new StackPanel { Orientation = Orientation.Horizontal, Children = { full, run } };
        runs.Margin = new Thickness(0, 0, 130, 0); run.Margin = new Thickness(0);
        DockPanel.SetDock(runs, Dock.Right);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(runs, true);
        titleRow.Children.Add(runs);
        titleRow.Children.Add(new TextBlock { Text = "Inbox sweep", FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"] });
        head.Children.Add(titleRow);
        head.Children.Add(_summary);
        head.Children.Add(_filters);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_filters, true);   // the title row stays draggable
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Grid.SetRow(_footer, 2);
        _footer.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_footer);

        Content = new Grid { Children = { root, Ui.CaptionButtons(this) } };
        StateChanged += (_, _) => root.Margin = WindowState == WindowState.Maximized ? new Thickness(34, 8, 34, 28) : new Thickness(26, 0, 26, 20);

        void OnChanged() => Dispatcher.BeginInvoke(Render);
        _engine.Changed += OnChanged;
        Closed += (_, _) => _engine.Changed -= OnChanged;
        Render();
    }

    static string PriorityColor(int p) => p switch { 1 => "Alarm", 2 => "Warn", 3 => "Accent", _ => "Dim" };
    static string PriorityName(int p) => p switch { 1 => "Urgent", 2 => "High", 3 => "Medium", 4 => "Low", _ => "Whenever" };

    static string Age(DateTimeOffset? at)
    {
        if (at is null) return "";
        var d = (DateTimeOffset.Now - at.Value).TotalDays;
        return d < 1 ? "today" : d < 2 ? "yesterday" : d < 60 ? $"{(int)d} days ago" : $"{(int)(d / 30)} months ago";
    }

    void Render()
    {
        var sweep = _engine.Sweep;
        var open = _engine.OpenSweepItems();
        if (_run is not null && _full is not null)
        {
            _run.IsEnabled = _full.IsEnabled = !_engine.SweepRunning;
            _run.Content = _engine.SweepRunning ? "Sweeping…" : sweep is null ? "Run sweep" : "Quick sweep";
            _full.Visibility = sweep is null ? Visibility.Collapsed : Visibility.Visible;     // the first sweep is always a full one
        }
        _summary.Children.Clear(); _filters.Children.Clear(); _list.Children.Clear();

        if (sweep is null)
        {
            _footer.Text = "";
            _list.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 60, 0, 0), MaxWidth = 520,
                Children =
                {
                    new TextBlock { Text = "No sweep yet", FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = _engine.SweepStatus ?? "Press Run sweep. Claude reads the last 45 days (a few minutes, once) of your mail in the background (no window opens) and this list fills in when it finishes. You only need to do it now and then.", Foreground = Ui.B("Dim"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) },
                },
            });
            return;
        }

        if (_engine.SweepStatus is { } st) _summary.Children.Add(Ui.Chip(st, "accent"));
        _summary.Children.Add(Ui.Chip($"{sweep.Scanned} messages read", "neutral"));
        _summary.Children.Add(Ui.Chip($"{open.Count} still open", open.Count > 0 ? "warn" : "ok"));
        if (open.Count(i => i.Priority <= 2) is > 0 and var hot) _summary.Children.Add(Ui.Chip($"{hot} urgent or high", "alarm"));
        _summary.Children.Add(Ui.Chip("swept " + Age(sweep.GeneratedAt), "neutral"));
        if (sweep.Scope.Length > 0) _summary.Children.Add(Ui.Chip(sweep.Scope, "neutral"));

        var aside = _engine.SetAsideSweepItems();
        void View(string text, bool on, Action act)
        {
            var b = Ui.Ghost(text);
            b.FontWeight = FontWeights.SemiBold;
            if (on) { b.Foreground = Ui.B("Accent"); b.Background = Ui.B("AccentSoft"); }
            b.Click += (_, _) => { act(); Render(); };
            _filters.Children.Add(b);
        }
        var queued = aside.Where(x => x.Queued).ToList();
        var cleared = aside.Where(x => !x.Queued).ToList();
        View($"To do ({open.Count})", _tab == "todo", () => _tab = "todo");
        View($"Sync queue ({queued.Count})", _tab == "queue", () => _tab = "queue");
        View($"Cleared ({cleared.Count})", _tab == "cleared", () => _tab = "cleared");
        if (_tab == "queue") { RenderQueue(queued); return; }
        if (_tab == "cleared") { RenderAside(cleared); return; }
        _filters.Children.Add(new Border { Width = 1, Background = Ui.B("Line"), Margin = new Thickness(6, 6, 14, 6) });

        var cats = open.Select(i => i.Category).Distinct().OrderBy(c => c).ToList();
        if (_category != "All" && !cats.Contains(_category)) _category = "All";
        void Filter(string text, bool on, Action act)
        {
            var b = Ui.Ghost(text);
            if (on) { b.Foreground = Ui.B("Accent"); b.Background = Ui.B("AccentSoft"); }
            b.Click += (_, _) => { act(); Render(); };
            _filters.Children.Add(b);
        }
        Filter($"All ({open.Count})", _category == "All", () => _category = "All");
        foreach (var c in cats) Filter($"{c} ({open.Count(i => i.Category == c)})", _category == c, () => _category = c);
        Filter(_maxPriority == 5 ? "Show: everything" : "Show: urgent + high", false, () => _maxPriority = _maxPriority == 5 ? 2 : 5);

        var shown = open.Where(i => (_category == "All" || i.Category == _category) && i.Priority <= _maxPriority).ToList();
        if (shown.Count == 0)
            _list.Children.Add(new TextBlock { Text = open.Count == 0 ? "Everything from this sweep is handled." : "Nothing matches this filter.", Foreground = Ui.B("Dim"), Margin = new Thickness(0, 40, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });

        int? lastPri = null;
        foreach (var i in shown)
        {
            if (lastPri != i.Priority)
            {
                var l = Ui.Label($"{PriorityName(i.Priority)} · {shown.Count(x => x.Priority == i.Priority)}");
                l.Margin = new Thickness(2, lastPri is null ? 0 : 14, 0, 8);
                l.Foreground = Ui.B(PriorityColor(i.Priority));
                _list.Children.Add(l);
                lastPri = i.Priority;
            }
            _list.Children.Add(Row(i));
        }

        var q = _engine.Store.State.TrashQueue.Count;
        _footer.Text = (q > 0 ? $"{q} message{(q == 1 ? "" : "s")} will move to Deleted Items on Claude's next sync. " : "")
            + "Trash only ever moves mail to Deleted Items (recoverable); nothing is permanently deleted.";
    }

    void RenderQueue(IReadOnlyList<(SweepItem Item, string How, bool Queued)> queued)
    {
        var next = SyncSchedule.Next(DateTime.Now);
        var when = next.Date == DateTime.Today ? Fmt.Time(next) : Fmt.DayTime(next);
        var info = new TextBlock
        {
            Text = queued.Count == 0
                ? "Nothing is waiting. Messages you Trash are listed here until Claude's next sync moves them to Deleted Items in Outlook."
                : $"These {queued.Count} message{(queued.Count == 1 ? "" : "s")} will be moved to Deleted Items in Outlook at Claude's next sync, around {when}. Put back cancels one. To go sooner, open Claude desktop, then Scheduled, then \"Anchor: sync Outlook and Teams\" and choose Run now.",
            FontSize = 12.5, Foreground = Ui.B("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 12),
        };
        _list.Children.Add(info);
        foreach (var x in queued) _list.Children.Add(AsideRow(x.Item));
        _footer.Text = "Moving mail needs an approved Claude run, so Anchor can't do it on its own. Nothing is permanently deleted.";
    }

    UIElement AsideRow(SweepItem item)
    {
        var back = Ui.Secondary("Put back"); back.Margin = new Thickness(12, 0, 0, 0); back.VerticalAlignment = VerticalAlignment.Center;
        back.Click += (_, _) => _engine.RestoreSweepItem(item.Id);
        var open = Ui.Ghost("Open"); open.Foreground = Ui.B("Accent"); open.VerticalAlignment = VerticalAlignment.Center;
        var link = item.Link;
        open.Click += (_, _) => { if (link is not null) Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); };
        open.Visibility = link is null ? Visibility.Collapsed : Visibility.Visible;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        text.Children.Add(new TextBlock { Text = $"{(item.From.Length > 0 ? item.From : "Unknown sender")} · {item.Category} · {PriorityName(item.Priority)}", FontSize = 12, Foreground = Ui.B("Dim"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Children = { open, back } };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(tools, 1);
        grid.Children.Add(text); grid.Children.Add(tools);
        return new Border { Background = Ui.B("Card"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 10, 9), Margin = new Thickness(0, 0, 0, 6), Child = grid };
    }

    void RenderAside(IReadOnlyList<(SweepItem Item, string How, bool Queued)> aside)
    {
        if (aside.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "Nothing cleared yet. Items you mark Handled, put on today's list or have already been moved to Deleted Items show up here so you can put them back in the list.", Foreground = Ui.B("Dim"), TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 40, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            _footer.Text = "";
            return;
        }
        (string Key, string Title, string Hint, string Brush)[] groups =
        [
            ("sent", "Sent to Deleted Items", "Already moved in Outlook. Putting it back here only shows it in this list again; restore the mail itself from Deleted Items.", "Dim"),
            ("today", "On today's list", "Put back removes it from today's list.", "Accent"),
            ("handled", "Handled", "Hidden from your list.", "Dim"),
        ];
        string Key((SweepItem Item, string How, bool Queued) x) => x.How == "trash" ? "sent" : x.How == "today" ? "today" : "handled";
        foreach (var g in groups)
        {
            var rows = aside.Where(x => Key(x) == g.Key).ToList();
            if (rows.Count == 0) continue;
            var l = Ui.Label($"{g.Title} · {rows.Count}");
            l.Margin = new Thickness(2, 6, 0, 2); l.Foreground = Ui.B(g.Brush);
            _list.Children.Add(l);
            _list.Children.Add(new TextBlock { Text = g.Hint, FontSize = 12, Foreground = Ui.B("Dim"), Margin = new Thickness(2, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
            foreach (var x in rows)
            {
                var item = x.Item;
                var back = Ui.Secondary("Put back"); back.Margin = new Thickness(12, 0, 0, 0); back.VerticalAlignment = VerticalAlignment.Center;
                back.Click += (_, _) => _engine.RestoreSweepItem(item.Id);
                var open = Ui.Ghost("Open"); open.Foreground = Ui.B("Accent"); open.VerticalAlignment = VerticalAlignment.Center;
                var link = item.Link;
                open.Click += (_, _) => { if (link is not null) Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); };
                open.Visibility = link is null ? Visibility.Collapsed : Visibility.Visible;
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                text.Children.Add(new TextBlock { Text = $"{(item.From.Length > 0 ? item.From : "Unknown sender")} · {item.Category} · {PriorityName(item.Priority)}", FontSize = 12, Foreground = Ui.B("Dim"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Children = { open, back } };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(tools, 1);
                grid.Children.Add(text); grid.Children.Add(tools);
                _list.Children.Add(new Border { Background = Ui.B("Card"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 10, 9), Margin = new Thickness(0, 0, 0, 6), Child = grid });
            }
        }
        _footer.Text = "";
    }

    UIElement Row(SweepItem i)
    {
        var col = Ui.B(PriorityColor(i.Priority));
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = i.Title, FontWeight = FontWeights.SemiBold, FontSize = 14.5 });
        var meta = $"{(i.From.Length > 0 ? i.From : "Unknown sender")}" + (i.Received is not null ? $" · {Age(i.Received)}" : "") + $" · {i.Category}" + (i.Due is { } d ? $" · due {Fmt.DayTime(d.LocalDateTime)}" : "");
        body.Children.Add(new TextBlock { Text = meta, FontSize = 12, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        if (i.Why.Length > 0) body.Children.Add(new TextBlock { Text = i.Why, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.9 });
        if (i.Action.Length > 0) body.Children.Add(new TextBlock { Text = "→ " + i.Action, FontSize = 12.5, Foreground = Ui.B("Accent"), Margin = new Thickness(0, 3, 0, 0) });

        var btns = new WrapPanel { Margin = new Thickness(-9, 8, 0, -4) };
        void Add(string text, string tip, Action act, string? brush = null)
        {
            var b = Ui.Ghost(text);
            b.ToolTip = tip;
            if (brush is not null) b.Foreground = Ui.B(brush);
            b.Click += (_, _) => act();
            btns.Children.Add(b);
        }
        if (i.Link is { } link) Add("Open", "Open this message in Outlook", () => Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }), "Accent");
        Add("+ Today", "Add to today's to-do list", () => _engine.AddTodo(i.Title, i.Link, "Inbox sweep", i.Id, "Work", i.Priority));
        Add("Handled", "Remove from this list (I dealt with it, or it is not a loose end)", () => _engine.HandleSweepItem(i.Id));
        Add("Trash", "Move the message to Deleted Items on Claude's next sync (recoverable)", () => _engine.QueueTrash(i.Id), "Alarm");
        body.Children.Add(btns);

        var bar = new Border { Width = 4, Background = col, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 14, 0) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(body, 1);
        g.Children.Add(bar); g.Children.Add(body);
        return new Border { Background = Ui.B("Card"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 10), Margin = new Thickness(0, 0, 0, 8), Child = g };
    }
}
