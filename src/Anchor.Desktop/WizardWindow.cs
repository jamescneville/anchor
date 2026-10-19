using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Anchor.Core;
using Anchor.Core.AI;

namespace Anchor.Desktop;

/// <summary>
/// First-run setup: connect Google, turn on the Claude login, optionally set up the Outlook/Teams sync, pick a few preferences.
/// Every step is optional and explains how to get what it needs. Reachable again from the tray menu and Settings.
/// </summary>
public sealed class WizardWindow : Window
{
    readonly AnchorSettings _s;
    readonly Border _body = new();
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal };
    readonly Button _back = Ui.Secondary("Back");
    readonly Button _next = Ui.Primary("Next", 22, 9);
    readonly TextBlock _step = new() { FontSize = 12, Foreground = Ui.B("Dim") };
    int _page;
    bool _finished;

    // Choices (applied to settings only when the user finishes)
    bool _google;
    string _clientId = "", _secret = "";
    bool _claude;
    string _email = "";
    bool _use24h, _autostart;
    string _hotkey;

    const int Pages = 6;

    public WizardWindow(AnchorSettings settings)
    {
        _s = settings;
        _google = settings.Google.Enabled;
        _clientId = settings.Google.ClientId; _secret = settings.Google.ClientSecret;
        _claude = settings.ClaudeDesktop.Enabled || ClaudeCliBackend.Locate(settings.ClaudeDesktop.CliPath) is not null;
        _email = settings.WorkEmail;
        _use24h = settings.Use24HourClock;
        _autostart = App.IsAutoStart();
        _hotkey = settings.Hotkey;

        Title = "Anchor · Setup";
        Width = 700; Height = 700; MinHeight = 560;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.Style(this);
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 56, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0),
        });

        var header = new DockPanel { Margin = new Thickness(30, 18, 30, 0) };
        var logo = new Image { Source = Ui.Logo, Width = 30, Height = 30, Margin = new Thickness(0, 0, 12, 0) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(logo, Dock.Left);
        header.Children.Add(logo);
        header.Children.Add(new StackPanel { Children = { new TextBlock { Text = "Set up Anchor", FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"] }, _step } });

        var footer = new DockPanel { Margin = new Thickness(30, 12, 30, 20) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        _next.Margin = new Thickness(0);
        nav.Children.Add(_back); nav.Children.Add(_next);
        DockPanel.SetDock(nav, Dock.Right);
        footer.Children.Add(nav);
        footer.Children.Add(_dots);

        _back.Click += (_, _) => Go(_page - 1);
        _next.Click += (_, _) => { if (_page == Pages - 1) Finish(); else if (Validate()) Go(_page + 1); };

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Padding = new Thickness(30, 12, 30, 12), Child = _body } };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header); root.Children.Add(footer); root.Children.Add(scroll);
        Content = new Grid { Children = { root, Ui.CaptionButtons(this) } };

        // Closing early still counts as seen, so it doesn't reappear on every launch. It can be reopened from the tray menu.
        Closed += (_, _) => { if (!_finished && !_s.SetupDone) { _s.SetupDone = true; try { _s.Save(); } catch { /* not worth blocking exit */ } } };
        Go(0);
    }

    // ---------------------------------------------------------------- navigation

    void Go(int page)
    {
        _page = Math.Clamp(page, 0, Pages - 1);
        _back.Visibility = _page == 0 ? Visibility.Hidden : Visibility.Visible;
        _next.Content = _page == Pages - 1 ? "Finish" : _page == 0 ? "Get started" : "Next";
        _step.Text = $"Step {_page + 1} of {Pages}";
        _dots.Children.Clear();
        for (var i = 0; i < Pages; i++)
            _dots.Children.Add(new Border { Width = i == _page ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = Ui.B(i == _page ? "Accent" : "Line") });
        _body.Child = _page switch { 0 => Welcome(), 1 => GooglePage(), 2 => ClaudePage(), 3 => WorkPage(), 4 => PrefsPage(), _ => DonePage() };
    }

    bool Validate()
    {
        if (_page == 1 && _google)
        {
            var g = new GoogleSettings { Enabled = true, ClientId = _clientId.Trim(), ClientSecret = _secret.Trim() };
            var probe = new AnchorSettings { Google = g };
            var problems = SettingsValidator.Check(probe);
            if (_clientId.Trim().Length == 0 || _secret.Trim().Length == 0) problems = [.. problems, "Paste both the client ID and the client secret, or turn Google off for now (you can add it later in Settings)."];
            if (problems.Count > 0) { MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, problems.Distinct()), "Google"); return false; }
        }
        if (_page == 4 && _hotkey.Trim().Length == 0) { MessageBox.Show(this, "Pick a hotkey such as Ctrl+Space.", "Hotkey"); return false; }
        return true;
    }

    void Finish()
    {
        _finished = true;
        _s.Google.Enabled = _google;
        if (_google) { _s.Google.ClientId = _clientId.Trim(); _s.Google.ClientSecret = _secret.Trim(); _s.Google.Calendar = true; _s.Google.Gmail = true; }
        _s.ClaudeDesktop.Enabled = _claude;
        _s.WorkEmail = _email.Trim();
        _s.Use24HourClock = _use24h;
        _s.Hotkey = _hotkey.Trim();
        _s.SetupDone = true;
        try { _s.Save(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Anchor setup"); _finished = false; return; }
        App.SetAutoStart(_autostart);
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart" + (_google ? " --connect-google" : "")) { UseShellExecute = true });
        App.Quit();
    }

    // ---------------------------------------------------------------- building blocks

    static TextBlock H(string t) => new() { Text = t, FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"], Margin = new Thickness(0, 6, 0, 8) };
    static TextBlock P(string t, bool dim = false, double top = 8) => new() { Text = t, FontSize = 13.5, LineHeight = 20, Margin = new Thickness(0, top, 0, 0), Foreground = dim ? Ui.B("Dim") : Ui.B("Fg") };

    static UIElement Bullet(string title, string text) => new StackPanel
    {
        Margin = new Thickness(0, 10, 0, 0),
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 },
            new TextBlock { Text = text, Foreground = Ui.B("Dim"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0), LineHeight = 19 },
        },
    };

    static Border Box(UIElement child) => new() { Background = Ui.B("Card"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 14, 0, 0), Child = child };

    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ } }

    CheckBox Check(string text, bool on, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 14, 0, 0), FontSize = 14, FontWeight = FontWeights.SemiBold };
        c.Checked += (_, _) => set(true); c.Unchecked += (_, _) => set(false);
        return c;
    }

    static UIElement Labeled(string label, TextBox box, string? hint = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(box);
        if (hint is not null) sp.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 4, 0, 0) });
        return sp;
    }

    // ---------------------------------------------------------------- pages

    UIElement Welcome() => new StackPanel
    {
        Children =
        {
            H("Welcome to Anchor"),
            P("Anchor keeps you oriented through the day: loud, escalating alerts before meetings, one agenda for every calendar, a short list of what needs you, and a “Where was I?” button for when you lose the thread."),
            Bullet("Everything here is optional", "Connect only what you want. Skip any step and add it later in Settings."),
            Bullet("Your data stays on this PC", "Sign-ins are stored encrypted for your Windows account. Nothing is sent anywhere except to the services you connect."),
            Bullet("Work content is kept out of AI by default", "Meetings and mail from work sources are redacted before anything reaches Claude, unless you turn that on."),
            P("This takes about three minutes. Press Get started.", dim: true, top: 18),
        },
    };

    UIElement GooglePage()
    {
        var root = new StackPanel();
        root.Children.Add(H("Google: calendar and Gmail"));
        root.Children.Add(P("Shows every calendar on your Google account in the agenda (including ones a family calendar app such as Skylight syncs from), and finds Gmail that needs a reply."));

        var fields = new StackPanel();
        var id = new TextBox { Text = _clientId }; id.TextChanged += (_, _) => _clientId = id.Text;
        var sec = new TextBox { Text = _secret }; sec.TextChanged += (_, _) => _secret = sec.Text;
        fields.Children.Add(Labeled("OAuth client ID", id, "Ends in .apps.googleusercontent.com"));
        fields.Children.Add(Labeled("OAuth client secret", sec, "Starts with GOCSPX-"));

        var guide = new StackPanel { Visibility = Visibility.Collapsed };
        guide.Children.Add(P("Google asks every app to have its own free “client”. Make one for yourself (about five minutes):", top: 4));
        string[] steps =
        [
            "Open the Google Cloud Console (button below) and create a project. Any name works.",
            "APIs & Services, then Library: enable “Google Calendar API” and “Gmail API”.",
            "OAuth consent screen: choose External, fill in the app name and your email, and add your own Google address under Test users.",
            "Credentials, then Create credentials, then OAuth client ID. Choose the type Desktop app.",
            "Copy the Client ID and Client secret into the boxes above.",
        ];
        for (var i = 0; i < steps.Length; i++) guide.Children.Add(P($"{i + 1}.  {steps[i]}", dim: true, top: 6));
        guide.Children.Add(P("When you sign in, Google may say the app is unverified. That's expected for your own project: choose Advanced, then continue.", dim: true, top: 10));
        var console = Ui.Secondary("Open Google Cloud Console"); console.Margin = new Thickness(0, 12, 0, 0);
        console.HorizontalAlignment = HorizontalAlignment.Left;
        console.Click += (_, _) => Open("https://console.cloud.google.com/");
        guide.Children.Add(console);

        var how = Ui.Ghost("I don't have these yet: show me how"); how.Margin = new Thickness(-9, 6, 0, 0); how.HorizontalAlignment = HorizontalAlignment.Left;
        how.Click += (_, _) => guide.Visibility = guide.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        var panel = new StackPanel { Visibility = _google ? Visibility.Visible : Visibility.Collapsed, Children = { fields, how, guide } };
        var toggle = Check("Connect Google", _google, v => { _google = v; panel.Visibility = v ? Visibility.Visible : Visibility.Collapsed; });
        root.Children.Add(toggle);
        root.Children.Add(panel);
        root.Children.Add(P("After setup, Anchor opens your browser once so you can approve access.", dim: true, top: 14));
        return root;
    }

    UIElement ClaudePage()
    {
        var exe = ClaudeCliBackend.Locate(_s.ClaudeDesktop.CliPath);
        var root = new StackPanel();
        root.Children.Add(H("AI: use your Claude login"));
        root.Children.Add(P("Anchor can plan your day, rank what needs attention and answer “Where was I?” using the Claude account that's already signed in on this PC. No API key, and nothing pops up: it runs quietly in the background."));

        if (exe is null)
        {
            var open = Ui.Secondary("Get Claude desktop"); open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Click += (_, _) => Open("https://claude.ai/download");
            root.Children.Add(Box(new StackPanel { Children =
            {
                new TextBlock { Text = "Claude desktop wasn't found on this PC", FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Warn") },
                P("Install Claude desktop and sign in, then run this setup again from Anchor's tray icon (Setup wizard). Everything else in Anchor works without it.", dim: true),
                new Border { Height = 10 }, open,
            } }));
            _claude = false;
            return root;
        }

        root.Children.Add(Check("Use my Claude login", _claude, v => _claude = v));
        var result = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        var test = Ui.Secondary("Test it (one tiny request)"); test.HorizontalAlignment = HorizontalAlignment.Left; test.Margin = new Thickness(0, 12, 0, 0);
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false; result.Foreground = Ui.B("Dim"); result.Text = "Testing…";
            var (ok, msg) = await new ClaudeCliBackend(exe, _s.ClaudeDesktop).TestAsync(CancellationToken.None);
            result.Foreground = Ui.B(ok ? "Ok" : "Alarm"); result.Text = msg; test.IsEnabled = true;
        };
        root.Children.Add(Box(new StackPanel { Children =
        {
            new TextBlock { Text = "Claude desktop found", FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Ok") },
            P("Uses a small, inexpensive model for quick checks and a stronger one for day plans, with daily and monthly spending limits.", dim: true),
            test, result,
        } }));
        root.Children.Add(P("Work meetings and work mail stay redacted from Claude unless you turn on “Include work items” in Settings. Check with your organisation before doing that.", dim: true, top: 14));
        return root;
    }

    UIElement WorkPage()
    {
        var root = new StackPanel();
        root.Children.Add(H("Outlook and Teams (work)"));
        root.Children.Add(P("Work Microsoft accounts usually can't be connected directly to personal tools. Anchor gets your Outlook calendar, mail and Teams follow-ups another way: a small scheduled task in Claude desktop reads them through Claude's Microsoft 365 connector and hands Anchor the results. It's read-only, and it needs Claude desktop."));

        var email = new TextBox { Text = _email }; email.TextChanged += (_, _) => _email = email.Text;
        root.Children.Add(Labeled("Your work email (optional)", email, "So Claude can ignore mail you sent yourself."));

        var status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontSize = 12.5, Foreground = Ui.B("Ok") };
        var copy = Ui.Primary("Copy setup prompt for Claude", 16, 8); copy.HorizontalAlignment = HorizontalAlignment.Left; copy.Margin = new Thickness(0, 12, 0, 0);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(ClaudeTaskPrompt.Build(_email)); status.Foreground = Ui.B("Ok"); status.Text = "Copied. Paste it into a new Claude chat."; }
            catch (Exception ex) { status.Foreground = Ui.B("Alarm"); status.Text = "Couldn't copy: " + ex.Message; }
        };
        var openClaude = Ui.Secondary("Open Claude"); openClaude.Margin = new Thickness(10, 12, 0, 0);
        openClaude.Click += (_, _) => Open("claude://claude.ai/new");

        root.Children.Add(Box(new StackPanel { Children =
        {
            new TextBlock { Text = "To turn it on (about two minutes)", FontWeight = FontWeights.SemiBold },
            P("1.  In Claude desktop, open Settings, then Connectors, and connect Microsoft 365. Your organisation may need to approve it.", dim: true, top: 8),
            P("2.  Press the button below to copy a prompt.", dim: true, top: 4),
            P("3.  Start a new session in Claude desktop's Code tab, paste the prompt and send it. Claude creates two scheduled tasks: one hourly on weekdays and one four times a day at weekends.", dim: true, top: 4),
            P("4.  Leave Claude desktop running. Anchor fills in on its own after the first sync.", dim: true, top: 4),
            new StackPanel { Orientation = Orientation.Horizontal, Children = { copy, openClaude } },
            status,
        } }));
        root.Children.Add(P("You can skip this and do it any time: run Setup wizard from Settings or the tray icon.", dim: true, top: 14));
        return root;
    }

    UIElement PrefsPage()
    {
        var root = new StackPanel();
        root.Children.Add(H("A few preferences"));
        root.Children.Add(Check("Use a 24-hour clock", _use24h, v => _use24h = v));
        root.Children.Add(P("Off shows times like 2:30 PM.", dim: true, top: 2));
        root.Children.Add(Check("Start Anchor when I sign in to Windows", _autostart, v => _autostart = v));
        var hk = new TextBox { Text = _hotkey, Width = 220, HorizontalAlignment = HorizontalAlignment.Left }; hk.TextChanged += (_, _) => _hotkey = hk.Text;
        root.Children.Add(Labeled("“Where was I?” hotkey", hk, "Works from anywhere. Examples: Ctrl+Space, Ctrl+Shift+W, Win+Shift+A. If another app already uses it, pick something else."));
        root.Children.Add(P("More options (alert loudness, workday hours, calendar colours, which calendars count) are in Settings.", dim: true, top: 18));
        return root;
    }

    UIElement DonePage()
    {
        var root = new StackPanel();
        root.Children.Add(H("Ready"));
        root.Children.Add(P("Here's what will happen when you press Finish:"));
        string Line(bool on, string yes, string no) => (on ? "✓  " : "○  ") + (on ? yes : no);
        root.Children.Add(P(Line(_google, "Google will connect (your browser opens once to approve).", "Google is skipped. Add it any time in Settings."), top: 10));
        root.Children.Add(P(Line(_claude, "Claude login is on for plans and “Where was I?”.", "AI is off. Everything else still works."), top: 6));
        root.Children.Add(P(Line(_email.Trim().Length > 0, "Work email saved for the Outlook and Teams sync.", "Outlook and Teams: set up later using the prompt in Settings."), top: 6));
        root.Children.Add(P(Line(_autostart, "Anchor starts with Windows.", "Anchor won't start automatically."), top: 6));
        root.Children.Add(P("Anchor restarts once to apply this, then lives in the tray (the anchor icon near the clock). Press " + _hotkey.Trim() + " any time to ask “Where was I?”. Use Settings or the tray menu to change anything.", dim: true, top: 18));
        return root;
    }
}

