using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Anchor.Core;
using Anchor.Core.AI;

namespace Anchor.Desktop;

/// <summary>Edits settings.json without hand-editing JSON. Save restarts Anchor so connectors rebuild.</summary>
public sealed class SettingsWindow : Window
{
    readonly AssistantEngine _engine;
    readonly AnchorSettings _s;
    readonly StackPanel _page = new() { MaxWidth = 720 };
    readonly List<Action> _apply = new();
    StackPanel _section = new();

    public SettingsWindow(AssistantEngine engine)
    {
        _engine = engine;
        _s = engine.Settings;
        Title = "Anchor · Settings";
        Width = 800; Height = 860; MinWidth = 640; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.Style(this);

        Section("Alerts & focus", "How loudly Anchor gets your attention.");
        Segmented("Meeting alerts", "Standard is a small corner card. Strong is a big centred card with sound. Maximum repeats until you respond.",
            ["Standard", "Strong", "Maximum"], _s.AlertIntensity, v => _s.AlertIntensity = v);
        Field("Alert before start", "Minutes, comma separated", string.Join(", ", _s.AlertOffsetsMinutes), v => _s.AlertOffsetsMinutes = ParseInts(v, _s.AlertOffsetsMinutes), 160);
        Field("Extra lead for in-person", "Minutes; 0 turns it off", _s.InPersonLeadMinutes.ToString(), v => _s.InPersonLeadMinutes = ParseInt(v, _s.InPersonLeadMinutes), 90);
        Field("Snooze", "Minutes", _s.SnoozeMinutes.ToString(), v => _s.SnoozeMinutes = Math.Max(1, ParseInt(v, _s.SnoozeMinutes)), 90);
        Field("Focus check-in", "Every N minutes during work hours; 0 turns it off", _s.CheckInMinutes.ToString(), v => _s.CheckInMinutes = ParseInt(v, _s.CheckInMinutes), 90);
        Field("Workday starts", "e.g. 8:30 AM or 08:30", _s.WorkdayStart, v => _s.WorkdayStart = v.Trim(), 90);
        Field("Workday ends", "e.g. 5:30 PM or 17:30", _s.WorkdayEnd, v => _s.WorkdayEnd = v.Trim(), 90);
        Field("“Where was I?” hotkey", "e.g. Ctrl+Space, Ctrl+Shift+W, Win+Shift+A", _s.Hotkey, v => _s.Hotkey = v.Trim(), 200);
        Toggle("24-hour clock", "Off shows times like 2:30 PM.", _s.Use24HourClock, v => _s.Use24HourClock = v);
        Toggle("Speak alerts aloud", null, _s.SpeakAlerts, v => _s.SpeakAlerts = v);
        VoiceRows();
        Toggle("Morning plan and end-of-day wrap-up", "A short brief at workday start, and a wrap-up 30 minutes before it ends.", _s.BriefsEnabled, v => _s.BriefsEnabled = v);

        Section("Claude", _engine.HasAnthropicKey ? "A key is saved. Leave the box empty to keep it." : "Add an API key to turn on AI triage and day plans.");
        KeyRow();
        Toggle("Enable AI", null, _s.Ai.Enabled, v => _s.Ai.Enabled = v);
        Toggle("Allow work items to be sent to Claude", "Outlook, Teams and Slack content. Leave off until you have approval to send work data to Anthropic.", _s.Ai.SendWorkContentToClaude, v => _s.Ai.SendWorkContentToClaude = v);
        Field("Monthly budget", "USD", _s.Ai.MonthlyBudgetUsd.ToString(), v => _s.Ai.MonthlyBudgetUsd = ParseDouble(v, _s.Ai.MonthlyBudgetUsd), 90);
        Field("Daily soft cap", "USD; limits background triage only", _s.Ai.DailySoftCapUsd.ToString(), v => _s.Ai.DailySoftCapUsd = ParseDouble(v, _s.Ai.DailySoftCapUsd), 90);
        Field("Hard cut-off", "USD; AI stops entirely at this monthly spend", _s.Ai.CutoffUsd.ToString(), v => _s.Ai.CutoffUsd = ParseDouble(v, _s.Ai.CutoffUsd), 90);

        Section("Claude login (no API key)", "Anchor can use the Claude account already signed in on this PC. It runs silently in the background: no window, nothing to approve.");
        Toggle("Use my Claude login", "Used when no API key is saved. Plans, inbox triage and “Where was I?” run quietly and show up here.", _s.ClaudeDesktop.Enabled, v => _s.ClaudeDesktop.Enabled = v);
        Toggle("Include work items", "Lets Claude see work meetings and Outlook / Teams / Slack items. Off keeps them redacted. Turn on only if your organisation allows it.", _s.ClaudeDesktop.IncludeWorkContext, v => _s.ClaudeDesktop.IncludeWorkContext = v);
        CliRow();
        EndSection();

        Section("Google", "Gmail and every calendar on the account, including the one Skylight syncs from.");
        Toggle("Enable Google", null, _s.Google.Enabled, v => _s.Google.Enabled = v);
        Field("OAuth client ID", "Ends in .apps.googleusercontent.com", _s.Google.ClientId, v => _s.Google.ClientId = v.Trim());
        Secret("OAuth client secret", "Starts with GOCSPX-", _s.Google.ClientSecret, v => _s.Google.ClientSecret = v.Trim());
        Toggle("Calendars", null, _s.Google.Calendar, v => _s.Google.Calendar = v);
        Toggle("Gmail", null, _s.Google.Gmail, v => _s.Google.Gmail = v);

        Section("Microsoft", "Outlook calendar and mail, and Teams chats. Work tenants often need admin consent.");
        Toggle("Enable Microsoft", null, _s.Microsoft.Enabled, v => _s.Microsoft.Enabled = v);
        Field("App (client) ID", "From your Entra app registration", _s.Microsoft.ClientId, v => _s.Microsoft.ClientId = v.Trim());
        Field("Tenant", "organizations, common, or a tenant ID", _s.Microsoft.Tenant, v => _s.Microsoft.Tenant = v.Trim(), 260);
        Toggle("Calendar", null, _s.Microsoft.Calendar, v => _s.Microsoft.Calendar = v);
        Toggle("Mail", null, _s.Microsoft.Mail, v => _s.Microsoft.Mail = v);
        Toggle("Teams chats", "Needs the Chat.Read permission, which many tenants gate behind admin approval.", _s.Microsoft.Teams, v => _s.Microsoft.Teams = v);

        Section("Slack", "Direct messages and @mentions.");
        Toggle("Enable Slack", null, _s.Slack.Enabled, v => _s.Slack.Enabled = v);
        Secret("User token", "xoxp-… with the search:read scope", _s.Slack.UserToken, v => _s.Slack.UserToken = v.Trim());

        Section("Calendars", "Full: alerts, Next up and AI planning. View only: shown for reference, ignored by everything else (good for a partner's shifts or sports). Hidden: not shown. Skylight's events arrive through your Google calendars.");
        var sources = KnownSources();
        if (sources.Count == 0) _section.Children.Add(Ui.Dim("Connect Google or Outlook (or wait for the first sync) and your calendars appear here."));
        foreach (var k in sources) CalendarRow(k);
        EndSection();

        Section("Calendar feeds (ICS)", "Read-only calendars published as a link. One per line: Name | https://… | personal (add “| personal” for non-work feeds; work feeds are redacted from AI prompts).");
        var ics = new TextBox { AcceptsReturn = true, MinHeight = 70, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            Text = string.Join(Environment.NewLine, _s.IcsFeeds.Select(f => $"{f.Name} | {f.Url}" + (f.IsWork ? "" : " | personal"))) };
        _section.Children.Add(ics);
        _apply.Add(() => _s.IcsFeeds = ics.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|', 3, StringSplitOptions.TrimEntries))
            .Where(p => p.Length >= 2 && p[1].Length > 0)
            .Select(p => new IcsFeed { Name = p[0], Url = p[1], IsWork = !(p.Length == 3 && p[2].Equals("personal", StringComparison.OrdinalIgnoreCase)) }).ToList());
        EndSection();

        // Footer
        var save = Ui.Primary("Save and restart", 22, 10);
        save.Click += (_, _) => SaveAndRestart();
        var raw = Ui.Secondary("Open JSON");
        raw.Click += (_, _) => Process.Start(new ProcessStartInfo(AppPaths.SettingsFile) { UseShellExecute = true });
        var wiz = Ui.Secondary("Setup wizard");
        wiz.ToolTip = "Walk through connecting accounts and setting up the Outlook/Teams sync";
        wiz.Click += (_, _) => { new WizardWindow(AnchorSettings.Load()).Show(); Close(); };
        var cancel = Ui.Secondary("Cancel");
        cancel.Click += (_, _) => Close();
        var hint = Ui.Dim("Anchor restarts to apply connector changes.");
        hint.VerticalAlignment = VerticalAlignment.Center;
        var footer = new DockPanel { Margin = new Thickness(26, 12, 26, 16) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { wiz, raw, cancel, save } };
        save.Margin = new Thickness(0);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        footer.Children.Add(hint);

        var title = new StackPanel { Margin = new Thickness(26, 20, 26, 6) };
        title.Children.Add(new TextBlock { Text = "Settings", FontSize = 24, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"] });

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Padding = new Thickness(26, 8, 26, 8), Child = _page } };
        var root = new DockPanel();
        DockPanel.SetDock(title, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(title); root.Children.Add(footer); root.Children.Add(scroll);
        Content = root;
    }

    void SaveAndRestart()
    {
        try
        {
            foreach (var a in _apply) a();
            if (!TimeOnly.TryParse(_s.WorkdayStart, out _) || !TimeOnly.TryParse(_s.WorkdayEnd, out _))
                throw new FormatException("Workday start and end must look like 08:30.");
            var problems = SettingsValidator.Check(_s);
            if (problems.Count > 0) throw new FormatException(string.Join(Environment.NewLine + Environment.NewLine, problems));
            _s.Save();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Anchor settings"); return; }

        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = true });
        App.Quit();
    }

