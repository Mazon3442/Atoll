# Planned features

Status key: **done**, **partial**, **planned**, **pinned** (waiting), **dropped**.

## Done

- Real frosted glass: sampled blur of the screen behind the island, tinted with the background colour, since Windows 11 accepts the blur-behind request and draws nothing. Glass keeps the island out of screen capture while it is on.
- Notch shoulders: small concave curves where the notch meets the top edge.
- Do Not Disturb: timed ATOLL focus on the moon chip, tray and palette; optional Windows Focus mirroring.
- Laptop battery: Settings toggle, header chip, warning at 20%, persistent pill from 10% down. A desktop with no battery shows none of it.
- API keys: island tab removed; key setup and gateway configuration live in Settings > Agents.
- Tide prompter: local script library, reading line, speed presets, text size, mirror, voice follow, screen-capture hiding. No account, no cloud.
- Tide Listen: microphone and system audio, labelled You and Others, local Whisper (`base.en`), transcript per VAD window, notes, action items and suggested replies from Ollama or the configured keys.
- Ask AI: Agents tab Sessions/Ask switch, installed CLI first, API keys fallback, streaming with Stop, chat-only by default with an Allow tools switch.
- Downloads: watches the real Downloads folder, tracks browser temp names only, live size, then an alert with Open and Show in folder.
- Persistent clipboard: opt-in, DPAPI, text and images, 100 items / 25 MB, wiped when turned off.
- Snappy motion preset beside Bouncy, Smooth and Off, with the fade now honouring reduced motion.
- Media: shuffle and repeat (off, all, one) on the panel, driven by the player's own Windows media controls. They stay disabled for players that do not offer them.
- Updates: after a background download finishes, the island asks Restart or Later; each version is asked about once.

## Partial

- **Tide Listen** reports a transcript per 6 s window, not word-by-word. Language, model path and keep-audio are JSON-only, there is no in-app session list, and ducking is an unsmoothed per-frame switch. Nothing has been run against a real microphone, system audio or the Whisper model yet.
- **Ask AI** has not been run against real installed CLIs. Codex has no trustworthy zero-tool flag (read-only sandbox still reads files) and OpenCode cannot be proven chat-only, so the UI says so instead of pretending.
- **Downloads** is WPF-only. Chrome's `Unconfirmed NNNNN.crdownload` does not match its final name, so it stops without an alert rather than guessing.

## Planned

### Animation

- More presets if asked; Snappy covers the gap between Bouncy and Smooth.

## Pinned: home control

Waiting for device details.

- Matter direct is not practical on Windows. Use Home Assistant as the Matter/Thread bridge.
- Govee local LAN is feasible for models with LAN control; cloud key otherwise.
- Corsair iCUE SDK is feasible for lighting, while iCUE runs.
- Also practical: Philips Hue, LIFX, WLED, Nanoleaf, Kasa, Shelly.
- Needs: Govee models, Corsair devices, Home Assistant yes/no, and where controls should live.

## Pinned: Discord tab

Parked on purpose; build when asked. Mockup: `docs/assets/mockups/discord-tab.png`.

- Layout from the mockup: header with connection state, a voice-call card, Mic / Audio / Leave tiles, Share screen and Toggle camera, and a voice members list with a green ring on whoever is speaking. Drop the footer sentence; it repeats the controls.
- Real state needs Discord's local RPC (named pipe to the desktop app). That needs a Discord developer application, the user added as a tester on it, and a one-time Authorize prompt in Discord. The client ID and secret would live in Settings, encrypted for the Windows account like Clipboard history. Nothing leaves the PC except Discord's own local connection.
- RPC covers voice channel, members, speaking, mute, deafen and leave. It has no screen share or camera command, so those two send the user's own Discord shortcut keys and stay disabled until a key is set.
- A shortcut-only version needs no setup but cannot show members or whether the mic is muted.
- Also wanted: a call side-capsule while in a voice channel. Before the tab is connected it shows a Connect Discord state, not an empty pane.
- Open question: RPC (live state, five minutes of setup) or shortcuts only (no setup, no state).

## Pinned: phone transfer tab

Parked on purpose; build when asked. The mockup was shared in chat and is not saved in the repo.

- Layout from the mockup: header "Transfer to phone" with the subline "Same Wi-Fi · Phone ↔ Computer" and a Stop button, a QR code, "Scan QR · Valid for 15 minutes", a network picker (for example `192.168.1.102`), Choose files / Received files / Copy link / Clear list, and a "Send to phone" list showing each file with its size.
- Approach: a small local web server bound only to the chosen private network address. The QR holds a link with a random one-time token. The phone opens it in its browser, downloads what the computer is sending and can upload files back. No app, account or cloud.
- Stopping, or the 15 minutes running out, closes the server and invalidates the link. Received files go to a known folder, never straight to Desktop, and the page refuses anything without the token.
- Be honest in the UI: the transfer is plain HTTP on the local network, so anyone on the same Wi-Fi who sees the link could use it until it expires. Windows Firewall will ask the first time.
- Drop the duplicate "Same Wi-Fi" line under the list; keep it once in the header.
- Questions for later: where Received files should live, a size limit, and whether the Shelf should be able to send into it.

## Pinned: AirDrop

Parked on purpose; build when asked. Reference: https://github.com/t4t5/owly

- Owly is a Linux tool, written in Rust under the MIT license, that talks AirDrop with iPhones. Its README names Linux distributions and Wi-Fi cards that are not Apple's Broadcom parts.
- Not checked yet: whether any of it works on Windows. AirDrop rides on Apple's AWDL Wi-Fi link, and Windows Wi-Fi drivers do not normally expose it. Expect this to depend on specific adapters, or to be a non-starter.
- Start by reading its README and source for the AWDL requirement, then decide. If it cannot work on Windows, the phone transfer tab above (local web page and QR code) is the fallback for iPhones too.
- If it does work, it belongs in the phone transfer tab as a second way to send, not a separate tab.

## Hold: translations

- JSON language packs with English fallback.
- Runtime language switch in Settings.
- Do after feature strings stop moving.
- First language still undecided.

## Dropped

- FPS overlay. Not built.

## Notes

- All work stays on local branches until asked to publish.
- Tide is local-only: no account, no cloud, no telemetry. Its notes pass is the only outbound call and only when a model is configured.