/// <summary>Builds the text the user pastes into Claude to create the two sync tasks with their own paths, time zone and address.</summary>
public static class ClaudeTaskPrompt
{
    public static string Build(string email)
    {
        var body = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/claude-sync-task.md"))!.Stream;
        var task = new StreamReader(body).ReadToEnd()
            .Replace("{TZ}", TimeZoneInfo.Local.DisplayName)
            .Replace("{EMAIL}", email.Trim().Length > 0 ? email.Trim() : "not given: treat messages from the account owner as mine")
            .Replace("{BRIDGE}", Bridge.Dir + Path.DirectorySeparatorChar);
        var skill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "scheduled-tasks", "anchor-outlook-sync", "SKILL.md");
        return $"""
            Please set up two scheduled tasks for my Anchor app with the scheduled-tasks tool (create_scheduled_task). Create them exactly as specified; do not change the prompts.

            TASK 1
            taskId: anchor-outlook-sync
            title: Anchor: sync Outlook and Teams
            description: Hourly on weekdays: lean, read-only copy of my Outlook calendar, mail and Teams follow-ups into Anchor's bridge folder.
            cronExpression: 0 7-18 * * 1-5
            notifyOnCompletion: false
            prompt (everything between the markers, verbatim):
            ----- BEGIN PROMPT -----
            {task.Trim()}
            ----- END PROMPT -----

            TASK 2
            taskId: anchor-outlook-sync-weekend
            title: Anchor: weekend sync
            description: Weekends, four times a day: runs the same lean sync as the weekday task.
            cronExpression: 0 9,13,17,21 * * 0,6
            notifyOnCompletion: false
            prompt: Read the file {skill} and carry out the instructions in its body exactly (ignore its frontmatter). It is a lean mechanical sync: do not deliberate, use as few tool calls as possible, and finish with the single one-line reply it specifies.

            After creating both, open each task's SKILL.md and add the line  model: claude-haiku-5-5  to its frontmatter (the sync is mechanical, so use the small model). Then reply with one line saying both tasks exist. Do not run them yet.
            """;
    }
}
