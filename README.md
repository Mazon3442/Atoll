<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/brand/banner-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="docs/assets/brand/banner-light.svg">
  <img alt="ATOLL: a Dynamic Island for Windows" src="docs/assets/brand/banner-dark.svg" width="100%">
</picture>

**Music, timers, coding agents, notifications, calendar and weather, in a pill at the top of your screen.<br>Click it and it opens into a panel. Runs on your own PC, with no account and no telemetry.**

[![Release](https://img.shields.io/github/v/release/Mazon3442/Atoll?style=flat-square&color=2FD3E6&label=release)](https://github.com/Mazon3442/Atoll/releases/latest)
[![Platform: Windows 10 and 11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-555?style=flat-square&logo=windows&logoColor=white)](#install)
[![Built with .NET 10 and WPF](https://img.shields.io/badge/built%20with-.NET%2010%20%2B%20WPF-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![Downloads](https://img.shields.io/github/downloads/Mazon3442/Atoll/total?style=flat-square&color=555&label=downloads)](https://github.com/Mazon3442/Atoll/releases)

[Features](#features) · [Install](#install) · [Quick start](#quick-start) · [Using ATOLL](#using-atoll) · [Privacy](#privacy) · [How it works](#how-it-works) · [Build from source](#build-from-source) · [Releases](#releases)

</div>

## Features

The island has three sizes:

- **Compact**: a small pill with the most important thing happening (the clock when nothing is).
- **Peek**: rest the pointer on it, or let an alert open it, for a bit more detail.
- **Expanded**: click it for seven grouped tabs: Agents, Media, Notifications, Clock & calendar,
  System, Files and Weather. Each module can be turned on or off and reordered in Settings.

Up to two smaller side bubbles keep other active modules within reach, one on each side of the island. Click either to open its tab.

| Feature | What it does |
| --- | --- |
| **Media** | Album art, a live audio visualizer, play/pause, skip, seek and the system volume slider. Seeking works when the player supports it. Peeks on track change. |
| **Clock & calendar** | Clock, countdown timers, stopwatch, Pomodoro, and upcoming Google Calendar and Microsoft 365 events through private ICS links. Pin an event to keep it in the island, or dismiss one occurrence. |
| **Agents** | Claude Code, Codex, OpenCode and Hermes activity, with completion and attention alerts labeled by agent and project. Token and cost totals cover Claude Code. |
| **Notifications** | Windows notifications mirrored into the island, with sender icons, history, per-app mute, and a privacy switch that hides message text. |
| **System** | Device status, CPU/GPU/memory/network graphs, and quick actions: screenshot, microphone mute, lock, screen off, Downloads. Popups for volume, battery, network, Bluetooth, brightness and lock keys. |
| **Files** | A drag-and-drop file shelf next to your last 25 clipboard entries (text, images, files). Click an entry to copy it again. |
| **Weather** | Current conditions, high and low, wind and gusts, rain timing, and a 7-day forecast with an hourly precipitation, wind or temperature chart you can step through with the keyboard. |
| **Updates itself** | Installed copies check GitHub releases in the background, download only what changed, and apply the update the next time ATOLL starts. |

### Fits around your work

- **Click-through.** When a maximized or fullscreen window is behind ATOLL (a browser's tab strip,
  say), clicks go to that window and ATOLL fades while the pointer passes. Rest the pointer on it to
  use it.
- **Fullscreen apps.** ATOLL hides over fullscreen video and presentations and pops up only for
  alerts. Hold the pointer at the top center of the display for about three seconds to bring it
  back for a moment. You can also keep it visible, dim it to 50%, or hide it.
- **Games.** ATOLL hides over games until you use the same edge hold. A game is a fullscreen or
  maximized app that runs in exclusive fullscreen, is in Xbox Game Bar's list of games, or is
  installed in a Steam, Epic, Riot, GOG, Xbox, EA or Ubisoft library folder.
- **Multiple displays.** Show it on the main display, every display, or the ones you pick.
- **Keyboard and motion.** Panels, charts and settings work from the keyboard, and animations
  follow Windows' reduced-motion setting.

### Looks

20 built-in themes (two of them frosted glass) and your own saved themes, with a color picker
(RGB, opacity and hex). Size and roundness update in a live preview. Frosted glass blurs what is
behind the island; while it is on, ATOLL is left out of screenshots and screen recordings (that is
how it can sample the screen behind itself).

## Install

Download **`Atoll-win-Setup.exe`** from the [latest release](https://github.com/Mazon3442/Atoll/releases/latest)
and run it. It installs to your user folder with no admin prompt and adds a Start menu entry.
Uninstall from Windows Settings › Apps.

| | |
| --- | --- |
| **Needs** | Windows 10 version 2004 or later, or Windows 11, on x64 |
| **Runtime** | Included; nothing else to install |
| **Portable** | `Atoll-win-Portable.zip` on the same release runs without installing |

> [!NOTE]
> The installer isn't code-signed yet, so the first run shows "Windows protected your PC". Click
> **More info › Run anyway**.

## Quick start

1. Start ATOLL. It appears at the top center of your main display and puts its icon in the tray.
2. Play some music, start a timer, or rest the pointer on the pill to see it peek.
3. Click the pill to open the panel; click a tab to switch.
4. Left-click the tray icon for Settings; right-click it to hide ATOLL or quit.

Then set up the parts you want:

- **Notifications**: Settings › Notifications › **Set up notifications** ([details](#notifications)).
- **Coding agents**: Settings › Agents › **Install** next to each agent you use ([details](#coding-agents)).
- **Calendar**: Settings › Calendar, paste a private ICS link ([details](#calendar)).
- **Weather**: Settings › Weather › **Use my location** or search for a city ([details](#weather)).

## Using ATOLL

### Notifications

Windows only lets apps with a *package identity* read notifications. ATOLL gets one from a small
signed package registered next to `Atoll.exe`.

Open **Settings › Notifications** and click **Set up notifications**. Approve the Windows
administrator prompt, restart ATOLL with the button shown, then click **Allow access**. The page
checks access again when you come back from Windows Settings. **Preview in the island** checks the
display; a notification from another app checks the Windows connection.

Release builds include the signed setup files. For a source build, see
[Notification identity for source builds](#notification-identity-for-source-builds).

### Coding agents

Open **Settings › Agents** and click **Install** for an agent you use. An older adapter shows
**Update**; a current one shows **Installed**. Restart running sessions after changes. Existing
hooks and settings are kept, and changed files are backed up.

| Agent | Integration |
| --- | --- |
| **Claude Code** | Command hooks in `~/.claude/settings.json`. |
| **Codex** | Command hooks in `$CODEX_HOME/hooks.json` (default `~/.codex/hooks.json`). Enable hooks and review/trust the new definitions in Codex Settings › Hooks, the composer's Review hooks control, or `/hooks` in the CLI. ATOLL never bypasses that review. |
| **OpenCode** | A local JavaScript plugin in `~/.config/opencode/plugins/atoll.js` (respects `XDG_CONFIG_HOME` and `OPENCODE_CONFIG_DIR`). |
| **Hermes** | A native `atoll` plugin under `$HERMES_HOME/plugins/atoll` (default `%LocalAppData%/hermes`). Setup runs `hermes plugins enable atoll`; if that can't run, the page shows the command. |

**Remove** unregisters only ATOLL's integration. Adapters send short local activity summaries and
stay silent if ATOLL is closed. They never approve, deny or change an agent's tool calls. Agents
must run on the same Windows machine as ATOLL; remote and WSL environments aren't bridged.

The adapters use the documented [Codex hooks](https://learn.chatgpt.com/docs/hooks),
[OpenCode plugins](https://opencode.ai/docs/plugins/) and
[Hermes plugin hooks](https://hermes-agent.nousresearch.com/docs/user-guide/features/hooks).

Usage totals work without the hooks: ATOLL reads the transcripts Claude Code already writes to
`~/.claude/projects`. Costs are estimates at API list prices, not what a subscription charges.

### Calendar

The Clock & calendar tab shows the clock and timers on the left and the next 14 days of events on
the right. Today's events show their title, then their time and place in your system's time format;
the event on now is marked **Now** and the one after it counts down. Later days take one line per
event, with the start time, so more of the fortnight fits. All-day events appear in the list
and reach the island only when pinned. Point at an event row (or Tab to it) to show its pin and
close buttons; the island's preview has them too. Pin (filled when pinned) keeps an event in the island, and close
dismisses that occurrence from the island while it stays in the list, dimmed. These choices survive
refreshes and restarts, and later occurrences of a recurring event stay visible.

Add each calendar's private ICS link in **Settings › Calendar** (the settings button in the island's
header opens the page for the tab you are on), with a name and color. ATOLL
downloads them again every 15 minutes.

- **Google**: Calendar settings › the calendar › *Secret address in iCal format*.
- **Outlook / Microsoft 365**: Settings › Calendar › Shared calendars › *Publish a calendar* › copy
  the ICS link. Some Microsoft 365 admins turn publishing off for work accounts.

The calendar is read-only: ATOLL can't create, accept or edit events. Recurring events (weekly,
every second Tuesday, ...) are expanded correctly, including skipped and moved occurrences.
Google's links can lag by hours; that is on Google's end.

### Weather

Open **Settings › Weather**. Click **Use my location** to ask Windows for your location, or search
for a city and pick the right region. Windows coordinates are rounded to two decimal places before
they are stored or sent to Open-Meteo, and ATOLL doesn't track your location in the background.

Pick a forecast day and switch between **Precipitation**, **Wind** and **Temperature**. Point at
the chart, click a reading, or focus it and use Left/Right to step through the hours (Home and End
jump to the first and last). Missing readings stay empty rather than showing zero. Times use the
place's local time. Celsius uses km/h and millimeters; Fahrenheit uses mph and inches. Forecasts
refresh every 20 minutes. Rain timing is a forecast, not an alert service.

### Settings

Settings has eight pages: General, Appearance, Modules, Notifications, Agents, Calendar, Weather
and About. Every change applies immediately. In Modules, drag a handle, choose a position number,
or use Alt + Up/Down on a focused handle to reorder; related features share a tab at the first
enabled feature's position.

Settings, the shelf list and the log live in `%AppData%\Atoll` (`settings.json`, `shelf.json`,
`atoll.log`).

| Launch option | Effect |
| --- | --- |
| `--settings` or `--settings=<page>` | Open Settings on a page, in the order above (0 = General ... 7 = About) |
| `--open=<module>` | Open a tab: `agents`, `media`, `notifications`, `clock`, `system`, `files`, `weather`. Older names (`claude`, `calendar`, `shelf`, `clipboard`, `stats`, `actions`) open their group. |

## Privacy

ATOLL runs locally and has no account or telemetry.

- **Network.** Weather searches send a city name to [Open-Meteo](https://open-meteo.com), and
  forecasts send the chosen coordinates. Calendar downloads use the ICS links you add. Installed
  copies check this repo's GitHub releases for updates. Nothing else goes out.
- **Calendar links** are encrypted with your Windows login (DPAPI), so they can't be read from the
  settings file. Anyone with one of these links can read that calendar, so keep them private.
- **Clipboard history** stays in memory and is gone when ATOLL closes. Copies that password
  managers mark private are skipped.
- **The shelf** stores file locations only. ATOLL never copies, moves or deletes your files.
- **Notifications** are read only after you grant access, and stay on your PC.
- **Agent adapters** send a short summary of each event (agent, session folder, tool name, file
  name, the first line of a command, or notification text) to ATOLL over a local named pipe that
  only your user account can open. File contents are never sent.

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
  Atoll.ClaudeHook/   shared agent bridge exe (legacy name kept for installed Claude hooks)
  Shared/             hook message format shared by both
packaging/            release build, notification identity package, logo generator
tests/Atoll.Tests/    xUnit tests
```

- Each feature is an `IIslandModule`. It posts **activities** (a view model plus a priority and an
  optional duration) to the `ActivityHost`, and can provide a panel for the expanded view.
- The island shows the highest-priority activity with a DataTemplate named
  `{ViewModelType}.Compact`, `.Peek`, `.Mini` or `.Panel`.
- The island window is a fixed, transparent, always-on-top layered window; only the pill inside it
  animates, so clicks outside the pill reach the desktop.
- The pointer is tracked by polling the cursor position, because a click-through window gets no
  mouse events.
- The hourly weather chart is drawn natively and redraws only when data, selection or size change.
  System stats sample only while a stats view is visible, and solid themes never capture the screen.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), and the Windows SDK
(`makeappx.exe`, `signtool.exe`) for the notification package and release builds.

```powershell
dotnet build Atoll.sln
dotnet test Atoll.sln
.\src\Atoll.App\bin\Debug\net10.0-windows10.0.19041.0\Atoll.exe
```

### Notification identity for source builds

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

### Logo and brand assets

The icon, banners and MSIX logos are drawn by `packaging/make-logos.py` (Python with Pillow, numpy
and fontTools, plus the Noto Sans font). The output is committed, so you only need it after changing
the design.

## Releases

Versions follow [semantic versioning](https://semver.org) and each release is a `vX.Y.Z` tag on
`main`. The [releases page](https://github.com/Mazon3442/Atoll/releases) has the notes for each one.

To cut a release, from an up-to-date `main`:

```powershell
.\packaging\build-release.ps1 -Version 1.2.0            # Setup.exe, update packages and feed in .\releases
.\packaging\build-release.ps1 -Version 1.2.0 -Publish   # also tag v1.2.0 and publish the GitHub release
```

`-Publish` needs a GitHub token with `repo` scope in `VPK_TOKEN`. Installed copies pick the release
up on their next update check.

## Troubleshooting

- **Something is wrong**: check `%AppData%\Atoll\atoll.log`.
- **ATOLL isn't in my screenshot**: a frosted-glass theme is on; that is expected.
- **The visualizer doesn't move**: it runs only while media is playing.
- **Notifications say "one-time setup needed"**: run the setup in Settings › Notifications, or the
  two packaging scripts for a source build, then restart ATOLL.
- **Agent sessions don't show up**: install that agent's adapter in Settings › Agents and restart
  running sessions. For Codex, also review and trust ATOLL's hooks. If ATOLL moved, click
  **Install / update** again.
- **Hook debugging**: set `ATOLL_HOOK_DEBUG=1` to make the hook print errors to stderr.
- **A calendar is missing events**: Google's ICS links can lag by hours. Check the link in
  Settings › Calendar, and that the calendar is published.

## License

No license has been chosen yet. Until one is added, all rights are reserved by the author.
