# ATOLL

A Dynamic Island for Windows. ATOLL sits at the top of your screen as a small pill that shows what is
happening right now (music, timers, Claude Code, notifications, volume) and opens into a panel when
you click it.

Built with C# and WPF on .NET 10 for Windows 10 (2004) and Windows 11.

## What it does

The island has three sizes:

- **Compact**: a small pill with the most important thing happening (the clock when nothing is).
- **Peek**: rest the pointer on it, or let an alert open it, for a bit more detail.
- **Expanded**: click it for a panel with one tab per feature.

When two things happen at once, the second shows in a small bubble next to the pill.

| Feature | What you see |
| --- | --- |
| **Media** | Album art and a live audio visualizer for anything using Windows media controls (Spotify, browsers, ...). Play/pause, skip, and click or drag the progress slider to seek when the player supports it. Peeks on track change. |
| **Clock & timer** | Clock when idle. Countdown timers, stopwatch and Pomodoro (25/5, long break every 4th) with alerts. |
| **Calendar** | Upcoming events from Google Calendar and Microsoft 365 (private ICS links) merged into one list, a next-event countdown pill, and a popup before events start. |
| **System** | Popups for volume, output device, battery, network, Bluetooth, brightness (laptops), Caps Lock and Num Lock. Volume slider in the panel. |
| **Claude** | Live Claude Code activity per session (thinking, editing a file, running a command), a popup when a session needs permission or finishes, and token usage for today and the current 5-hour window. Messages from the Claude desktop app get the Claude look. |
| **Notifications** | Windows notifications shown in the island, with history, per-app mute, and a privacy switch that hides message text. |
| **File shelf** | Drag files onto ATOLL to keep them handy, then drag them out to any app. |
| **Clipboard history** | Your last 25 copies (text, images, files). Click one to copy it again. |
| **System stats** | CPU, GPU, memory and network with one-minute graphs. |
| **Weather** | Current conditions and a 5-day forecast for your city. |
| **Quick actions** | Screenshot, mute microphone, lock, turn the screen off, open Downloads. |

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

20 built-in themes (including two frosted-glass themes) and your own saved themes, plus color,
opacity, size and roundness controls with a live preview. Frosted glass blurs what is behind the
island; while it is on, ATOLL is left out of screenshots and screen recordings (that is how it can
sample the screen behind itself).

## Getting started

### Requirements

- Windows 10 version 2004 or later, or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
- Windows SDK (only for the notifications setup below; `makeappx.exe` and `signtool.exe`)

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

### Claude Code

Open Settings › Claude Code and click **Install**. This adds ATOLL's hooks to
`~/.claude/settings.json` (your other settings and hooks are kept, and a backup is saved next to the
file). New Claude Code sessions then report what they are doing. **Remove** takes only ATOLL's
entries out again.

Usage totals work without the hooks: ATOLL reads the transcripts Claude Code already writes to
`~/.claude/projects`. Costs are estimates at API list prices, not what a subscription charges.

### Weather

Set your city in Settings › Modules › Weather.

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

Settings has seven pages: General, Appearance, Modules, Notifications, Claude Code, Calendar and
About. Every change applies immediately.

Settings, the shelf list and the log live in `%AppData%\Atoll`
(`settings.json`, `shelf.json`, `atoll.log`).

Launch options:

| Option | Effect |
| --- | --- |
| `--settings` or `--settings=<page>` | Open settings (page 0 = General ... 6 = About) |
| `--open=<module>` | Open ATOLL expanded on a tab: `media`, `clock`, `calendar`, `system`, `claude`, `notifications`, `shelf`, `clipboard`, `stats`, `weather`, `actions` |

## Privacy

ATOLL runs locally and has no account or telemetry.

- **Network**: the only outgoing requests are to [Open-Meteo](https://open-meteo.com) for weather
  (only when a city is set) and to the calendar servers you add in Settings › Calendar. Weather
  requests contain that city name and its coordinates; calendar requests contain only the ICS link
  itself.
- **Calendar links** are stored encrypted with your Windows login (DPAPI), so they can't be read from
  the settings file. Anyone with one of these links can read that calendar, so keep them private.
- **Clipboard history** stays in memory and is gone when ATOLL closes. Copies marked private by
  password managers are skipped.
- **The shelf** stores file locations only. ATOLL never copies, moves or deletes your files.
- **Notifications** are read only after you grant access, and stay on your PC.
- **Claude Code hooks** send a short summary of each event (session folder, tool name, file name,
  the first line of a command, Claude's notification text) to ATOLL over a local named pipe that
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
  Atoll.ClaudeHook/   tiny exe run by Claude Code hooks; forwards events over a named pipe
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
- **Claude sessions don't show up**: install the hooks, then start a new Claude Code session
  (running sessions keep their old hooks). If ATOLL moved to a new folder, click **Install** again.
- **Hook debugging**: set `ATOLL_HOOK_DEBUG=1` to make the hook print errors to stderr.
- **A calendar is missing events**: Google's ICS links can lag by hours. Check the link in Settings ›
  Calendar, and that the calendar is published (some M365 admins disable it).

## Roadmap

The feature set is complete. Open issues and ideas are tracked on GitHub.

## License

No license has been chosen yet. Until one is added, all rights are reserved by the author.
