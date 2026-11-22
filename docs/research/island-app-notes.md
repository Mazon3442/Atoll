# Island (islandapp.pro) — competitor teardown

Source: https://islandapp.pro/en, the public README at github.com/exuupery/island (docs, no source code), and README screenshots. Captured 2026-11-22. Island is a closed-source "Dynamic Island for Windows" (4 MB, 64-bit, Win 10/11, 21,400 downloads, 180 GitHub stars, solo dev, RU/EN UI). Free tier + one-time **Island Pro $4.99** (399 ₽ for Russian cards), 14-day full Pro trial, offline-verified licence key valid on all own PCs.

Notes are descriptive. Nothing here is a spec for Atoll; the "Atoll gap" column is based on `DESIGN.md` only and needs checking against the code before any work.

## 1. Core concept

- A "liquid black pill" at top-centre. Collapsed it shows live events; hover or push the cursor against the top edge and it opens into a tabbed toolbox.
- **Never steals keyboard focus** from the window being typed in, so clicking a clipboard entry pastes at the caret. (Does not work into elevated windows; entry still lands on the clipboard.)
- Hides in exclusive-fullscreen games (cannot draw there).
- Excluded from screenshots/recordings/streams by default; one setting shows it.
- Self-updates: checks at startup and every 6 h, shows an "Update" card, downloads signed installer, restarts.

## 2. Signature visual idea: ferrofluid edge

The shell's lower edge is a procedural wavy "liquid" line, not a static rounded rect.
- Reaches toward the cursor.
- Spikes to music in the album-cover colour; with "edge as equaliser" it is a real frequency spectrum (low left, high right).
- Drops a bead on good news (download done, etc.); sags when the user is away; drips lightly when it rains outside.
- Pill silhouette has concave "shoulder" curves where it meets the screen edge (notch-like), see screenshots.
- Marketing hero: the same wavy edge under an app mock on a vivid blue→violet→coral gradient.
- Optional "character" replaces the liquid (see 4).

## 3. Collapsed pill states (all fold away automatically)

Default: clock centred, battery % next to it on laptops, weather icon + temp left/right when idle.
Media: cover thumb left, equaliser bars right, wavy edge in cover colour. Three music layouts: normal, wide with track name + thin progress bar, **mini player** (cover, title, bar, buttons inside the pill). Synced lyrics line (LRCLIB, free, only for the playing song) lights up in the mini player or its own pill.
Others: dictation (mic + red voice meter + timer + stop), screen recording (red dot, time, format chip, area icon, Stop), download (ring + filename + size + speed), shelf (file-count badge), character in pill, timer visible while running, Steam download %, OBS recording/stream status (via local OBS websocket), mic/camera-in-use dots with mute button, keyboard layout + Caps Lock, VPN chip with country + IP (ipwho.is, can be disabled), Windows notifications as cards (click opens app; per-app blocklist), AI agent status.
Volume: mouse wheel over the pill changes volume.

Expanded "cards" (transient, 1 line + icon + action button):
Reminder (bell icon, title, +10 min snooze pill, check button), dictated text (mic icon, text, copied, open button), screenshot ready (thumbnail, resolution, "drag the preview into a chat or folder", edit + folder buttons), colour picker (swatch, hex + "copied", rgb mono, history dots), downloaded file (file-type icon, name · size, open-folder button), VPN connected (green shield, country · IP · protocol).
Pattern worth noting: every card is icon-chip left, title + muted subtitle, 1–2 circular action buttons right, hairline wavy accent line at the bottom edge.

## 4. Characters (25 "tenants")

Alternate pill identities: Pixel, tail, island-on-legs, jellyfish, hamster wheel, campfire, lens, Newton's cradle, cat, etc. Each dances to music, reacts to AI thinking / finished actions / alarms, **eyes follow the real cursor**, falls asleep when idle. Live preview in Settings → Character. 3 free (Pixel, Spark, Eyes), 22 Pro. Monetisation lever: cosmetic content gates Pro.

