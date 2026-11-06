# ATOLL

A desktop island for coding agents, music, timers, calendars, weather and files.
ATOLL uses .NET 10 and Avalonia on Windows, macOS, Ubuntu and Arch Linux.
No account is required.

[Releases](https://github.com/Mazon3442/Atoll/releases) · [Builds](https://github.com/Mazon3442/Atoll/actions/workflows/release.yml)

## Install

Download the package for your computer from GitHub Releases. Each package includes
the .NET runtime and the agent hook executable.

| System | Package | Install |
| --- | --- | --- |
| Windows 10 (2004+) / 11, x64 | `Atoll-win-Setup.exe` | Run the installer; installs to your user folder. |
| Windows portable | `Atoll-win-Portable.zip` | Extract the whole ZIP and run `Atoll.exe`. |
| macOS 14+, Apple silicon | `Atoll-X.Y.Z-osx-arm64.dmg` | Open the disk image and drag ATOLL to Applications. |
| macOS 14+, Intel | `Atoll-X.Y.Z-osx-x64.dmg` | Open the disk image and drag ATOLL to Applications. |
| Ubuntu 22.04 / 24.04, x64 | `atoll_X.Y.Z_amd64.deb` | `sudo apt install ./atoll_X.Y.Z_amd64.deb` |
| Arch Linux, x64 | `atoll-X.Y.Z-1-x86_64.pkg.tar.zst` | `sudo pacman -U ./atoll-X.Y.Z-1-x86_64.pkg.tar.zst` |
| Linux portable, x64 | `Atoll-X.Y.Z-linux-x64.tar.gz` | Extract and run `./Atoll`; install the native dependencies listed below. |

Windows installers are currently unsigned. macOS bundles have an ad-hoc signature,
not an Apple Developer ID signature or notarization. Downloaded builds may need
approval in the operating system's security settings before they open.

Each platform's `SHA256SUMS-*.txt` lists its package hashes. On Linux/macOS, use
`sha256sum -c SHA256SUMS-linux-x64.txt` or `shasum -a 256` respectively; on Windows,
use `Get-FileHash -Algorithm SHA256` and compare the result.

Linux uses X11 or XWayland. Overlay placement, tray icons and global clipboard access
depend on your desktop/compositor. Install XWayland when using a Wayland session.
The `.deb` and Arch package declare their required libraries, `playerctl` for MPRIS
media controls, and PulseAudio-compatible tools for volume controls. A portable
copy needs the same libraries; see `packaging/package-linux.sh` for the full list.

## Use ATOLL

1. Launch ATOLL. The compact island appears at the top of your primary display.
2. Hover over or click the pill to expand it. Press Escape to collapse it.
3. Open Settings from the island or tray/menu-bar icon. The same menu can pause
   the island or quit ATOLL. Settings also has a Quit button for desktops without a tray.
4. Choose your displays, theme and enabled modules. Existing Windows settings,
   saved themes, calendars and shelf paths are reused.

| Feature | Availability |
| --- | --- |
| Agents | Claude Code, Codex, OpenCode and Hermes adapters on all platforms. Install or update adapters in Settings > Agents, then restart agent sessions. Claude token/cost totals come from local transcripts and are estimates at API list prices. |
| Clock and timers | Clock, countdown presets, stopwatch and Pomodoro on all platforms. |
| Calendar | Private HTTPS ICS links, upcoming events, pin/dismiss controls and pre-event alerts on all platforms. Add links in Settings > Calendar. |
| Weather | Open-Meteo city search, current conditions and seven-day forecasts on all platforms. Hourly wind, rain and temperature charts support pointer and keyboard selection. |
| Files | File shelf and clipboard history on all platforms. Clipboard access may be restricted by the desktop. History stays in memory; sensitive/private formats are skipped. |
| Media | Windows media sessions with art and seeking; Linux MPRIS through `playerctl`; Music/Spotify on macOS through Apple Events. macOS may ask for Automation permission. |
| Devices | Windows native audio/device events; Linux PulseAudio/PipeWire-compatible volume and battery readings; macOS volume and battery readings. |
| System stats | CPU, memory and network readings on all platforms. |
| Quick actions | Screenshot, microphone mute, lock, display off and Downloads. Linux screenshot/display actions require desktop tools such as `gnome-screenshot` and `xset`; `xset` requires X11. |
| Notification mirroring | Windows only, after package identity setup and permission. Linux desktops differ; macOS has no supported listener for other apps' notifications. ATOLL's own agent, calendar and timer alerts work everywhere. |
| Fullscreen/game handling and click-through | Windows native handling. Linux/macOS window behavior follows the desktop/window manager. |
| Updates | Windows installer copies download updates from published GitHub releases and apply them on restart. Linux packages update through reinstall/package management; macOS and portable copies update from release downloads. |

The Avalonia shell uses compact and expanded views with grouped tabs. It does not
yet reproduce the old WPF side bubbles, animated peek view, GPU graphs or screenshot
exclusion. The WPF project remains in the repository for regression coverage; release
packages launch the Avalonia app on every platform.

### Windows notification setup

Open Settings > Notifications and run **Set up notifications**. This explicitly asks
for administrator permission to trust ATOLL's development signing certificate in
Local Machine > Trusted People and register a sparse identity package. It does not
install a root CA. Quit and reopen ATOLL, then allow notification access.
The installer itself does not need administrator access.

`packaging/remove-identity.ps1` removes the identity and its development certificate.
Uninstalling the Windows app is otherwise available through Windows Settings > Apps.

## Privacy and local data

- Settings, shelf paths and logs live under the current user's application-data
  folder: `%AppData%/Atoll` on Windows, `~/.config/Atoll` on Linux (or the .NET
  application-data location), and `~/Library/Application Support/Atoll` on macOS.
- Calendar links retain Windows DPAPI encryption. Linux/macOS use AES-GCM with a
  random key in `secrets.key`, readable only by the user. Back up that key with your
  settings; losing it means saving your calendar links again. This protects stored
  links from other accounts, not from programs running as you.
- Clipboard history is not saved. Private/concealed/password formats are ignored.
- Agent adapters send short summaries over a current-user-only local named pipe.
  Existing hooks are preserved and backed up; malformed settings are left unchanged.
- Weather requests send the search text or selected coordinates to Open-Meteo.
  Calendar requests go to the ICS providers you add. Update checks go to GitHub.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download). These commands work
