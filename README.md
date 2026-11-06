# ATOLL

A Dynamic Island for Windows. ATOLL sits at the top of your screen as a small pill that shows what is
happening right now (music, timers, coding agents, notifications, volume) and opens into a panel when
you click it.

Built with C# and WPF on .NET 10 for Windows 10 (2004) and Windows 11.

## What it does

The island has three sizes:

- **Compact**: a small pill with the most important thing happening (the clock when nothing is).
- **Peek**: rest the pointer on it, or let an alert open it, for a bit more detail.
- **Expanded**: click it for seven grouped tabs: Agents, Media, Notifications, Clock & calendar,
  System, Files, and Weather. Individual modules can still be enabled, disabled, and reordered in Settings.

When two things happen at once, the second shows in a small bubble next to the pill.

| Feature | What you see |
| --- | --- |
| **Media** | Album art, live audio visualizer, play/pause, skip, seek, and the system volume slider. Seeking is available when the player supports it. Peeks on track change. |
| **Clock & calendar** | Clock, countdown timers, stopwatch, Pomodoro, and upcoming Google Calendar/Microsoft 365 events through private ICS links. Timer and event alerts still appear in the island. |
| **System** | Device status, CPU/GPU/memory/network graphs, and quick actions: screenshot, microphone mute, lock, screen off, Downloads. System popups still report volume, battery, network, Bluetooth, brightness, and lock keys. |
| **Agents** | Claude Code, Codex, OpenCode, and Hermes activity and completion/attention alerts, labeled by agent and project. Token/cost totals currently cover Claude Code only. |
| **Notifications** | Windows notifications shown in the island, with history, per-app mute, and a privacy switch that hides message text. |
| **Files** | A file shelf for drag-and-drop alongside your last 25 clipboard entries (text, images, files). Click an entry to copy it again. |
| **Weather** | Current conditions, wind and gusts, rain timing, and a selectable 7-day forecast with hourly precipitation and wind. |

### Fits around your work

- **Click-through**: when a maximized or fullscreen window is behind ATOLL (a browser's tab strip,
  say), your clicks go to that window and ATOLL fades while the pointer passes. Rest the pointer on
  it to use it.
- **Fullscreen apps**: by default ATOLL hides over fullscreen video and presentations and only pops
  up for alerts. You can also keep it visible, dim it to 50%, or hide it completely.
- **Games**: ATOLL always hides over games. A game is a fullscreen or maximized app that runs in
  exclusive fullscreen, is in Xbox Game Bar's list of games, or is installed in a Steam, Epic, Riot,
  GOG, Xbox, EA or Ubisoft library folder.
- **Multiple displays**: show on the main display, every display, or the ones you pick.

### Looks

20 built-in themes (including two frosted-glass themes) and your own saved themes, plus a visual
color picker with RGB, opacity and hex controls. Size and roundness update in a live preview. Frosted glass blurs what is behind the
island; while it is on, ATOLL is left out of screenshots and screen recordings (that is how it can
sample the screen behind itself).

## Getting started

### Requirements

- Windows 10 version 2004 or later, or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
- Windows SDK for notification setup packages and release builds (`makeappx.exe` and `signtool.exe`)

### Build and run

```powershell
dotnet build Atoll.sln
dotnet test Atoll.sln
.\src\Atoll.App\bin\Debug\net10.0-windows10.0.19041.0\Atoll.exe
```

ATOLL appears at the top center of your main display and puts an **A** icon in the tray.
Left-click the tray icon for settings; right-click it to hide ATOLL or quit.