## 5. Open-panel tabs

Top row icons (selected tab = pill with label): Home, Clipboard, Shelf/Inbox, Text, Chat, Note, Weather, Controls; top-right: voice-waveform button, pin, settings.

- **Home**: large now-playing card (source label e.g. SPOTIFY, title, artist, scrubber, time and remaining, prev/play-pause/next, play button is a white circle), volume slider with numeric value and output-device dropdown, a 2×4 grid of quick-action tiles (Area, Full screen, Screen text, QR code, Color picker, Plain text, Record screen, Command), and a **launcher row** of app icons with a dashed "+" tile. Launcher: drag one shortcut onto another to make a group (opens above the row); "+" adds a URL or custom protocol (`steam://run/…`) with auto-fetched icon; right-click edits; each shortcut can have its own global hotkey.
- **Clipboard**: text + image history, search, pins, click-to-paste into previous window; images open with a button; screen recordings land here too. Password-manager copies marked private are never recorded; custom app skip-list. Local SQLite.
- **Shelf**: drag files onto the island, drag them out later one-by-one or all at once.
- **Text**: wrong-layout fixer, case changes, transliteration, JSON, Base64, URL, QR generation; AI actions: translate, fix, shorten, explain, reply.
- **Chat** (Pro): context-keeping chat; attach images and PDF/Word/Excel/PowerPoint/text by paste, drop or pick; images scaled and sent as images, documents parsed locally and sent as text; dictate with mic button; copy + regenerate under replies; reply bubbles with bullet lists, bold numbers. Bring-your-own key.
- **Command bar** (Alt+Space): apps incl. Store apps, files/folders by name (names in memory only), recent files, calculator, unit + currency conversion (CBR rates), web search, "timer 10 min", "remind me to call mum at 7 pm", `>` runs cmd/PowerShell, `Tab` asks the AI, `Ctrl+Enter` opens containing folder (or continues an AI answer in chat), `Ctrl+Shift+Enter` run as admin. System commands: "wifi", "bluetooth", "theme", "lock".
- **Note + timer + reminders**: self-saving rich note (bold, italic, strike, colour mark, bullets, numbered, checkbox tasks), folders; timer with presets or custom time, lives in the pill; reminders from one natural phrase ("pills every day at 23:00", "in 20 minutes turn the stove off"), repeat on named days, editable afterwards, appear as sound + card that waits until answered.
- **Controls**: Wi-Fi (or wired), Bluetooth with paired devices, battery level and one-press connect for headphones/speakers, VPN clients (Happ, v2RayTun, Karing, WARP, Mullvad, PIA, Windscribe, OpenVPN, Windows built-in), dark-theme toggle, lock/sleep/restart/shutdown (last three require double press).
- **Weather** (Pro): sky background matching the weather, now + 24 h + 7 days; temperature in the pill when idle; card ~1 h before rain/snow; MET Norway (no key, no signup); city chosen by name once, never IP-guessed.

## 6. Tools

Screenshot (area/full → clipboard + folder; markup: arrows, boxes, marker, text, blur), OCR text-from-screen (Pro; merges Windows RU+EN recognisers line by line), QR-from-screen, colour picker with magnifier + history, screen recording MP4 (H.264 via Media Foundation, 30/60 fps, quality) or GIF (Pro), paste without formatting, voice-to-text (Pro; hotkey, text to clipboard or typed into the previous window; Groq Whisper/Gemini), upload screenshot to Imgur/ImgBB with own key, "through mode" (collapsed pill is click-through and does not open under cursor), auto-hide (slides off, returns on cursor rest at the top edge; or only while named programs are foreground), per-monitor placement + slide along the top edge, sits under a top taskbar, size 80–200 %, independent transparency for collapsed and open, dark/light/system theme, choose which sound device volume/mic the island controls (virtual mixers), player picker when several players are open.

