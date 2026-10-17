using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Anchor.Desktop;

/// <summary>Shared look: native dark title bar + Mica on Windows 11, and small builders for cards, chips and labels.</summary>
public static class Ui
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }

    public static Brush B(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Dark title bar, rounded corners and a Mica backdrop (Windows 11 22621+); harmless no-op elsewhere.</summary>
    public static void Style(Window w, bool mica = true)
    {
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
            if (!mica || Environment.OSVersion.Version.Build < 22621) return;
            int backdrop = 2;                                              // Mica
            if (DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) != 0) return;
            var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            if (HwndSource.FromHwnd(hwnd) is { } src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            // Window background becomes a translucent tint so Mica shows through.
            w.Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0E, 0x10, 0x14));
        };
        w.Background = B("Bg");
    }

    public static Border Card(UIElement child, double pad = 18) => new()
    {
        Background = B("Card"), BorderBrush = B("Line"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14), Padding = new Thickness(pad), Child = child,
    };

    public static TextBlock Label(string s) => new()
    {
        Text = s.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B("Dim"),
        Margin = new Thickness(0, 0, 0, 10), Opacity = 0.9,
    };

    public static TextBlock Dim(string s, double size = 12) => new() { Text = s, FontSize = size, Foreground = B("Dim") };

    /// <summary>Small rounded status pill. kind: ok | warn | alarm | accent | neutral.</summary>
    public static Border Chip(string text, string kind = "neutral", string? glyph = null)
    {
        var (bg, fg) = kind switch
        {
            "ok" => (B("OkSoft"), B("Ok")),
            "warn" => (B("WarnSoft"), B("Warn")),
            "alarm" => (B("AlarmSoft"), B("Alarm")),
            "accent" => (B("AccentSoft"), B("Accent")),
            _ => (B("CardRaised"), B("Dim")),
        };
        var tb = new TextBlock { Text = glyph is null ? text : glyph + "  " + text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = fg, TextWrapping = TextWrapping.NoWrap };
        return new Border { Background = bg, CornerRadius = new CornerRadius(20), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), Child = tb };
    }

    public static Button Primary(string text, double padH = 16, double padV = 8) =>
        new() { Content = text, Style = (Style)Application.Current.Resources["Primary"], Padding = new Thickness(padH, padV, padH, padV) };

    public static Button Ghost(string text) =>
        new() { Content = text, Style = (Style)Application.Current.Resources["Ghost"] };

    public static Button Secondary(string text) => new() { Content = text };
}
