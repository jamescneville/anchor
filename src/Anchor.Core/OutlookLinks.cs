using System.Web;

namespace Anchor.Core;

/// <summary>
/// The webLink Graph returns for a message (outlook.office365.com/owa/?ItemID=...&amp;viewmodel=ReadMessageItem) often fails to load
/// in the current Outlook (a "can't reach this page" error). The deeplink form below opens the same message reliably, so mail
/// links are rewritten to it using the item id already inside the original link.
/// </summary>
public static class OutlookLinks
{
    public static string Normalize(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var u)) return link;
        if (!(u.Host.EndsWith(".office.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".office365.com", StringComparison.OrdinalIgnoreCase))) return link;

        var q = HttpUtility.ParseQueryString(u.Query);
        var id = q["ItemID"] ?? q["itemid"];
        if (string.IsNullOrEmpty(id)) return link;

        var path = q["path"] ?? "";
        var view = q["viewmodel"] ?? "";
        var isCalendar = path.Contains("calendar", StringComparison.OrdinalIgnoreCase) || view.Contains("Calendar", StringComparison.OrdinalIgnoreCase);
        if (isCalendar) return link;                                  // the mail form is not verified for events: leave those as given

        var e = Uri.EscapeDataString(id);
        return $"https://outlook.office.com/mail/deeplink/read/{e}?ItemID={e}&exvsurl=1";
    }
}