    // ---------------------------------------------------------------- layout helpers

    void Section(string title, string subtitle)
    {
        EndSection();
        _section = new StackPanel();
        _section.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        _section.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 2, 0, 10) });
        _sectionOpen = true;
    }

    bool _sectionOpen;
    void EndSection()
    {
        if (!_sectionOpen) return;
        var card = Ui.Card(_section, 20);
        card.Margin = new Thickness(0, 0, 0, 14);
        _page.Children.Add(card);
        _sectionOpen = false;
    }

    protected override void OnContentRendered(EventArgs e) { base.OnContentRendered(e); }

    Grid Row(string label, string? hint, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = label });
        if (hint is not null) left.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(left);
        Grid.SetColumn((FrameworkElement)control, 1);
        ((FrameworkElement)control).VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(control);
        return g;
    }

    void Field(string label, string? hint, string value, Action<string> set, double width = 300)
    {
        var box = new TextBox { Text = value, Width = width };
        _section.Children.Add(Row(label, hint, box));
        _apply.Add(() => set(box.Text));
    }

    void Secret(string label, string? hint, string value, Action<string> set)
    {
        var box = new PasswordBox { Password = value, Width = 300 };
        _section.Children.Add(Row(label, hint, box));
        _apply.Add(() => set(box.Password));
    }

    void VoiceRows()
    {
        var voices = Voice.Installed();
        string Short(string full) => full.Replace("Microsoft ", "").Replace(" Desktop", "");
        var options = new[] { "Default" }.Concat(voices.Select(Short)).ToArray();
        var current = voices.FirstOrDefault(v => v == _s.SpeechVoice) is { } cv ? Short(cv) : "Default";
        string voice = _s.SpeechVoice; int rate = _s.SpeechRate;

        RadioButton[] Make(string[] names, Func<string, bool> on, Action<string> pick, string group)
            => names.Select(n =>
            {
                var rb = new RadioButton { Content = n, GroupName = group, IsChecked = on(n), Style = (Style)Application.Current.Resources["Segment"] };
                rb.Checked += (_, _) => pick(n);
                return rb;
            }).ToArray();

        var vg = Guid.NewGuid().ToString("N"); var rg = Guid.NewGuid().ToString("N");
        var vBtns = Make(options, n => n == current, n => voice = n == "Default" ? "" : voices.First(v => Short(v) == n), vg);
        var vPanel = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var b in vBtns) vPanel.Children.Add(b);
        string[] paces = ["Slower", "Normal", "Faster"];
        var pBtns = Make(paces, n => n == (rate < 0 ? "Slower" : rate > 0 ? "Faster" : "Normal"), n => rate = n == "Slower" ? -2 : n == "Faster" ? 2 : 0, rg);
        var pPanel = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var b in pBtns) pPanel.Children.Add(b);
        Border Box(StackPanel p) => new() { Background = Ui.B("Field"), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), Child = p };

        _section.Children.Add(Row("Voice", voices.Count == 0 ? "No Windows voices found." : Voice.NaturalAvailable ? "Natural is an offline neural voice; the others are Windows voices." : "Windows voices. For a more natural one, see the setup guide (Natural voice).", Box(vPanel)));
        var test = Ui.Secondary("Hear it");
        test.Click += (_, _) => { var v = voice; var r = rate; _ = Task.Run(() => Voice.Say(SpeechText.Phrase("Weekly planning", DateTimeOffset.Now.AddMinutes(5), DateTimeOffset.Now), v, r)); };
        var paceRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { Box(pPanel), new Border { Width = 10 }, test } };
        _section.Children.Add(Row("Pace", null, paceRow));
        _apply.Add(() => { _s.SpeechVoice = voice; _s.SpeechRate = rate; });
    }

    void CliRow()
    {
        var exe = ClaudeCliBackend.Locate(_s.ClaudeDesktop.CliPath);
        var test = Ui.Secondary("Test");
        test.Margin = new Thickness(0);
        var result = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 300, TextAlignment = TextAlignment.Right,
            Foreground = Ui.B(exe is null ? "Warn" : "Dim"), Text = exe is null ? "Claude app not found on this PC." : "Claude app found." };
        test.IsEnabled = exe is not null;
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false; result.Foreground = Ui.B("Dim"); result.Text = "Testing… (a few seconds)";
            var (ok, msg) = await new ClaudeCliBackend(exe!, _s.ClaudeDesktop).TestAsync(CancellationToken.None);
            result.Foreground = Ui.B(ok ? "Ok" : "Alarm"); result.Text = msg; test.IsEnabled = true;
        };
        var signIn = Ui.Secondary("Sign in again");
        signIn.ToolTip = "Opens Claude's sign-in in a console window and your browser. Finish signing in, then press Test.";
        signIn.Margin = new Thickness(0, 8, 0, 0);
        signIn.IsEnabled = exe is not null;
        signIn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(exe!, "auth login --claudeai") { UseShellExecute = true }); result.Foreground = Ui.B("Dim"); result.Text = "Finish signing in in your browser, then press Test."; }
            catch (Exception ex) { result.Foreground = Ui.B("Alarm"); result.Text = ex.Message; }
        };
        _section.Children.Add(Row("Test Claude login", "Makes one tiny request.", new StackPanel { Children = { test, signIn, result } }));
    }

    /// <summary>Every calendar Anchor knows about: discovered on accounts, plus any source that currently has events.</summary>
    List<KnownCalendar> KnownSources()
    {
        var st = _engine.Store.State;
        var list = st.KnownCalendars.ToList();
        foreach (var src in st.CachedEvents.Select(e => e.Source).Distinct().Where(s => list.All(k => k.Source != s)))
            list.Add(new KnownCalendar(src, src == Bridge.EventSource ? "Outlook (work, via Claude)" : src, null));
        return list.OrderBy(k => CalendarPrefs.GroupOf(_s, k.Source)).ThenBy(k => k.Name).ToList();
    }

    void CalendarRow(KnownCalendar k)
    {
        var pref = _s.Calendars.FirstOrDefault(p => p.Source == k.Source);
        var group = CalendarPrefs.GroupOf(_s, k.Source);
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 11, Height = 11, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)new BrushConverter().ConvertFromString(!string.IsNullOrWhiteSpace(k.Color) ? k.Color : group == CalGroup.Work ? CalendarPrefs.WorkColor : CalendarPrefs.PersonalColor)!,
        };
        var useGroup = Guid.NewGuid().ToString("N");
        var mode = CalendarPrefs.ModeOf(_s, k.Source);
        RadioButton Use(string text, CalMode m, string tip) => new()
        {
            Content = text, GroupName = useGroup, Style = (Style)Application.Current.Resources["Segment"], IsChecked = mode == m, ToolTip = tip,
            Padding = new Thickness(9, 5, 9, 5), FontSize = 12,
        };
        var full = Use("Full", CalMode.Full, "Alerts, \"Next up\" and AI planning");
        var viewOnly = Use("View only", CalMode.ViewOnly, "Shown in the agenda, but no alerts, not in \"Next up\", and never given to AI");
        var hidden = Use("Hidden", CalMode.Hidden, "Not shown anywhere");
        var useSeg = new Border { Background = Ui.B("Field"), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { full, viewOnly, hidden } } };
        var radios = Guid.NewGuid().ToString("N");
        var personal = new RadioButton { Content = "Personal", GroupName = radios, Style = (Style)Application.Current.Resources["Segment"], IsChecked = group == CalGroup.Personal, Padding = new Thickness(9, 5, 9, 5), FontSize = 12 };
        var work = new RadioButton { Content = "Work", GroupName = radios, Style = (Style)Application.Current.Resources["Segment"], IsChecked = group == CalGroup.Work, Padding = new Thickness(9, 5, 9, 5), FontSize = 12 };
        var seg = new Border { Background = Ui.B("Field"), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { personal, work } } };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, useSeg, seg } };
        _section.Children.Add(Row(k.Name, k.Source == k.Name ? null : k.Source, controls));
        _apply.Add(() =>
        {
            _s.Calendars.RemoveAll(p => p.Source == k.Source);
            var m = hidden.IsChecked == true ? CalMode.Hidden : viewOnly.IsChecked == true ? CalMode.ViewOnly : CalMode.Full;
            _s.Calendars.Add(new CalendarPref { Source = k.Source, Mode = m.ToString(), Enabled = m != CalMode.Hidden, Group = work.IsChecked == true ? "Work" : "Personal" });
        });
    }

    void KeyRow()
    {
        var box = new PasswordBox { Width = 230 };
        var test = Ui.Secondary("Test");
        test.Margin = new Thickness(8, 0, 0, 0);
        var result = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 300, TextAlignment = TextAlignment.Right };
        test.Click += async (_, _) =>
        {
            var key = box.Password.Length > 0 ? box.Password : _engine.CurrentAnthropicKey();
            if (string.IsNullOrWhiteSpace(key)) { result.Foreground = Ui.B("Warn"); result.Text = "Paste a key first."; return; }
            test.IsEnabled = false; result.Foreground = Ui.B("Dim"); result.Text = "Testing…";
            var (ok, msg) = await ClaudeService.TestKeyAsync(key.Trim(), _s.Ai.FastModel, CancellationToken.None);
            result.Foreground = Ui.B(ok ? "Ok" : "Alarm"); result.Text = msg; test.IsEnabled = true;
        };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Children = { box, test } };
        var holder = new StackPanel { Children = { line, result } };
        _section.Children.Add(Row("Anthropic API key", "Stored encrypted for your Windows user. Never written to settings.json.", holder));
        _apply.Add(() => { if (box.Password.Length > 0) _engine.SaveAnthropicKey(box.Password.Trim()); });
    }

    void Toggle(string label, string? hint, bool value, Action<bool> set)
    {
        var sw = new CheckBox { IsChecked = value, Content = "" };
        sw.HorizontalAlignment = HorizontalAlignment.Right;
        _section.Children.Add(Row(label, hint, sw));
        _apply.Add(() => set(sw.IsChecked == true));
    }

    void Segmented(string label, string? hint, string[] options, string current, Action<string> set)
    {
        var group = Guid.NewGuid().ToString("N");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = options.Select(o => new RadioButton
        {
            Content = o, GroupName = group, IsChecked = o == current, Style = (Style)Application.Current.Resources["Segment"],
        }).ToList();
        if (buttons.All(b => b.IsChecked != true)) buttons[1].IsChecked = true;
        foreach (var b in buttons) panel.Children.Add(b);
        var holder = new Border { Background = Ui.B("Field"), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), Child = panel };
        _section.Children.Add(Row(label, hint, holder));
        _apply.Add(() => set((string)buttons.First(b => b.IsChecked == true).Content));
    }

    static int ParseInt(string v, int fallback) => int.TryParse(v.Trim(), out var n) && n >= 0 ? n : fallback;
    static double ParseDouble(string v, double fallback) => double.TryParse(v.Trim(), out var n) && n >= 0 ? n : fallback;
    static int[] ParseInts(string v, int[] fallback)
    {
        var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nums = parts.Select(p => int.TryParse(p, out var n) && n >= 0 ? n : -1).ToArray();
        return nums.Length > 0 && nums.All(n => n >= 0) ? nums : fallback;
    }
}
