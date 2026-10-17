using System.Text.RegularExpressions;

namespace Anchor.Core;

/// <summary>Catches the paste mistakes that otherwise only show up as a confusing OAuth error in the browser.</summary>
public static partial class SettingsValidator
{
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Guid();

    [GeneratedRegex(@"^\d+-[a-z0-9]+\.apps\.googleusercontent\.com$")]
    private static partial Regex GoogleClientId();

    public static IReadOnlyList<string> Check(AnchorSettings s)
    {
        var problems = new List<string>();

        if (s.Microsoft.Enabled)
        {
            var id = s.Microsoft.ClientId.Trim();
            if (!Guid().IsMatch(id))
                problems.Add("Microsoft: the Application (client) ID must look like 12345678-abcd-1234-abcd-1234567890ab. " +
                             "Copy it from your Entra app registration's Overview page (not your email address).");
            var tenant = s.Microsoft.Tenant.Trim();
            if (tenant.Length == 0)
                problems.Add("Microsoft: Tenant can't be empty. Use your Directory (tenant) ID, or 'organizations'.");
            else if (tenant is not ("organizations" or "common" or "consumers") && !Guid().IsMatch(tenant))
                problems.Add("Microsoft: Tenant must be 'organizations', 'common', or a tenant ID like 12345678-abcd-1234-abcd-1234567890ab.");
        }

        if (s.Google.Enabled)
        {
            if (!GoogleClientId().IsMatch(s.Google.ClientId.Trim()))
                problems.Add("Google: the OAuth client ID must end in .apps.googleusercontent.com.");
            if (!s.Google.ClientSecret.Trim().StartsWith("GOCSPX-", StringComparison.Ordinal))
                problems.Add("Google: the client secret should start with GOCSPX-.");
        }

        if (s.Slack.Enabled && s.Slack.UserToken.Length > 0 && !s.Slack.UserToken.Trim().StartsWith("xoxp-", StringComparison.Ordinal))
            problems.Add("Slack: a user token starts with xoxp-.");

        return problems;
    }
}
