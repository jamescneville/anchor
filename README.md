# Anchor: ADHD-friendly personal assistant for Windows

A tray app that keeps you oriented: escalating meeting alerts, one merged agenda (Google incl. your
Skylight/home calendar, Outlook, optional ICS feeds), an inbox triage for Gmail/Outlook/Teams/Slack, an
AI day plan, and a "Where was I?" hotkey (default Ctrl+Space, change `Hotkey` in settings.json; note Ctrl+Space is IntelliSense in many editors).

Meeting alerts run entirely locally from a cached calendar. They keep working offline, after sleep/wake,
and when the AI budget is exhausted. Claude is only used for triage, planning and recovery text.

## Run

```
dotnet run --project src/Anchor.Desktop
dotnet test
```

First launch opens **Settings** (also in the tray menu). Everything below is editable there; it saves to
`%LOCALAPPDATA%\Anchor\settings.json` and restarts Anchor. Tray menu: Settings, Test alert, Start with Windows, Exit.

Install for daily use: `scripts\publish.ps1` (self-contained single exe in `dist\`) then `scripts\install.ps1`
(copies to `%LOCALAPPDATA%\Programs\Anchor`, adds a Start Menu shortcut, starts with Windows).

Other behaviour: morning plan at workday start and a wrap-up 30 min before it ends (weekdays); in-person
meetings (a physical location, no join link) get an extra early alert (`InPersonLeadMinutes`, default 30).
Use **Test alert** first to see what a meeting alert looks like.

## Connect things (all optional, each independent)

| Source | What you do | Settings |
|---|---|---|
| **Claude** | Paste your key in Settings (stored encrypted via DPAPI), or set env `ANTHROPIC_API_KEY` | `Ai.*` (budget, models) |
| **Google** (Gmail + all calendars, incl. Skylight's) | Google Cloud Console → new project → enable Gmail API + Calendar API → OAuth consent (External, add yourself as test user) → Credentials → OAuth client ID, type **Desktop app** | `Google.Enabled/ClientId/ClientSecret`, then press *Connect google* |
| **Outlook / Teams** | Entra ID → App registrations → New, "Accounts in any org directory", platform **Mobile and desktop**, redirect `http://localhost`, delegated perms `Calendars.Read Mail.Read Chat.Read User.Read` | `Microsoft.Enabled/ClientId`, then *Connect microsoft* |
| **Slack** | api.slack.com/apps → new app → User Token Scope `search:read` → install → copy `xoxp-` token | env `ANCHOR_SLACK_TOKEN` (preferred) or `Slack.UserToken` |
| **Work calendar fallback** | Outlook on the web → Settings → Shared calendars → Publish → copy ICS link | `IcsFeeds: [{Name, Url}]` |

Skylight has no public API; it mirrors a Google/Outlook/iCloud calendar. Anchor reads that calendar directly.

## Autodesk / work-data guardrails

- Teams `Chat.Read` and Outlook consent frequently need tenant-admin approval. If blocked, the source shows a
  warning and everything else keeps working; use the ICS fallback for the calendar.
- By default **no Outlook/Teams/Slack content is sent to Claude** (`Ai.SendWorkContentToClaude: false`). Those
  items are still surfaced using local keyword heuristics. Flip it only after you have approval to send work
  data to Anthropic, and use an approved/enterprise API key, not a personal one.
- Only sender, subject and a 300-char snippet are sent for triage. Nothing is sent that you have not enabled.
- No screenshots, keystroke capture or window tracking. Focus support is a manual current task, a check-in
  prompt every `CheckInMinutes` during work hours, and the recovery hotkey.

## Claude login: AI without an API key

If no Anthropic API key is saved, Anchor uses the Claude account already signed in on this PC by running the local Claude Code CLI
headless (`claude -p`): no window opens and nothing needs approving. Each call is one tool-less turn with no MCP servers, settings or
saved session, so it is cheap and can't act on anything. It powers **Plan my day**, the morning/evening briefs, **Where was I?** and
automatic inbox triage, and the results appear in Anchor (a "Claude is working..." chip shows while it runs). The CLI is found
automatically (Windows Store build or a normal install); override with `ClaudeDesktop.CliPath`. If an API key is saved it takes priority.

- Work items are redacted unless you switch on *Include work items* (Settings → Claude login). The API path has its own switch.
- Only one Claude process runs at a time and token-refresh collisions with the desktop app are retried automatically.
- **Optional file bridge:** anything that writes `plan.md` and/or `tasks.json` into `%LOCALAPPDATA%/Anchor/bridge` (for example a
  scheduled Claude task) shows up in the Plan card and Needs attention. Anchor only reads these files, treats them as untrusted text and
  only opens http(s) links. `tasks.json` is `[{"id","title","source","from","link","due","confidence"}]`.

## Redaction before AI

With `Ai.SendWorkContentToClaude` off (default), **nothing from work sources is put in an AI prompt**: mail/chat triage skips
Outlook/Teams/Slack, and day plans / recovery replace work meetings with "Busy (work meeting)" (times kept, title/location/link
dropped) and collapse work follow-ups into a count. Work sources are Outlook, Teams, Slack and ICS feeds not marked `| personal`.
Work items still appear locally in the agenda and attention list, and meeting alerts are unaffected.

## Cost control

Triage uses Haiku 5.5 (thinking off, batches of 15). Day plans use Sonnet 5.5. Prices are in
`Ai.PricePerMTok`; verify against current pricing. Daily soft cap ($0.60) stops background triage only;
warning at $15, hard local cutoff at $19. This is a local estimate: also set a spend limit in the Anthropic
Console for a real ceiling.

## Layout

```
src/Anchor.Core      alert scheduler, state, OAuth+PKCE, Google/Microsoft/Slack/ICS connectors, Claude service, engine
src/Anchor.Desktop   WPF: tray, main window, alert window, recovery window, hotkey, power events
tests/Anchor.Tests   scheduler (sleep/wake, snooze, reschedule), ICS recurrence, budget, parsing, dedupe
```

## Status / known limits

- Connectors are written against the documented Google, Graph and Slack APIs but have **not been run against live
  accounts** (no credentials in the build environment). Expect small fixes on first real connect.
- Alert UI was smoke-launched but not visually tested here; try **Test alert**.
- Slack uses search (DMs + @mentions) rather than channel history.
- ICS recurrence supports DAILY/WEEKLY(BYDAY)/MONTHLY/YEARLY only.
- Read-only everywhere: Anchor never sends, replies or edits anything.