## 7. AI agent integration (Pro)

Claude Code and Codex push status to the island: working / waiting for you / done. A card shows the question or the start of the answer. **Claude Code can be answered inline**: Allow/Deny a command, or pick one of its multiple-choice options (e.g. branch list + Skip), plus "In the terminal" escape hatch and working-dir label. Connection is one button in Settings: it edits the agents' own config (Claude Code hooks, Codex `notify`) and keeps a backup beside the file. Data stays local, kept nowhere. (Atoll already has an Agents tab and plan-usage ring per `DESIGN.md`; compare depth.)

## 8. AI plumbing

BYO key, OpenAI-compatible endpoint, keys encrypted with DPAPI per user. Presets: Groq (≈1000 req/day free, images ≤3/message, Whisper dictation), Google Gemini (free AI Studio tier, images, dictation), OpenRouter (50 free-model req/day), DeepSeek (paid), OmniRoute (local gateway). Flow: pick service → "Get a key" → paste → "Test". AI lives in three places: Text tab buttons, command bar via `Tab`, Chat tab; command-bar answers continue in chat with Ctrl+Enter.

## 9. Hotkeys (all rebindable)

Ctrl+Alt+I open/close, Alt+Space command bar, Ctrl+Alt+A chat, Ctrl+Alt+D dictation, Ctrl+Alt+V clipboard, Ctrl+Alt+S / +Shift+S screenshot area/screen, Ctrl+Alt+O OCR, Ctrl+Alt+Q QR, Ctrl+Alt+C colour picker, Ctrl+Alt+R record, Ctrl+Alt+Shift+V plain paste, Ctrl+Alt+M mic mute.

## 10. Privacy / trust posture (used as a selling point)

Local SQLite, DPAPI keys, licence verified offline, no telemetry beyond listed optional requests (full list of every endpoint is in the README: AI endpoint, GitHub update check, api.met.no coords only, Nominatim city search, cbr-xml-daily.ru rates, ipwho.is, link favicons, OBS on 127.0.0.1). Recording not kept after recognition. Site cookie banner defaults to declining analytics. Data attributions for MET Norway (CC BY 4.0), OSM (ODbL), GeoNames.

## 11. Website design language (islandapp.pro)

- **Palette** (CSS vars): `--bg #07070a` near-black with a slight cool tint; `--ink #f3f1ec` warm off-white; `--mute rgba(243,241,236,.66)`, `--dim .58`; `--line rgba(255,255,255,.08)`; `--card rgba(255,255,255,.035)`; accents **amber `#ffb35c`** (primary: headline highlight "for Windows", glows), coral `#e8845f`, violet `#8b7bff`; status green `#4ade80`, sky `#5eb0ff`; extra surfaces `#0d0d10`, `#040406`, button text `#0b0b0e`. Most borders are white at 6–16 % alpha; panels are black at 90–95 %.
- **Type**: headline **Unbounded** 700 (wide geometric display, ~84 px h1, tight), body/UI **Onest** (600 for buttons, 14 px), mono stack `ui-monospace, Cascadia Code, Consolas` for code/values. Hero H1 sits inside an outlined pill-shaped container with a faint amber halo.
- **Shape**: everything pill/full-round (buttons 99 px radius), large 24–32 px rounded device frames on saturated blue→violet→magenta→orange gradient "wallpaper" stages so the black island pops.
- **Hero animations**: rolling odometer counter (21 400 downloads, digit-strip scroll), a marquee/loop of pill states (music, download, mic/camera, timer, reminder, VPN, Steam, OBS, agent, notification, layout/Caps, lyrics), per-letter reveal (`isl-letter`), `isl-unveil`, `isl-rise`, `isl-halo`, `isl-beam`, `isl-eq`, `isl-drain/fill` progress, blink eyes for the greeter character (`--es` scale var).
- **Interactive demos** on the page: live mock island you can press (Allow/Deny a git push, choose a branch), chat demo that reads a PDF + chart, tabs showcase with the wavy edge.
- **Motion curves** (overshoot-heavy springs): `cubic-bezier(.2,1.3,.3,1)`, `(.3,1.4,.4,1)`, `(.2,1.6,.4,1)`, `(.3,1.6,.5,1)`; calm ones `(.16,1,.3,1)`, `(.2,.8,.2,1)`, `(.4,0,.2,1)`. Container-query unit `--u: calc(100cqw/1000)` scales the whole mock proportionally.
- **Accessibility**: `@media (prefers-reduced-motion: reduce)` kills all animation (`animation:none!important`).
- **Copy tone**: short, concrete, second person, trial-and-FAQ ("Hover over it, and the island opens"; "A shelf for files"; "The short answers").
- **In-app UI language** (from screenshots): solid black panel, 16–20 px inner radius cards `~#141416`, selected tab = pale-grey pill with label, other tabs = icon only, large play button = white circle, secondary controls = dark rounded squares, sliders thin white-on-grey with numeric readout, accent colour pulled from album art for the progress bar, mono font for numeric technical values, hairline wavy accent line at edge.

