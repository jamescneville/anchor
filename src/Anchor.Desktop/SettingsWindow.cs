using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Anchor.Core;

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
        Field("Workday starts", "HH:mm", _s.WorkdayStart, v => _s.WorkdayStart = v.Trim(), 90);
        Field("Workday ends", "HH:mm", _s.WorkdayEnd, v => _s.WorkdayEnd = v.Trim(), 90);
        Field("“Where was I?” hotkey", "e.g. Ctrl+Space, Ctrl+Shift+W, Win+Shift+A", _s.Hotkey, v => _s.Hotkey = v.Trim(), 200);
        Toggle("Speak alerts aloud", null, _s.SpeakAlerts, v => _s.SpeakAlerts = v);
        Toggle("Morning plan and end-of-day wrap-up", "A short brief at workday start, and a wrap-up 30 minutes before it ends.", _s.BriefsEnabled, v => _s.BriefsEnabled = v);

        Section("Claude", _engine.HasAnthropicKey ? "A key is saved. Leave the box empty to keep it." : "Add an API key to turn on AI triage and day plans.");
        Secret("Anthropic API key", "Stored encrypted for your Windows user. Never written to settings.json.", "", v => { if (v.Length > 0) _engine.SaveAnthropicKey(v); });
        Toggle("Enable AI", null, _s.Ai.Enabled, v => _s.Ai.Enabled = v);
        Toggle("Allow work items to be sent to Claude", "Outlook, Teams and Slack content. Leave off until you have approval to send work data to Anthropic.", _s.Ai.SendWorkContentToClaude, v => _s.Ai.SendWorkContentToClaude = v);
        Field("Monthly budget", "USD", _s.Ai.MonthlyBudgetUsd.ToString(), v => _s.Ai.MonthlyBudgetUsd = ParseDouble(v, _s.Ai.MonthlyBudgetUsd), 90);
        Field("Daily soft cap", "USD; limits background triage only", _s.Ai.DailySoftCapUsd.ToString(), v => _s.Ai.DailySoftCapUsd = ParseDouble(v, _s.Ai.DailySoftCapUsd), 90);
        Field("Hard cut-off", "USD; AI stops entirely at this monthly spend", _s.Ai.CutoffUsd.ToString(), v => _s.Ai.CutoffUsd = ParseDouble(v, _s.Ai.CutoffUsd), 90);

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

        Section("Calendar feeds (ICS)", "Read-only calendars published as a link. One per line: Name | https://…");
        var ics = new TextBox { AcceptsReturn = true, MinHeight = 70, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            Text = string.Join(Environment.NewLine, _s.IcsFeeds.Select(f => $"{f.Name} | {f.Url}")) };
        _section.Children.Add(ics);
        _apply.Add(() => _s.IcsFeeds = ics.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[1].Length > 0).Select(p => new IcsFeed { Name = p[0], Url = p[1] }).ToList());
        EndSection();

        // Footer
        var save = Ui.Primary("Save and restart", 22, 10);
        save.Click += (_, _) => SaveAndRestart();
        var raw = Ui.Secondary("Open JSON");
        raw.Click += (_, _) => Process.Start(new ProcessStartInfo(AppPaths.SettingsFile) { UseShellExecute = true });
        var cancel = Ui.Secondary("Cancel");
        cancel.Click += (_, _) => Close();
        var hint = Ui.Dim("Anchor restarts to apply connector changes.");
        hint.VerticalAlignment = VerticalAlignment.Center;
        var footer = new DockPanel { Margin = new Thickness(26, 12, 26, 16) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { raw, cancel, save } };
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