There is no installer yet; see [Roadmap](#roadmap).

### Install

Download `Atoll-Setup.exe` from the latest [GitHub release](https://github.com/Mazon3442/Atoll/releases)
and run it. It installs to your user folder with no admin prompt and adds a Start menu entry; uninstall
from Windows Settings › Apps. ATOLL checks the repo for new releases in the background, downloads only
the changed parts, and applies the update the next time it restarts.

To build a release yourself:

```powershell
.\packaging\build-release.ps1 -Version 1.0.0   # produces Setup.exe, update packages and the feed
.\packaging\build-release.ps1 -Version 1.0.1 -Publish   # also attach them to a GitHub release (needs VPK_TOKEN)
```

The Setup.exe is not code-signed, so Windows shows "Windows protected your PC" on first run — click
**More info › Run anyway**. Removing that prompt means buying a code-signing certificate; it is fine
to skip for personal use.

### Notifications (one-time setup)

Windows only lets apps with a *package identity* read notifications. ATOLL gets one from a small
signed package registered next to `Atoll.exe`:

Open **Settings › Notifications** and click **Set up notifications**. Approve the Windows
administrator prompt, restart ATOLL using the button shown, then click **Allow access**.
The page checks access again when you return from Windows Settings. **Preview in the island**
checks the display; receive a notification from another app to check the Windows connection.
Release builds include the signed setup files. For a source build, prepare them once:

```powershell
# Builds and signs packaging\out\Atoll.Identity.msix. Creates a "CN=Atoll Dev" signing
# certificate in your personal certificate store the first time. Changes no trust settings.
powershell -ExecutionPolicy Bypass -File packaging\build-identity.ps1

# Asks for admin once. Trusts only that certificate (Local Machine > Trusted People)
# and registers the package for the build folder.
powershell -ExecutionPolicy Bypass -File packaging\setup-identity.ps1
```

Then restart ATOLL and click **Allow access** in Settings › Notifications. To undo everything, run
`packaging\remove-identity.ps1`.

### Coding agents

Open Settings › Agents and click **Install / update** for each agent you use, then restart its
running sessions. Existing hooks and settings are kept, and changed files are backed up.

| Agent | Integration |
| --- | --- |
| Claude Code | Command hooks in `~/.claude/settings.json`. |
| Codex | Command hooks in `$CODEX_HOME/hooks.json` (default `~/.codex/hooks.json`). Enable hooks and review/trust the new definitions in Codex Settings › Hooks, the composer's Review hooks control, or `/hooks` in the CLI. ATOLL never bypasses that review. |
| OpenCode | A local JavaScript plugin in `~/.config/opencode/plugins/atoll.js` (respects `XDG_CONFIG_HOME` and `OPENCODE_CONFIG_DIR`). |
| Hermes | A native `atoll` plugin under `$HERMES_HOME/plugins/atoll` (default `%LocalAppData%/hermes` on Windows). Setup runs `hermes plugins enable atoll`; if the command cannot run, the settings page shows the manual command. |

**Remove** unregisters only ATOLL's integration. Adapters send short local activity summaries and
remain silent if ATOLL is closed. They never approve, deny, or alter an agent's tool calls.
Agent integrations must run on the same Windows machine as ATOLL; remote/WSL agent environments
are not bridged automatically.

The adapters use the documented [Codex hooks](https://learn.chatgpt.com/docs/hooks),
[OpenCode plugins](https://opencode.ai/docs/plugins/), and
[Hermes plugin hooks](https://hermes-agent.nousresearch.com/docs/user-guide/features/hooks).

Usage totals work without the hooks: ATOLL reads the transcripts Claude Code already writes to
`~/.claude/projects`. Costs are estimates at API list prices, not what a subscription charges.

### Weather

Open **Settings › Weather**. Click **Use my location** to request Windows location access,
or search for a city and choose the correct region from the results. Windows coordinates are
rounded to two decimal places before being stored or sent to Open-Meteo; ATOLL does not track
your location in the background. If Windows location is unavailable, city search still works.

Select a forecast day to see hourly conditions, precipitation chances and amounts, and wind.
Forecast times use the selected place's local time. Celsius uses km/h and millimeters;
Fahrenheit uses mph and inches. Forecasts refresh every 20 minutes or when you click **Refresh**.
Rain timing is a forecast, not an alert service.

### Calendar

Open Settings › Calendar and add each calendar's private ICS link. Google: Calendar settings → the
calendar → "Secret address in iCal format". Outlook / M365: Settings → Calendar → Shared calendars →
"Publish a calendar" → copy the ICS link. Give each one a name and color; ATOLL re-downloads them
every 15 minutes.

Calendar is read-only: ATOLL can't create, accept or edit events. Recurring events (weekly, every 2nd
Tuesday, ...) are expanded correctly, including skipped and moved occurrences. Google's links can lag
— changes sometimes take hours to appear, which is on Google's end. Some M365 admins disable calendar
publishing, so "Publish a calendar" may be missing for work accounts.

## Settings

Settings has eight pages: General, Appearance, Modules, Notifications, Agents, Calendar, Weather and
About. Every change applies immediately.

In Modules, drag a handle, choose a position number, or use Alt + Up/Down on a focused handle
to reorder features. Related features share a tab at the first enabled feature's position.

Settings, the shelf list and the log live in `%AppData%\Atoll`
(`settings.json`, `shelf.json`, `atoll.log`).

Launch options:

| Option | Effect |
| --- | --- |
| `--settings` or `--settings=<page>` | Open settings (page 0 = General ... 6 = About) |
| `--open=<module>` | Open a grouped tab: `agents`, `media`, `notifications`, `clock`, `system`, `files`, `weather`. Existing names (`claude`, `calendar`, `shelf`, `clipboard`, `stats`, `actions`) still open their containing group. |

## Privacy

ATOLL runs locally and has no account or telemetry.

- **Network**: weather searches send a city name to [Open-Meteo](https://open-meteo.com), and forecasts
  send the selected coordinates. Choosing Windows location requests an approximate position through
  Windows; ATOLL does not track movement. Calendar downloads use the ICS links you add. Installed
  copies also check the project's GitHub releases for updates.
- **Calendar links** are stored encrypted with your Windows login (DPAPI), so they can't be read from
  the settings file. Anyone with one of these links can read that calendar, so keep them private.
- **Clipboard history** stays in memory and is gone when ATOLL closes. Copies marked private by
  password managers are skipped.
- **The shelf** stores file locations only. ATOLL never copies, moves or deletes your files.
- **Notifications** are read only after you grant access, and stay on your PC.
- **Agent adapters** send a short summary of each event (agent, session folder, tool name, file name,
  the first line of a command, or notification text) to ATOLL over a local named pipe that
  only your user account can open. File contents are never sent, and nothing leaves your PC.

## How it works

```
src/
  Atoll.App/          the app (WPF)
    Shell/            island window, animations, click-through, glass, fullscreen and game detection
    Activities/       ActivityHost: what is shown, by priority, with expiry
    Modules/<Name>/   one folder per feature: module, view models, XAML templates
    Settings/         settings model, store, settings window
    Theme/            theme tokens, presets, shared styles
    Updates/          checks GitHub releases and downloads updates in the background
  Atoll.ClaudeHook/   shared agent bridge exe (legacy name retained for installed Claude hooks)
  Shared/             hook message format shared by both
packaging/            notification identity package and scripts
tests/Atoll.Tests/    xUnit tests
```

- Each feature is an `IIslandModule`. It posts **activities** (a view model plus a priority and an
  optional duration) to the `ActivityHost`, and optionally provides a panel for the expanded view.
- The island picks the highest-priority activity and renders it with a DataTemplate named
  `{ViewModelType}.Compact`, `.Peek`, `.Mini` or `.Panel`.
- The island window is a fixed, transparent, always-on-top layered window; only the pill inside it
  animates, so clicks outside the pill reach the desktop.
- The pointer is tracked by polling the cursor position, because a click-through window gets no
  mouse events.

## Troubleshooting

- **Something is wrong**: check `%AppData%\Atoll\atoll.log`.
- **ATOLL isn't in my screenshot**: a frosted-glass theme is on; that is expected.
- **The visualizer doesn't move**: it only runs while media is playing.
- **Notifications say "one-time setup needed"**: run the two packaging scripts above, then restart
  ATOLL.
- **Agent sessions don't show up**: install that agent's adapter in Settings › Agents and restart
  running sessions. For Codex, review/trust ATOLL's hooks as well. If ATOLL moved, use **Install / update** again.
- **Hook debugging**: set `ATOLL_HOOK_DEBUG=1` to make the hook print errors to stderr.
- **A calendar is missing events**: Google's ICS links can lag by hours. Check the link in Settings ›
  Calendar, and that the calendar is published (some M365 admins disable it).

## Roadmap

The feature set is complete. Open issues and ideas are tracked on GitHub.

## License

No license has been chosen yet. Until one is added, all rights are reserved by the author.