## 12. Gap audit vs Atoll (verified against code, 2026-11-22)

Status: **Have** = present in code; **Partial** = something close exists; **Gap** = no trace found (grep plus reading the owning module); **Check** = not verified.

| Island feature | Status | Evidence / note |
|---|---|---|
| Live collapsed pill, side capsules, clock, battery chip | Have | `Atoll.Core/Activities`, `ClockModule`, `ShowBattery` |
| Music pill with level-reactive visuals | Have | `AudioLevels`, `MediaModule`, Bongo cat |
| Synced lyrics (LRCLIB) | Have | `Atoll.Core/Modules/Media/Lyrics.cs`, `ShowLyrics` |
| Mini-player inside the collapsed pill | Partial | Media activity and lyrics exist; no mini-player pill layout setting found |
| Ferrofluid wavy edge, drips, beads, sag | Gap | Only static notch shoulders (`PLANNED.md`) |
| Edge as frequency equaliser | Gap | Levels exist but the edge shape is static |
| Cursor-following, sleeping characters | Partial | Only Bongo Cat (`BongoCatStyle`) |
| Mic/camera in-use indicator | Have | `PrivacyModule` (names the apps) |
| Mute button on that indicator | Partial | `QuickActionsModule` has mic mute, separate from the privacy pill |
| Caps/Num Lock popup | Have | `IndicatorsModule` |
| Keyboard-layout chip | Gap | no `KeyboardLayout` anywhere |
| Volume/brightness/network/Bluetooth popups | Have | `IndicatorsModule` |
| Mouse-wheel volume on the pill | Gap | Header wheel switches tabs (`OnHeaderWheel`) |
| VPN chip (country + IP) | Gap | no VPN code |
| Controls tab: Wi-Fi, VPN, theme, lock/sleep/restart/shutdown | Partial | Bluetooth chip and battery exist; no Wi-Fi toggle or VPN; palette has Lock and Turn off display |
| OBS recording/stream status | Gap | no OBS code |
| Steam download progress | Partial | Steam library scan only (`GameLibrary`) |
| Downloads pill and card | Have | `Modules/Downloads` |
| Windows notifications as cards | Have | `Modules/Notifications` |
| Clipboard history | Have | `ClipboardModule`; Atoll is opt-in and DPAPI-encrypted, stricter than Island |
| Skip password-manager copies | Check | not verified |
| File shelf | Have | `ShelfModule`, `FileDragDrop` |
| Text tools tab (layout fix, case, JSON, Base64, URL, QR) | Gap | none |
| AI chat | Have | `AgentChat` (CLI first, API fallback, streaming). Check PDF/Office attachment parsing |
| AI text actions (translate/fix/shorten) | Gap | |
| Claude Code inline Allow/Deny | Have | `ClaudeTemplates.xaml`, `Atoll.ClaudeHook` pipe with reply. Check multiple-choice option picking |
| Codex/OpenCode/Hermes status | Have | `Integrations/*` (more agents than Island) |
| Command bar | Partial | Palette: tabs, Start Menu apps, actions, timers, DND. Missing: files by name, calculator, unit/currency, web search, `>` shell, phrase parsing, Ctrl+Enter open folder, admin launch |
| Natural-language reminders | Gap | reminders are a form (`NotesModule`) |
| Rich-text note with folders | Partial | Notes are plain editable text |
| Timer in pill | Have | `ClockModule` plus Pomodoro (Island has none) |
| Weather tab, pill temperature | Have | `WeatherModule`; rain heads-up and sky background not verified |
| Screenshot markup, OCR, QR scan | Gap | Screenshot just launches `ms-screenclip:` |
| Screen eyedropper | Gap | `ColorPicker` is the theme colour control only |
| Screen recording | Gap | |
| Voice-to-text dictation | Partial | Tide Listen has local Whisper (`AudioPipeline`); no hotkey dictate-into-window |
| Screenshot upload (Imgur/ImgBB) | Gap | |
| Paste without formatting | Gap | |
| Launcher groups, custom links, per-item hotkeys | Gap | Launcher is a Start Menu scan only |
| Auto-hide, per-program rules | Partial | `HideWhenIdle`, `FullscreenMode`, `MaximizedMode`, `EdgeHoldMs`; no per-program list |
| Click-through mode | Have | `ClickThrough` (while a window is maximised) |
| Fullscreen-game handling | Have | `FullscreenWatcher`, Games module (stronger than Island) |
| Frosted glass, hidden from capture | Have | `GlassSampler`, `CapturePrivacy` |
| Per-monitor placement, top offset | Have | `MonitorMode`, `TopOffset` |
| Island size and transparency | Partial | width/height/roundness/opacity; no single 80-200 % scale |
| Themes | Have | `Theme`, `CustomThemes` |
| Many rebindable global hotkeys | Partial | only open, hide, palette (`SettingsShortcuts`) |
| Self-update with Restart/Later | Have | `Updates` |
| Calendar, DND, Tide prompter/Listen, Games, Minecraft server, Pomodoro, provider usage | Atoll-only | Island has none of these |

