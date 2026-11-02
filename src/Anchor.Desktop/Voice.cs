using System.Speech.Synthesis;

namespace Anchor.Desktop;

/// <summary>Text to speech through the voices installed in Windows.</summary>
public static class Voice
{
    public static IReadOnlyList<string> Installed()
    {
        try { using var s = new SpeechSynthesizer(); return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList(); }
        catch { return []; }
    }

    /// <summary>Speaks synchronously. Never throws: with no voice installed, the alert sound and window still do their job.</summary>
    public static void Say(string text, string voice, int rate)
    {
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
}
