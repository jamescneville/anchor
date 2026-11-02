using System.Diagnostics;
using System.IO;
using System.Speech.Synthesis;
using System.Text;
using Anchor.Core;

namespace Anchor.Desktop;

/// <summary>
/// Text to speech. Uses Piper (an offline neural voice) when it has been installed into Anchor's data folder
/// (see scripts\install-natural-voice.ps1), otherwise the voices installed in Windows.
/// </summary>
public static class Voice
{
    public const string Natural = "Natural (Piper)";

    static string Root => Path.Combine(AppPaths.Dir, "voices");
    static string PiperExe => Path.Combine(Root, "piper", "piper", "piper.exe");
    static string PiperModel => Path.Combine(Root, "models", "en_US-lessac-medium.onnx");
    public static bool NaturalAvailable => File.Exists(PiperExe) && File.Exists(PiperModel);

    public static IReadOnlyList<string> Installed()
    {
        var list = new List<string>();
        if (NaturalAvailable) list.Add(Natural);
        try { using var s = new SpeechSynthesizer(); list.AddRange(s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)); }
        catch { /* no Windows voices */ }
        return list;
    }

    /// <summary>Speaks synchronously. Never throws: with no voice, the alert sound and window still do their job.</summary>
    public static void Say(string text, string voice, int rate)
    {
        if (voice == Natural && NaturalAvailable && TryPiper(text, rate)) return;
        if (voice == Natural) voice = "";               // Piper missing or failed: fall back to the Windows default
        try
        {
            using var s = new SpeechSynthesizer();
            if (voice.Length > 0) { try { s.SelectVoice(voice); } catch { /* voice was removed: use the default */ } }
            s.Rate = Math.Clamp(rate, -10, 10);
            var b = new PromptBuilder();
            b.AppendText(text, PromptRate.Medium);
            s.Speak(b);
        }
        catch { /* no voice available */ }
    }

    static bool TryPiper(string text, int rate)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"anchor-voice-{Guid.NewGuid():N}.wav");
        try
        {
            var scale = Math.Pow(1.08, -Math.Clamp(rate, -10, 10) / 1.0);        // length_scale: above 1 is slower
            var psi = new ProcessStartInfo(PiperExe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(PiperExe)!,
            };
            foreach (var a in new[] { "--model", PiperModel, "--output_file", wav, "--length_scale", scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            _ = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();      // drain so it never blocks
            p.StandardInput.Write(text.Replace('\r', ' ').Replace('\n', ' ') + "\n");
            p.StandardInput.Close();
            if (!p.WaitForExit(20_000)) { try { p.Kill(true); } catch { } return false; }
            if (p.ExitCode != 0 || !File.Exists(wav) || new FileInfo(wav).Length < 1000) return false;
            using var player = new System.Media.SoundPlayer(wav);
            player.PlaySync();
            return true;
        }
        catch { return false; }
        finally { try { File.Delete(wav); } catch { } }
    }
}