### Suggested picks (my judgement)

1. **Command bar upgrades**: calculator, units/currency, `>` shell, files by name, "timer 10 min" and "remind me to X at 7 pm" parser. Pure logic, unit-testable, builds on `CommandPalette`.
2. **Text tab**: layout fixer, case, JSON/Base64/URL, QR generate. Self-contained module.
3. **Screen tools via Windows APIs**: OCR (`Windows.Media.Ocr`), QR decode, eyedropper. Markup is bigger.
4. **Launcher groups, custom links, per-item hotkeys.**
5. **Small chips**: privacy-pill mute button, keyboard-layout chip, wheel-volume option (conflicts with the tab-switching wheel, so make it a setting).
6. **Ferrofluid edge**: the only item that changes Atoll's identity. Costly, and must respect `DESIGN.md` (reduced motion, Snappy preset, notch/pill shapes), so it needs your design call first.
7. More rebindable hotkeys.

## 13. Per-feature GUI layouts (from the website's feature cards)

Source: the user's screenshots of the islandapp.pro feature grid plus the README images. Descriptions are of layout and behaviour, not pixel specs.

### Shared shell (every open tab)

- **Frame**: solid black panel, large rounded bottom corners, concave shoulders at the top where it meets the screen edge, wavy "ferrofluid" bottom edge with a thin pink/violet hairline along it. Panel width is the same for every tab (about 3:2 of the screen mock); only height changes per tab.
- **Top bar** (always the same): left = tab icons in one row (Home, Clipboard, Shelf, Text "T", Chat bubble, Note, Weather, Controls sliders). The **active tab is a pale rounded pill with icon + label** ("Clipboard", "Shelf 3", "Chat", "Note", "Weather", "Controls", "Home"); inactive tabs are icon-only, dim grey. Shelf shows a count badge inside its pill. Right side = voice-waveform button (dictation), a **round pin button** (filled circle when pinned open), and a settings sliders icon.
- **Bottom hint line** inside panels where useful: tiny muted text, e.g. "Click to paste · Shift+click to copy only" with `⇧ Enter` hint at right.
- Marketing cards: dark glass card (rounded 24–28 px, 1 px low-alpha border), title in Unbounded with an optional **hotkey chip** ("Alt+Space") or a **Pro badge** (amber gradient pill), 2–3 line muted description, then the panel mock sitting on a blue→violet→pink→orange gradient stage, clipped by the card.

