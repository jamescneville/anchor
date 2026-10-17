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
    readonly StackPanel _body = new() { Margin = new Thickness(0, 0, 12, 0) };
    readonly List<Action> _apply = new();

    public SettingsWindow(AssistantEngine engine)
    {
        _engine = engine;
        _s = engine.Settings;
        Title = "Anchor · Settings";
        Width = 620; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.Resources["Bg"];

        Section("ALERTS & FOCUS");
        var intensity = Combo("Meeting alert intensity", ["Standard", "Strong", "Maximum"], _s.AlertIntensity, v => _s.AlertIntensity = v);
        Note("Standard: small corner card. Strong: big centred card + sound, must be acknowledged. Maximum: repeats until acknowledged.");
        Text("Alert minutes before start", string.Join(",", _s.AlertOffsetsMinutes), v => _s.AlertOffsetsMinutes = ParseInts(v, _s.AlertOffsetsMinutes));
        Text("Extra lead for in-person meetings (min, 0=off)", _s.InPersonLeadMinutes.ToString(), v => _s.InPersonLeadMinutes = ParseInt(v, _s.InPersonLeadMinutes));
        Text("Snooze minutes", _s.SnoozeMinutes.ToString(), v => _s.SnoozeMinutes = Math.Max(1, ParseInt(v, _s.SnoozeMinutes)));
        Text("Focus check-in every (min, 0=off)", _s.CheckInMinutes.ToString(), v => _s.CheckInMinutes = ParseInt(v, _s.CheckInMinutes));
        Text("Workday start (HH:mm)", _s.WorkdayStart, v => _s.WorkdayStart = v);
        Text("Workday end (HH:mm)", _s.WorkdayEnd, v => _s.WorkdayEnd = v);
        Check("Speak alerts aloud", _s.SpeakAlerts, v => _s.SpeakAlerts = v);
        Check("Morning plan and end-of-day wrap-up", _s.BriefsEnabled, v => _s.BriefsEnabled = v);
        Text("Global 'Where was I?' hotkey", _s.Hotkey, v => _s.Hotkey = v);

        Section("CLAUDE");
        var key = Text("Anthropic API key (stored encrypted for this Windows user)", "", null, password: true);
        Note(_engine.HasAnthropicKey ? "A key is already set. Leave blank to keep it." : "No key set: AI plans and triage are off until you add one.");
        _apply.Add(() => { if (key.Text.Length > 0) _engine.SaveAnthropicKey(key.Text); });
        Check("Enable AI", _s.Ai.Enabled, v => _s.Ai.Enabled = v);
        Check("Allow sending work items (Outlook, Teams, Slack) to Claude. Needs Autodesk approval.", _s.Ai.SendWorkContentToClaude, v => _s.Ai.SendWorkContentToClaude = v);
        Text("Monthly budget USD", _s.Ai.MonthlyBudgetUsd.ToString(), v => _s.Ai.MonthlyBudgetUsd = ParseDouble(v, _s.Ai.MonthlyBudgetUsd));
        Text("Daily soft cap USD (background triage)", _s.Ai.DailySoftCapUsd.ToString(), v => _s.Ai.DailySoftCapUsd = ParseDouble(v, _s.Ai.DailySoftCapUsd));
        Text("Cut-off USD", _s.Ai.CutoffUsd.ToString(), v => _s.Ai.CutoffUsd = ParseDouble(v, _s.Ai.CutoffUsd));

        Section("GOOGLE  (Gmail + all calendars, including the one Skylight syncs)");
        Check("Enable Google", _s.Google.Enabled, v => _s.Google.Enabled = v);
        Text("OAuth client ID", _s.Google.ClientId, v => _s.Google.ClientId = v.Trim());
        Text("OAuth client secret", _s.Google.ClientSecret, v => _s.Google.ClientSecret = v.Trim(), password: true);
        Check("Calendars", _s.Google.Calendar, v => _s.Google.Calendar = v);
        Check("Gmail", _s.Google.Gmail, v => _s.Google.Gmail = v);

        Section("MICROSOFT  (Outlook + Teams)");
        Check("Enable Microsoft", _s.Microsoft.Enabled, v => _s.Microsoft.Enabled = v);
        Text("App (client) ID", _s.Microsoft.ClientId, v => _s.Microsoft.ClientId = v.Trim());
        Text("Tenant (organizations / common / tenant id)", _s.Microsoft.Tenant, v => _s.Microsoft.Tenant = v.Trim());
        Check("Calendar", _s.Microsoft.Calendar, v => _s.Microsoft.Calendar = v);
        Check("Mail", _s.Microsoft.Mail, v => _s.Microsoft.Mail = v);
        Check("Teams chats (often needs admin consent)", _s.Microsoft.Teams, v => _s.Microsoft.Teams = v);

        Section("SLACK");
        Check("Enable Slack", _s.Slack.Enabled, v => _s.Slack.Enabled = v);
        Text("User token (xoxp-...)", _s.Slack.UserToken, v => _s.Slack.UserToken = v.Trim(), password: true);

        Section("ICS CALENDAR FEEDS  (one per line:  Name | https://...)");
        var ics = new TextBox { AcceptsReturn = true, MinHeight = 60, Text = string.Join(Environment.NewLine, _s.IcsFeeds.Select(f => $"{f.Name} | {f.Url}")) };
        _body.Children.Add(ics);
        _apply.Add(() => _s.IcsFeeds = ics.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[1].Length > 0).Select(p => new IcsFeed { Name = p[0], Url = p[1] }).ToList());

        var save = new Button { Content = "Save and restart", Padding = new Thickness(18, 8, 18, 8) };
        save.Click += (_, _) => SaveAndRestart();
        var raw = new Button { Content = "Open JSON file" };
        raw.Click += (_, _) => Process.Start(new ProcessStartInfo(AppPaths.SettingsFile) { UseShellExecute = true });
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { save, raw, cancel } };

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    void SaveAndRestart()
    {
        try
        {
            foreach (var a in _apply) a();
            if (!TimeOnly.TryParse(_s.WorkdayStart, out _) || !TimeOnly.TryParse(_s.WorkdayEnd, out _))
                throw new FormatException("Workday start/end must look like 08:30.");
            _s.Save();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Anchor settings"); return; }

        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = true });
        App.Quit();
    }

    // ---- row helpers

    void Section(string title) => _body.Children.Add(new TextBlock
    {
        Text = title, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["Accent"], Margin = new Thickness(0, 18, 0, 6),
    });

    void Note(string text) => _body.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = (Brush)Application.Current.Resources["Dim"], Margin = new Thickness(0, 0, 0, 4) });

    TextBox Text(string label, string value, Action<string>? set, bool password = false)
    {
        _body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
        var box = new TextBox { Text = value };
        if (password) box.FontFamily = new FontFamily("Consolas");
        _body.Children.Add(box);
        if (set is not null) _apply.Add(() => set(box.Text));
        return box;
    }

    ComboBox Combo(string label, string[] items, string current, Action<string> set)
    {
        _body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
        var c = new ComboBox { ItemsSource = items, SelectedItem = items.Contains(current) ? current : items[1] };
        _body.Children.Add(c);
        _apply.Add(() => set((string)c.SelectedItem));
        return c;
    }

    void Check(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = new TextBlock { Text = label }, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
        _body.Children.Add(c);
        _apply.Add(() => set(c.IsChecked == true));
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
