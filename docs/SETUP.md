# Anchor setup guide

Anchor is a Windows tray app that keeps you oriented: escalating meeting alerts, one agenda for all your calendars, a short "needs attention" list, day planning and a "Where was I?" button. Setup takes about ten minutes, and every step is optional.

## 1. Install

1. Run `Anchor-Setup-<version>.exe`. Windows asks for permission to install into Program Files.
   The installer isn't code-signed, so SmartScreen may say "unknown publisher": choose **More info**, then **Run anyway**.
2. Leave "Start Anchor when I sign in" ticked if you want it always running, and finish. Anchor appears in the tray (the anchor icon near the clock).

To remove it: Settings, Apps, Installed apps, Anchor, Uninstall. You'll be asked whether to also delete your settings and sign-ins.

## 2. First run: the setup wizard

The wizard opens by itself the first time. You can reopen it any time from the tray menu (**Setup wizard…**) or from Settings.

| Step | What it does | What you need |
|---|---|---|
| Google | Shows every Google calendar in the agenda and finds Gmail that needs a reply | A free Google "OAuth client" for yourself (below) |
| AI | Day plans, "Where was I?" and ranking, using the Claude account already signed in on this PC. No API key | Claude desktop, signed in |
| Outlook and Teams | Work calendar, mail and Teams follow-ups, read-only | Claude desktop with the Microsoft 365 connector (below) |
| Preferences | 12 or 24 hour clock, start with Windows, the "Where was I?" hotkey | Nothing |

### Google: getting a client ID and secret (about five minutes)

Google asks each app to have its own free "client". Make one for yourself:

1. Open <https://console.cloud.google.com/> and create a project (any name).
2. **APIs & Services, Library:** enable **Google Calendar API** and **Gmail API**.
3. **OAuth consent screen:** choose **External**, fill in the app name and your email, and add your own Google address under **Test users**.
4. **Credentials, Create credentials, OAuth client ID:** choose the type **Desktop app**.
5. Paste the **Client ID** (ends `.apps.googleusercontent.com`) and **Client secret** (starts `GOCSPX-`) into the wizard.

After you finish, your browser opens once so you can approve access. Google may say the app is "unverified": that's expected for your own project, so choose **Advanced**, then continue.

### Outlook and Teams (work accounts)

Work Microsoft accounts usually can't be connected straight to a personal tool, so Anchor uses Claude desktop as the bridge. It's read-only apart from one thing you control: messages you press **Trash** on in the inbox sweep are moved to Deleted Items (recoverable).

1. In Claude desktop: **Settings, Connectors**, connect **Microsoft 365**. Your organisation may need to approve it.
2. In the wizard (Outlook and Teams step), press **Copy setup prompt for Claude**.
3. In Claude desktop, start a new session in the **Code** tab, paste the prompt and send it. Claude creates two scheduled tasks: hourly on weekdays (7 AM to 6 PM) and four times a day at weekends.
4. Keep Claude desktop running. Anchor fills in on its own after the first sync. If Claude was closed at a scheduled time, the sync runs the next time it opens.

Work content is kept away from AI by default: work meetings and mail are redacted before anything reaches Claude. Turn on **Include work items** in Settings only if your organisation allows it.

## 3. Everyday use

- **Alerts:** meeting alerts escalate as the start time nears. Snooze or acknowledge from the alert window.
- **Where was I?** press the hotkey (default Ctrl+Space) from anywhere. Write down what you're doing and a note for your future self, then mark things Done.
- **Agenda:** All, Personal, Work, or side by side. In Settings, Calendars, set each calendar to **Full** (alerts and planning), **View only** (shown but ignored, e.g. a partner's shifts or sports) or **Hidden**.
- **Inbox sweep:** the envelope icon opens the sweep window. **Run sweep** reads the last 45 days of mail in the background and ranks every loose end. Open a message, add it to today's list, mark it handled, or Trash it. **Sync queue** shows Trash clicks waiting for the next sync, with **Put back** to cancel; **Cleared** lists what you've dealt with.
- **Today's list:** on the main window; type your own items or add them from the sweep.

## 4. Troubleshooting

- **Google says "client missing a project id" or "invalid_client":** the client ID or secret was pasted wrongly. Re-check them in Settings.
- **Google says access denied:** add your address under Test users on the OAuth consent screen.
- **Claude features say "not signed in":** open Claude desktop and sign in.
- **Nothing from Outlook or Teams:** check the Microsoft 365 connector is connected in Claude desktop, and that Claude is running. Scheduled tasks live in Claude desktop's **Scheduled** section, where you can run one on demand.
- **The hotkey does nothing:** another app may own it. Pick a different one in Settings.
- **Logs and data:** `%LOCALAPPDATA%\Anchor` (settings.json, state, crash.log). Deleting it resets Anchor.