### Clipboard

- Full-width **search field** with magnifier and a placeholder "Search history" (focused on open, caret visible), **trash icon button** square at right.
- Below: a big dashed-outline empty area. Empty state = clipboard glyph, bold "History is empty", muted "Copy text or an image and it shows up here". With data the rows/thumbs replace it.
- Footer hint: "Click to paste · Shift+click to copy only".

### Shelf

- Header row: left a **"Drag all (3)" pill with a drag-handle grip icon**, right a muted "Clear" with trash icon.
- Body: files laid out as a **grid of tiles**, each a coloured file-type badge (red PDF, blue PNG, grey TXT) over filename (truncated) and muted size ("1.8 MB", "402 KB", "2.0 KB"). No borders on tiles; drag one out or all at once.

### Command bar (Alt+Space)

- Replaces the panel body, keeps the top shell. Top = search glyph, large typed query ("sc") with caret, a faint `Alt+Space` chip at right.
- Results list: one row per result, **left glyph, title, right-aligned muted source label ("Island")**, selected row has a lighter rounded highlight and a **return-arrow ↵** at its right. Examples: Screenshot of the screen, Lock the computer, Screenshot of an area, Screenshots folder, Screen recording.
- Last two rows are always fallbacks: **Ask AI: "query"** with the model name at right (e.g. gemini-3.8-flash), and **Search in the browser: "query"**.

### AI chat (Pro)

- Message column: user bubbles right-aligned in a dark rounded pill; attachments shown above the bubble as **chips** (image thumbnail, and a document chip with PDF icon, name, "12 pages").
- Assistant messages left-aligned with a small violet sparkle avatar, plain text (no bubble), bold numbers, bullet lists; under the reply two tiny icon buttons (copy, regenerate).
- Composer pinned at the bottom: **new-chat/compose icon** at left, long rounded field with paperclip at its left, placeholder "Message the AI…", **microphone** and a round **send arrow** button (grey when empty, light when ready).

### Note, timer and reminders (one tab, two columns)

- Left column (about 60 %): **note sub-tabs** across the top as small pills: "Note", "Ideas", "Shopping" and a "+" (the folders). The note body is a rounded dark editor with a title line ("Friday release"), a checklist (done item struck through with an amber check; others open circles), highlighted text (yellow mark), italic line, and a mention-style inline highlight ("call Anna"). **Formatting toolbar along the bottom**: B, I, S (strike), highlight, bullet, numbered, task, then right side a tick, "Remind", copy, trash.
- Right column: **timer ring** (amber circular progress, "24:58" in the middle) beside a **Pause** primary button, and small "+1 min" and "Reset" buttons; muted "until 0:27 · 25 min in all". Under it a divider and **"Reminders 2"** list: bell icon, title, muted due ("in 25 min", "tomorrow 9:00"), and at the bottom a single **input row** with the placeholder "Call mum tomorrow at 9" (the phrase parser).

### Control centre

