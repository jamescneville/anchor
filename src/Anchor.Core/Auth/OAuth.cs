using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anchor.Core.Auth;

public sealed class TokenSet
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Per-provider tokens, encrypted with Windows DPAPI (current user).</summary>
public sealed class TokenStore
{
    readonly string _path;
    readonly object _gate = new();
    public TokenStore(string? path = null) => _path = path ?? AppPaths.TokensFile;

    Dictionary<string, TokenSet> Read()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, TokenSet>>(plain) ?? new();
        }
        catch { return new(); }
    }

    public TokenSet? Get(string provider) { lock (_gate) return Read().GetValueOrDefault(provider); }

    public void Set(string provider, TokenSet tokens)
    {
        lock (_gate)
        {
            var all = Read();
            all[provider] = tokens;
            var plain = JsonSerializer.SerializeToUtf8Bytes(all);
            File.WriteAllBytes(_path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
    }

    public void Remove(string provider)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.Remove(provider))
                File.WriteAllBytes(_path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(all), null, DataProtectionScope.CurrentUser));
        }
    }
}

public sealed class NotConnectedException(string provider) : Exception($"{provider} is not connected.");

public sealed record OAuthProvider(
    string Name,
    string AuthUrl,
    string TokenUrl,
    string ClientId,
    string? ClientSecret,
    string Scopes,
    IReadOnlyDictionary<string, string>? ExtraAuthParams = null);

/// <summary>Authorization-code + PKCE over a loopback listener; silent refresh afterwards.</summary>
public sealed class OAuthSession(HttpClient http, TokenStore store, OAuthProvider provider)
{
    readonly SemaphoreSlim _refreshLock = new(1, 1);
    public string Name => provider.Name;
    public bool IsConnected => store.Get(provider.Name) is { RefreshToken.Length: > 0 };

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var t = store.Get(provider.Name) ?? throw new NotConnectedException(provider.Name);
        if (t.ExpiresAt > DateTimeOffset.Now.AddMinutes(1)) return t.AccessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            t = store.Get(provider.Name) ?? throw new NotConnectedException(provider.Name);
            if (t.ExpiresAt > DateTimeOffset.Now.AddMinutes(1)) return t.AccessToken;
            if (string.IsNullOrEmpty(t.RefreshToken)) throw new NotConnectedException(provider.Name);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = t.RefreshToken,
                ["client_id"] = provider.ClientId,
                ["scope"] = provider.Scopes,
            };
            if (!string.IsNullOrEmpty(provider.ClientSecret)) form["client_secret"] = provider.ClientSecret;
            var fresh = await PostTokenAsync(form, ct);
            fresh.RefreshToken ??= t.RefreshToken; // Google omits it on refresh
            store.Set(provider.Name, fresh);
            return fresh.AccessToken;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task ConnectAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var port = FreePort();
        var redirect = $"http://localhost:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var q = new Dictionary<string, string>
        {
            ["client_id"] = provider.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["scope"] = provider.Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (provider.ExtraAuthParams is not null)
            foreach (var kv in provider.ExtraAuthParams) q[kv.Key] = kv.Value;

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        openBrowser($"{provider.AuthUrl}?{string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ctxTask = listener.GetContextAsync();
        var done = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, timeout.Token));
        if (done != ctxTask) throw new TimeoutException("Sign-in was not completed in time.");

        var ctx = await ctxTask;
        var query = ctx.Request.QueryString;
        var error = query["error"];
        var ok = error is null && query["state"] == state && query["code"] is not null;
        var html = ok
            ? "<html><body style='font-family:sans-serif'><h2>Anchor is connected.</h2>You can close this tab.</body></html>"
            : $"<html><body style='font-family:sans-serif'><h2>Sign-in failed</h2>{WebUtility.HtmlEncode(error ?? "state mismatch")}</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html";
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException($"{provider.Name} sign-in failed: {error ?? "state mismatch"}");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = query["code"]!,
            ["redirect_uri"] = redirect,
            ["client_id"] = provider.ClientId,
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrEmpty(provider.ClientSecret)) form["client_secret"] = provider.ClientSecret;
        store.Set(provider.Name, await PostTokenAsync(form, ct));
    }

    public void Disconnect() => store.Remove(provider.Name);

    async Task<TokenSet> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await http.PostAsync(provider.TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // invalid_grant on refresh means consent was revoked/expired: force a clean reconnect.
            if (body.Contains("invalid_grant")) store.Remove(provider.Name);
            throw new HttpRequestException($"{provider.Name} token endpoint {(int)resp.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new TokenSet
        {
            AccessToken = r.GetProperty("access_token").GetString()!,
            RefreshToken = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            ExpiresAt = DateTimeOffset.Now.AddSeconds(r.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3300),
        };
    }

    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));
        configure?.Invoke(req);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{provider.Name} GET {new Uri(url).AbsolutePath} -> {(int)resp.StatusCode}: {Truncate(body, 300)}");
        return JsonDocument.Parse(body);
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
