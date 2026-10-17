using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Anchor.Core;

namespace Anchor.Desktop;

/// <summary>
/// Meeting alert. Standard: small corner card. Strong: large centred card, sound, must be acknowledged.
/// Maximum: as Strong, plus repeating sound/speech and re-raising itself until acknowledged.
/// </summary>
public sealed class AlertWindow : Window
{
    readonly AssistantEngine _engine;
    readonly DueAlert _alert;
    readonly string _intensity;
    readonly TextBlock _when = new();
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    int _ticks;
    bool _resolved;

    public AlertWindow(AssistantEngine engine, DueAlert alert)
    {
        _engine = engine;
        _alert = alert;
        _intensity = engine.Settings.AlertIntensity;
        var big = _intensity != "Standard";
        var e = alert.Event;

        Topmost = true;
        ShowInTaskbar = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = big ? 560 : 380;
        Background = (Brush)Application.Current.Resources["Card"];
        WindowStartupLocation = big ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        Title = "Anchor: " + e.Title;

        var urgent = alert.OffsetMinutes <= 1;
        var border = new Border
        {
            BorderThickness = new Thickness(big ? 4 : 2),
            BorderBrush = (Brush)Application.Current.Resources[urgent ? "Alarm" : "Accent"],
            Padding = new Thickness(big ? 32 : 18),
        };
        var stack = new StackPanel();
        border.Child = stack;

        stack.Children.Add(new TextBlock { Text = e.Title, FontSize = big ? 30 : 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        _when.FontSize = big ? 22 : 15;
        _when.Foreground = (Brush)Application.Current.Resources[urgent ? "Alarm" : "Accent"];
        stack.Children.Add(_when);
        stack.Children.Add(new TextBlock
        {
            Text = $"{e.Start.LocalDateTime:HH:mm}–{e.End.LocalDateTime:HH:mm}  ·  {e.Source}" + (e.Location is { Length: > 0 } l && !l.StartsWith("http") ? $"  ·  {l}" : ""),
            Foreground = (Brush)Application.Current.Resources["Dim"], Margin = new Thickness(0, 6, 0, 18),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (e.JoinUrl is { Length: > 0 } url)
        {
            var join = Btn("Join meeting", big);
            join.Background = (Brush)Application.Current.Resources["Accent"];
            join.Foreground = Brushes.Black;
            join.Click += (_, _) => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Resolve(ack: true); };
            buttons.Children.Add(join);
        }
        var snooze = Btn($"Snooze {_engine.Settings.SnoozeMinutes} min", big);
        snooze.Click += (_, _) => Resolve(ack: false);
        var ok = Btn("Got it", big);
        ok.Click += (_, _) => Resolve(ack: true);
        buttons.Children.Add(snooze);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        Content = border;

        Loaded += (_, _) =>
        {
            if (!big)
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth - 16;
                Top = wa.Bottom - ActualHeight - 16;
            }
            Refresh();
            Notify();
            Activate();
        };
        _clock.Tick += (_, _) => { Refresh(); _ticks++; if (_intensity == "Maximum") Repeat(); };
        _clock.Start();
        // Window close without a choice counts as snooze: never silently lose a meeting alert.
        Closed += (_, _) => { _clock.Stop(); if (!_resolved) _engine.Snooze(_alert, _engine.Settings.SnoozeMinutes); };
    }

    static Button Btn(string text, bool big) => new() { Content = text, FontSize = big ? 17 : 13, Padding = new Thickness(big ? 20 : 12, big ? 12 : 6, big ? 20 : 12, big ? 12 : 6) };

    void Refresh()
    {
        var now = DateTimeOffset.Now;
        _when.Text = AlertScheduler.Describe(_alert.Event, now);
        if (_alert.Event.Start <= now) _when.Foreground = (Brush)Application.Current.Resources["Alarm"];
    }

    void Notify()
    {
        if (_intensity != "Standard") SystemSounds.Exclamation.Play();
        Speak();
    }

    void Speak()
    {
        if (!_engine.Settings.SpeakAlerts) return;
        var text = $"{_alert.Event.Title} {AlertScheduler.Describe(_alert.Event, DateTimeOffset.Now)}";
        _ = Task.Run(() =>
        {
            try { using var s = new System.Speech.Synthesis.SpeechSynthesizer(); s.Speak(text); }
            catch { /* no voice installed: sound + visual still fire */ }
        });
    }

    void Repeat()
    {
        if (_ticks % 20 == 0) { SystemSounds.Exclamation.Play(); }
        if (_ticks % 60 == 0) { Speak(); Topmost = false; Topmost = true; Activate(); }
    }

    void Resolve(bool ack)
    {
        _resolved = true;
        if (ack) _engine.Acknowledge(_alert); else _engine.Snooze(_alert, _engine.Settings.SnoozeMinutes);
        Close();
    }
}
