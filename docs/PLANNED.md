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
