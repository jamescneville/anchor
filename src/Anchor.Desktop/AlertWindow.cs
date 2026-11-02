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
        AllowsTransparency = true;          // rounded card with a soft shadow
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = big ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        Title = "Anchor: " + e.Title;

        var urgent = alert.OffsetMinutes <= 1;
        var tone = Ui.B(urgent ? "Alarm" : "Accent");
        var soft = Ui.B(urgent ? "AlarmSoft" : "AccentSoft");

        var stack = new StackPanel { MinWidth = big ? 520 : 340 };
        var chip = Ui.Chip(alert.OffsetMinutes == 0 ? "STARTING" : "MEETING", urgent ? "alarm" : "accent");
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        chip.Margin = new Thickness(0, 0, 0, big ? 16 : 10);
        stack.Children.Add(chip);
        stack.Children.Add(new TextBlock
        {
            Text = e.Title, FontSize = big ? 32 : 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"],
        });
        _when.FontSize = big ? 22 : 15;
        _when.FontWeight = FontWeights.SemiBold;
        _when.Foreground = tone;
        stack.Children.Add(_when);
        stack.Children.Add(new TextBlock
        {
            Text = $"{Fmt.Time(e.Start.LocalDateTime)}–{Fmt.Time(e.End.LocalDateTime)}  ·  {e.Source}" + (e.Location is { Length: > 0 } l && !l.StartsWith("http") ? $"  ·  {l}" : ""),
            Foreground = Ui.B("Dim"), Margin = new Thickness(0, 8, 0, big ? 24 : 14),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (e.JoinUrl is { Length: > 0 } url)
        {
            var join = Ui.Primary("Join meeting", big ? 24 : 14, big ? 12 : 7);
            join.FontSize = big ? 16 : 13;
            join.Click += (_, _) => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Resolve(ack: true); };
            buttons.Children.Add(join);
        }
        if (e.JoinUrl is not { Length: > 0 } && Anchor.Core.Bridge.SafeLink(e.OpenUrl) is { } open)
        {
            var openBtn = Ui.Primary("Open event", big ? 24 : 14, big ? 12 : 7);
            openBtn.FontSize = big ? 16 : 13;
            openBtn.Click += (_, _) => { Process.Start(new ProcessStartInfo(open) { UseShellExecute = true }); Resolve(ack: true); };
            buttons.Children.Add(openBtn);
        }
        var snooze = Ui.Secondary($"Snooze {_engine.Settings.SnoozeMinutes} min");
        snooze.Padding = new Thickness(big ? 20 : 12, big ? 12 : 7, big ? 20 : 12, big ? 12 : 7);
        snooze.FontSize = big ? 16 : 13;
        snooze.Click += (_, _) => Resolve(ack: false);
        var ok = Ui.Secondary("Got it");
        ok.Padding = snooze.Padding; ok.FontSize = snooze.FontSize; ok.Margin = new Thickness(0);
        ok.Click += (_, _) => Resolve(ack: true);
        buttons.Children.Add(snooze);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        var card = new Border
        {
            Background = Ui.B("Card"), BorderBrush = tone, BorderThickness = new Thickness(urgent ? 2 : 1),
            CornerRadius = new CornerRadius(big ? 22 : 16), Padding = new Thickness(big ? 36 : 22), Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 36, ShadowDepth = 0, Opacity = 0.65, Color = Colors.Black },
        };
        Content = new Border { Padding = new Thickness(28), Child = card };   // room for the shadow

        Loaded += (_, _) =>
        {
            if (!big)
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth + 4;
                Top = wa.Bottom - ActualHeight + 4;
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


    void Refresh()
    {
        var now = DateTimeOffset.Now;
        _when.Text = AlertScheduler.Describe(_alert.Event, now);
        if (_alert.Event.Start <= now) _when.Foreground = Ui.B("Alarm");
    }

    void Notify()
    {
        if (_intensity != "Standard") SystemSounds.Exclamation.Play();
        Speak();
    }

    void Speak()
    {
        if (!_engine.Settings.SpeakAlerts) return;
        var text = SpeechText.Phrase(_alert.Event.Title, _alert.Event.Start, DateTimeOffset.Now);
        var voice = _engine.Settings.SpeechVoice; var rate = _engine.Settings.SpeechRate;
        _ = Task.Run(() => Voice.Say(text, voice, rate));
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
