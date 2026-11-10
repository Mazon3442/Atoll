# Planned features

Status key: **done**, **building**, **planned**, **pinned** (waiting), **dropped**.

## Done

- Real frosted glass: DWM blur behind the pill and side bubbles, with the captured blur as fallback. Both shapes and the one glass switch stay.
- Notch shoulders: small concave curves where the notch meets the top edge.
- Do Not Disturb: timed ATOLL focus on the moon chip, tray and palette; optional Windows Focus mirroring. Silences notifications, agent-done, calendar, media and game announcements.
- Laptop battery: Settings toggle, header chip, warning at 20%, persistent pill from 10% down.
- API keys: island tab removed; key setup and gateway configuration live in Settings > Agents.

## Building: Tide

Tide is our local teleprompter, inspired by Notchy's Cuely.

Done:
- Local script library: add, rename, delete, autosave.
- Prompter: speed presets, text size, mirror, manual wheel scroll, reading line.
- Follow My Voice using Windows speech recognition, off by default.
- Hide from screen capture while the prompter runs.
- Script text stays local.

Next:
- Listen mode: live transcript, notes, action items and suggested replies.
- Needs a decision: Windows speech or a local Whisper model.
- Needs system-audio capture for the other side of a meeting; the microphone alone is not enough.
- Notes can use local Ollama, or the Ask AI service with configured keys.

## Planned

### Ask AI

- Agents tab gets **Sessions** and **Ask** sections.
- Installed Claude Code, Codex or OpenCode first; API keys as fallback.
- Chat-only by default, with an explicit **Allow tools** switch.
- Shared `AgentChatService` for streaming, cancel and model choice.

### Persistent clipboard

- Opt-in history across restarts.
- Text and images, DPAPI encrypted.
- Proposed cap: 100 items or 25 MB.
- Disable or Clear wipes the file.

### Downloads activity

- Watch the real Downloads folder.
- Partial files: `.crdownload`, `.part`, `.partial`, `.download`, `.opdownload`.
- Live size, then a completed alert with Open and Show in folder.
- No fake percentage; browsers do not expose total size.

### Animation presets

- Add **Snappy** beside Bouncy, Smooth and Off.
- Bouncy keeps the overshoot. Smooth is critically damped. Snappy is shorter and tighter.
- Respect Windows reduced motion.

### Battery

Done. See above.

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
- Tide is intentionally local-only. No account, no cloud, no telemetry.
