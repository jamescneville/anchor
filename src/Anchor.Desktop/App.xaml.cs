using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Anchor.Core;
using Microsoft.Win32;

namespace Anchor.Desktop;

public partial class App : Application
{
    const int HotkeyId = 0xA17C;
    const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>True once we really want to exit; until then closing the main window only hides it to the tray.</summary>
    public static bool Exiting { get; private set; }
    public static void Quit() { Exiting = true; Current.Shutdown(); }

    Mutex? _single;
    AssistantEngine? _engine;
    MainWindow? _main;
    System.Windows.Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, "Anchor.SingleInstance", out var first);
        if (!first && e.Args.Contains("--restart"))
        {
            // Restarting from Settings: wait for the previous instance to exit and release the mutex.
            try { first = _single.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) =>
        {
            File.AppendAllText(Path.Combine(AppPaths.Dir, "crash.log"), $"{DateTime.Now:o} {ex.Exception}\n");
            ex.Handled = true; // an assistant that dies silently is worse than one that logs and carries on
        };

        var settings = AnchorSettings.Load();
        Fmt.Use24Hour = settings.Use24HourClock;
        _engine = new AssistantEngine(settings, new StateStore());
        _main = new MainWindow(_engine);

        _engine.AlertDue += a => Dispatcher.BeginInvoke(() => new AlertWindow(_engine, a).Show());
        _engine.BriefDue += kind => Dispatcher.BeginInvoke(() => new BriefWindow(_engine, kind).Show());
        _engine.CheckInDue += () => Dispatcher.BeginInvoke(() => new RecoveryWindow(_engine, true).Show());

        SystemEvents.PowerModeChanged += (_, p) => { if (p.Mode == PowerModes.Resume) _engine.OnResume(); };
        SystemEvents.SessionSwitch += (_, s) => { if (s.Reason == SessionSwitchReason.SessionUnlock) _engine.OnResume(); };
        SystemEvents.TimeChanged += (_, _) => _engine.OnResume();

        BuildTray();
        RegisterHotkey();
        _engine.Start();

        if (!e.Args.Contains("--background")) _main.Show();
        if (e.Args.Contains("--test-alert")) _engine.AddTestMeeting(1);
        if (e.Args.Contains("--plan-test"))   // dev: run the AI plan headless and write the outcome to plan-test.txt
            _ = Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var text = await _engine.PlanDayAsync();
                File.WriteAllText(Path.Combine(AppPaths.Dir, "plan-test.txt"), $"[{_engine.Claude?.Label ?? "none"}] {sw.Elapsed.TotalSeconds:0.0}s{Environment.NewLine}{text}");
            });
        if (e.Args.Contains("--recovery")) new RecoveryWindow(_engine, false).Show();
        if (e.Args.Contains("--brief")) new BriefWindow(_engine, "morning").Show();
        if (e.Args.Contains("--sweep")) new SweepWindow(_engine).Show();
        if (e.Args.Contains("--settings")) new SettingsWindow(_engine).Show();
        if (e.Args.Contains("--wizard")) new WizardWindow(AnchorSettings.Load()).Show();
        if (e.Args.Contains("--connect-google") && _engine.IsConnected("Google") is false && _engine.Providers.Contains("Google"))
            _ = Task.Run(async () =>
            {
                try { await _engine.ConnectAsync("Google", CancellationToken.None); await _engine.RefreshAllAsync(true); }
                catch (Exception ex) { _engine.SourceErrors["Google"] = ex.Message; }
            });
        if (!settings.SetupDone)
        {
            var configured = settings.Google.Enabled || settings.Microsoft.Enabled || settings.IcsFeeds.Count > 0;
            if (configured) { settings.SetupDone = true; try { settings.Save(); } catch { /* retry next launch */ } }   // someone who set things up by hand doesn't need the wizard
            else new WizardWindow(settings).Show();
        }
    }

    void RegisterHotkey()
    {
        var helper = new WindowInteropHelper(_main!);
        var hwnd = helper.EnsureHandle();
        HwndSource.FromHwnd(hwnd)!.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyId) { handled = true; new RecoveryWindow(_engine!, false).Show(); }
            return IntPtr.Zero;
        });
        var spec = _engine!.Settings.Hotkey;
        if (!TryParseHotkey(spec, out var mods, out var vk))
            _tray?.ShowBalloonTip(4000, "Anchor", $"Hotkey '{spec}' is not valid; use the tray menu for 'Where was I?'.", System.Windows.Forms.ToolTipIcon.Warning);
        else if (!RegisterHotKey(hwnd, HotkeyId, mods | ModNoRepeat, vk))
            _tray?.ShowBalloonTip(4000, "Anchor", $"{spec} is taken by another app. Change \"Hotkey\" in settings.json, or use the tray menu.", System.Windows.Forms.ToolTipIcon.Warning);
    }

    static bool TryParseHotkey(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var part in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win" or "windows": mods |= ModWin; break;
                default:
                    if (!Enum.TryParse<System.Windows.Forms.Keys>(part, true, out var k)) return false;
                    vk = (uint)k; break;
            }
        }
        return vk != 0 && mods != 0;
    }

    void BuildTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Anchor", null, (_, _) => ShowMain());
        menu.Items.Add("Where was I?", null, (_, _) => new RecoveryWindow(_engine!, false).Show());
        menu.Items.Add("Settings…", null, (_, _) => new SettingsWindow(_engine!).Show());
        menu.Items.Add("Setup wizard…", null, (_, _) => new WizardWindow(AnchorSettings.Load()).Show());
        menu.Items.Add("Test alert (meeting in 1 min)", null, (_, _) => _engine!.AddTestMeeting(1));
        var startup = new System.Windows.Forms.ToolStripMenuItem("Start with Windows") { Checked = IsAutoStart(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetAutoStart(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add("Exit", null, (_, _) => { _tray!.Visible = false; _engine?.Dispose(); Quit(); });

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(GetResourceStream(Ui.IconUri)!.Stream, System.Windows.Forms.SystemInformation.SmallIconSize),
            Text = "Anchor",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    void ShowMain() { _main!.Show(); _main.WindowState = WindowState.Normal; _main.Activate(); }

    internal static bool IsAutoStart() => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Anchor") is not null;

    internal static void SetAutoStart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)!;
        if (on) k.SetValue("Anchor", $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue("Anchor", throwOnMissingValue: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_main is not null) UnregisterHotKey(new WindowInteropHelper(_main).Handle, HotkeyId);
        _tray?.Dispose();
        base.OnExit(e);
    }
}
