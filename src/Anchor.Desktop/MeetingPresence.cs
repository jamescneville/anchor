using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Anchor.Core;

namespace Anchor.Desktop;

/// <summary>
/// "Am I already in this meeting?" True when a window of a conferencing app (Teams, Zoom, Webex, Slack, or a browser for Meet / Teams on the web)
/// has the meeting's name in its title. Used only to stop reminding about a meeting the user is already in.
/// </summary>
public static class MeetingPresence
{
    static readonly string[] Apps = ["ms-teams", "teams", "zoom", "webex", "ciscocollabhost", "slack", "chrome", "msedge", "firefox", "brave", "vivaldi", "opera"];

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static bool InMeeting(CalendarEvent e)
    {
        var name = SpeechText.Clean(e.Title).ToLowerInvariant();
        if (name.Length < 4 || name == "your meeting") return false;
        var found = false;
        var names = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            if (GetWindowText(h, sb, 256) == 0) return true;
            var title = sb.ToString().ToLowerInvariant();
            // Zoom's in-meeting window has a fixed title; everything else must carry the meeting's name.
            var zoomRoom = title is "zoom meeting" or "zoom webinar";
            if (!zoomRoom && !title.Contains(name, StringComparison.Ordinal)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!names.TryGetValue(pid, out var proc))
            {
                try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { proc = ""; }
                names[pid] = proc;
            }
            if (Apps.Contains(proc) && (!zoomRoom || e.JoinUrl?.Contains("zoom", StringComparison.OrdinalIgnoreCase) == true)) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
