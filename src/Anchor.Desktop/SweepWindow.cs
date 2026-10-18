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
    Button? _run;
    string _category = "All";
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
        var run = Ui.Primary("Run sweep"); run.Margin = new Thickness(0, 0, 130, 0); run.VerticalAlignment = VerticalAlignment.Center;
        run.ToolTip = "Read the last 45 days of mail with Claude (about 3 to 8 minutes; uses some of your Claude usage) and rank every loose end";
        run.Click += async (_, _) => { var m = await _engine.RunSweepAsync(); if (m.StartsWith("Sweep failed")) MessageBox.Show(this, m, "Inbox sweep"); };
        _run = run;
        DockPanel.SetDock(run, Dock.Right);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(run, true);
        titleRow.Children.Add(run);
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
        if (_run is not null) { _run.IsEnabled = !_engine.SweepRunning; _run.Content = _engine.SweepRunning ? "Sweeping…" : sweep is null ? "Run sweep" : "Run again"; }
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
                    new TextBlock { Text = _engine.SweepStatus ?? "Press Run sweep. Claude reads the last 45 days of your mail in the background (no window opens) and this list fills in when it finishes. You only need to do it now and then.", Foreground = Ui.B("Dim"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) },
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
        Add("+ Today", "Add to today's to-do list", () => _engine.AddTodo(i.Title, i.Link, "Inbox sweep", i.Id));
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