on all supported systems:

```sh
dotnet run --project src/Atoll.Desktop/Atoll.Desktop.csproj
dotnet test tests/Atoll.Desktop.Tests/Atoll.Desktop.Tests.csproj -c Release
```

On Windows, also run the WPF regression suite:

```powershell
dotnet test tests/Atoll.Tests/Atoll.Tests.csproj -c Release
```

Do not build the entire solution on Linux/macOS: it includes the legacy WPF project.
Use a separate `--artifacts-path` when a development copy is running.

For Windows notification setup in a source build, install the Windows SDK
(`makeappx.exe`, `signtool.exe`), run `packaging/build-identity.ps1`, rebuild the
desktop project, then run setup from the Notifications settings page.

## Releases

The **Desktop releases** GitHub Actions workflow tests and packages all four targets:
Windows x64, Linux x64, macOS Intel and macOS Apple silicon. Pull requests build the
same packages as releases and upload installers, checksums and rendered UI evidence
as workflow artifacts. A manual workflow run accepts `X.Y.Z` and builds artifacts
without publishing or creating a tag.

After merging to `main`, tag the intended release commit:

```sh
git tag v0.10.0
git push origin v0.10.0
```

When every platform succeeds, the workflow creates a **draft** GitHub release with
all platform downloads attached. Review its notes and publish it from GitHub.
Windows auto-updates only see published releases. Existing release assets are never
silently overwritten.

For a local package, use PowerShell 7 (`pwsh`) on the target OS:

```powershell
./packaging/build-release.ps1 -Version 0.10.0 -RuntimeIdentifier win-x64
./packaging/build-release.ps1 -Version 0.10.0 -RuntimeIdentifier linux-x64
./packaging/build-release.ps1 -Version 0.10.0 -RuntimeIdentifier osx-arm64
```

Outputs go to `artifacts/release/<runtime>/<version>/packages`. Use a fresh
`-OutputRoot` when rebuilding the same version. Windows needs the Windows SDK;
Linux packaging needs `dpkg-deb`, `tar` and `zstd`; macOS uses `sips`, `iconutil`,
`codesign`, `ditto` and `hdiutil`. Windows uses a pinned, local Velopack CLI install.
`-Publish` creates a draft release containing that local build and requires `gh`
plus `GH_TOKEN` (or the existing `VPK_TOKEN`). Use the workflow to assemble a release
containing every platform.

## Source layout

```text
src/Atoll.Core/         shared settings, activity arbitration, parsers and agent logic
src/Atoll.Desktop/      Avalonia shell, views, platform adapters and startup handling
src/Atoll.App/          legacy WPF shell and module sources shared with Avalonia
src/Atoll.ClaudeHook/   self-contained agent bridge, built for each release runtime
src/Shared/            hook protocol shared with the bridge
packaging/             native packages, notification identity and existing brand assets
tests/Atoll.Desktop.Tests/ portable logic and rendered Avalonia tests
tests/Atoll.Tests/      Windows/WPF regressions
```