- A **tile grid**: top row of four large rounded tiles, each with a round **icon badge** (violet/indigo circle) at top-left, a title and a muted state line below: Wi-Fi (On), Bluetooth (On, plus a small menu/list icon at top-right of the tile for paired devices), Dark theme (On), Battery (82 %, "On battery", battery glyph instead of a badge).
- Second row of four **shorter action tiles**, icon on top, label under: Lock, Sleep, Restart, Shut down (the last three need a second press).
- Panel is short (no scrolling); tiles are equal width.

### Weather (Pro)

- Left: a **tall rounded sky card** whose colour matches the weather (blue-grey for partly cloudy): location pin + city (Tokyo) with a refresh icon, weather glyph, huge light "21°" numeral, "Partly cloudy" and "Feels like 21°  21°…28°", and a bottom row of three small stat chips (wind "7 m/s NW", humidity "93 %", rain "0 mm") plus sunrise/sunset times.
- Right top: **hourly strip**, 8 columns: time label, icon, temperature, with the current hour first as "Now".
- Right bottom: **7-day rows**: day name, weather icon, low temp, a **range bar** (thin rounded track with an amber segment from low to high, positioned on a shared week scale), high temp, and precipitation in blue ("0.2 mm", "38 mm"). Footer "Data from MET Norway".

### Home

- Row of **action tiles** (2×4) on the right: Area, Full screen, Screen text, QR code, Color picker, Plain text, Record screen, Command; each tile = outline glyph left + label, dark rounded rectangle.
- Left: big **now-playing card** (cover art with rounded corners, "SPOTIFY" tiny caps label, title bold, artist muted, thin progress bar in the cover's accent colour with elapsed and remaining times, prev / large white circular pause / next).
- Under it: **volume row** (speaker icon, white slider, numeric "62", **output-device dropdown chip** "Speakers ⌄").
- Bottom: **launcher shelf** row of real app icons plus a dashed square "+" tile.

### Light theme and size

- Same layout in a **white/very light grey panel**, black text, dark pill for the active tab, tiles in slightly darker grey, same wavy edge. Size is a 80–200 % scale applied to the whole island.

### Feature chips row (below the grid)

Compact pill buttons, each with an amber-outlined black icon square and a label: Screenshots with markup, Text from the screen (Pro), QR code from the screen, Colour picker, Screen recording (Pro), Voice to text (Pro), Windows notifications, Auto-hide. Pro items carry the same amber Pro badge.

### Patterns worth reusing

1. Active tab = pale pill with label; inactive = icon only; one pin button; a dictation button in the header.
2. Same shell for every tab; only the body changes, so switching feels like swapping content inside one object.
3. Empty states are specific and short (icon, bold title, one muted line) inside a dashed area.
4. Settings-like controls are **tiles with a state line**, not toggles in a list.
5. Search-driven surfaces always end with two fallback rows (Ask AI, Search the web).
6. Timer = ring plus single primary button; reminder entry = one free-text row, no date picker.
7. Weather range bars on a shared scale; the sky card colour carries the condition.

### Compared with Atoll (layout only, from DESIGN.md)

- Atoll already uses a card grid, segmented sections, a star/bookmark for the top row, and a three-dot menu for other tabs; Island instead puts all tabs in one icon row with a pin button.
- Atoll's Home uses month, now-playing, weather, timer cards; Island's Home is player + volume + 2×4 action tiles + launcher shelf. The action-tile grid and the volume row with an output-device chip are the pieces Atoll's Home may lack.
- Island's Controls tile grid and reminder phrase row have no Atoll counterpart (Atoll's System tab content wasn't inspected).

## Caveats

- Island is closed source; this is from public marketing copy, README text and screenshots only. Do not copy its assets, character art, or copy text; ideas and general interaction patterns are fine to reimplement independently.
- Statements about the interactive site demos come from rendered text and a handful of screenshots; I did not run the app.
