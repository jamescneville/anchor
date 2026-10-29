using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Anchor.Core.AI;

/// <summary>
/// Runs the local Claude Code CLI headless (claude -p) with the user's own Claude login: no API key, no window.
/// Each call is a single tool-less turn with no settings, MCP servers or session saved, so it is cheap and cannot act on anything.
/// </summary>
public sealed class ClaudeCliBackend(string exePath, ClaudeDesktopSettings s, BudgetTracker? budget = null) : AiBackend
{
    // One Claude process at a time: they share an OAuth token file, and parallel refreshes collide.
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public string ExePath => exePath;
    public override string Label => "Claude login";
    public override bool Allow(bool background) => budget?.Allow(background) ?? true;

    // ---------------------------------------------------------------- locating the CLI

    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var roots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // The Windows Store build keeps its CLI in the package's private copy of %APPDATA%.
        var pkgs = Path.Combine(local, "Packages");
        if (Directory.Exists(pkgs))
            foreach (var pkg in Directory.GetDirectories(pkgs, "Claude_*"))
                roots.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude-code"));
        // Claude desktop 4.29 and later keep it under %LOCALAPPDATA%\Claude-Data\claude_<package>\claude-code.
        var data = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(data))
            foreach (var d in Directory.GetDirectories(data, "claude_*"))
                roots.Insert(0, Path.Combine(d, "claude-code"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code"));

        // Layout: claude-code/<version>/<hash>/claude.exe (also accept claude-code/<version>/claude.exe).
        var best = roots.Where(Directory.Exists)
            .SelectMany(r => Directory.GetDirectories(r))
            .SelectMany(v => Directory.GetDirectories(v).Prepend(v).Select(d => (Ver: ParseVersion(Path.GetFileName(v)), Exe: Path.Combine(d, "claude.exe"))))
            .Where(x => File.Exists(x.Exe))
            .OrderByDescending(x => x.Ver).FirstOrDefault();
        if (best.Exe is not null) return best.Exe;

        // Newer Claude desktop builds no longer keep their own copy. Fall back to a standalone Claude Code install (the default
        // location of the native installer), then the copy bundled with the VS Code extension.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var standalone = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(standalone)) return standalone;
        var ext = Path.Combine(home, ".vscode", "extensions");
        if (Directory.Exists(ext))
        {
            var bundled = Directory.GetDirectories(ext, "anthropic.claude-code-*")
                .Select(d => (Ver: ParseVersion(Path.GetFileName(d).Split('-')[2]), Exe: Path.Combine(d, "resources", "native-binary", "claude.exe")))
                .Where(x => File.Exists(x.Exe)).OrderByDescending(x => x.Ver).FirstOrDefault();
            if (bundled.Exe is not null) return bundled.Exe;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var f = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(f)) return f; } catch { /* bad PATH entry */ }
        }
        return null;
    }

    /// <summary>Exposed for tests: picks the newest claude.exe under a claude-code folder.</summary>
    public static string? FindUnder(string claudeCodeRoot)
    {
        if (!Directory.Exists(claudeCodeRoot)) return null;
        var best = Directory.GetDirectories(claudeCodeRoot)
            .SelectMany(v => Directory.GetDirectories(v).Prepend(v).Select(d => (Ver: ParseVersion(Path.GetFileName(v)), Exe: Path.Combine(d, "claude.exe"))))
            .Where(x => File.Exists(x.Exe)).OrderByDescending(x => x.Ver).FirstOrDefault();
        return best.Exe;
    }

    static Version ParseVersion(string name) => Version.TryParse(name, out var v) ? v : new Version(0, 0);

    // ---------------------------------------------------------------- running it

    protected override async Task<string> CompleteAsync(string system, string user, AiTier tier, int maxTokens, CancellationToken ct)
    {
        var model = tier == AiTier.Fast ? s.FastModel : s.PlanModel;
        await OneAtATime.WaitAsync(ct);
        try
        {
            Exception? last = null;
            foreach (var delay in new[] { 0, 8, 20 })
            {
                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                try { return await RunOnceAsync(system, user, model, ct); }
                catch (TransientCliException ex) { last = ex; }
            }
            throw last!;
        }
        finally { OneAtATime.Release(); }
    }

    /// <summary>
    /// Runs one headless turn WITH the user's Microsoft 365 connector, restricted to the listed tools (everything else is denied).
    /// Used by the inbox sweep and for moving mail to Deleted Items. Needs the Claude login to have the connector connected.
    /// </summary>
    public async Task<string> RunWithConnectorAsync(string system, string user, string model, IEnumerable<string> connectorTools, TimeSpan timeout, CancellationToken ct)
    {
        var allowed = connectorTools.Select(t => "mcp__claude_ai_Microsoft_365__" + t).ToArray();
        await OneAtATime.WaitAsync(ct);
        try
        {
            Exception? last = null;
            foreach (var delay in new[] { 0, 8 })
            {
                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                try { return await RunOnceAsync(system, user, model, ct, allowed, timeout); }
                catch (TransientCliException ex) { last = ex; }
            }
            throw last!;
        }
        finally { OneAtATime.Release(); }
    }

    async Task<string> RunOnceAsync(string system, string user, string model, CancellationToken ct, string[]? connectorTools = null, TimeSpan? limit = null)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(), // no project files, CLAUDE.md or hooks to pick up
        };
        // Connector mode must not restrict --tools (the connector's tools are loaded on demand and a restricted set made newer CLIs print tool calls as text); dontAsk denies everything except the named tools. Plain mode has no tools at all.
        string[] args = connectorTools is null
            ? ["-p", "--output-format", "json", "--model", model, "--tools", "", "--system-prompt", system,
               "--no-session-persistence", "--disable-slash-commands", "--strict-mcp-config", "--setting-sources", ""]
            : ["-p", "--output-format", "json", "--model", model, "--system-prompt", system,
               "--no-session-persistence", "--permission-mode", "dontAsk", "--allowedTools", string.Join(",", connectorTools)];
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("ANTHROPIC_API_KEY");   // must use the Claude login, never silently bill an API key
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Claude.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit ?? TimeSpan.FromMinutes(3));
        try
        {
            await proc.StandardInput.WriteAsync(user.AsMemory(), timeout.Token);
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            var env = ParseEnvelope(await stdout, await stderr, proc.ExitCode);
            if (env.CostUsd > 0) budget?.RecordCost(env.CostUsd);
            return env.Text;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw ct.IsCancellationRequested ? new OperationCanceledException(ct) : new TimeoutException($"Claude took longer than {(limit ?? TimeSpan.FromMinutes(3)).TotalMinutes:0} minutes.");
        }
    }

    public sealed record Envelope(string Text, double CostUsd);

    /// <summary>Public for tests: the result text only.</summary>
    public static string ParseResult(string stdout, string stderr, int exitCode) => ParseEnvelope(stdout, stderr, exitCode).Text;

    /// <summary>Public for tests: turns the CLI's JSON envelope into text + reported cost, or a meaningful error.</summary>
    public static Envelope ParseEnvelope(string stdout, string stderr, int exitCode)
    {
        var json = stdout.Trim();
        var start = json.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(json[start..]);
                var r = doc.RootElement;
                var result = r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String ? res.GetString() ?? "" : "";
                var isError = r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                var cost = r.TryGetProperty("total_cost_usd", out var tc) && tc.ValueKind == JsonValueKind.Number && tc.TryGetDouble(out var cv) ? cv : 0;
                if (!isError && exitCode == 0) return new Envelope(result, cost);
                if (result.Contains("refresh", StringComparison.OrdinalIgnoreCase) && result.Contains("OAuth", StringComparison.OrdinalIgnoreCase))
                    throw new TransientCliException(result);
                if (result.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || result.Contains("/login", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Claude isn't signed in. Open the Claude app and sign in, then try again.");
                throw new InvalidOperationException(result.Length > 0 ? Clip(result) : Clip(stderr));
            }
            catch (JsonException) { /* fall through */ }
        }
        throw new InvalidOperationException(Clip(stderr.Length > 0 ? stderr : $"Claude exited with code {exitCode}."));
    }

    static string Clip(string s) => s.Length <= 240 ? s.Trim() : s[..240].Trim() + "…";

    /// <summary>Small real round trip for the Settings "Test" button.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        try
        {
            var text = (await CompleteAsync("Reply with exactly one word.", "Reply with the single word: ready", AiTier.Fast, 16, ct)).Trim();
            return (true, $"Works. Claude replied \"{text}\" using your Claude login.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

public sealed class TransientCliException(string message) : Exception(message);
