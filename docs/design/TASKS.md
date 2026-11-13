# Redesign task split

Read first: `docs/design/foundations.md`, `docs/design/layout.md`, `docs/design/mockups/*.png`, `AGENTS.md`.

## Rules for every task

- One task per branch off `design/system-refresh`, named `design/<task-id>`. Open one PR per task against `design/system-refresh`, not `main`. Do not merge.
- Touch only the files the task names. Keep unrelated changes out.
- Use `Island.*` theme tokens for every colour. No hard-coded colours, no new fonts, no new font sizes.
- Radii come only from the ladder: 34 shell, 22 card, 14 control, full for chips (height / 2).
- Keep keyboard operation, custom colours and reduced-motion support.
- Validate: `dotnet build Atoll.sln` and the relevant tests in `tests/`. Use a separate artifacts directory while the user's app is running (`--artifacts-path`), and render the touched WPF templates.
- Do not change behaviour. Layout, style and radius only.

## Codex tasks (self-contained, low risk)

Do in this order. Each is a mechanical change with a clear check.

### C1. Radius ladder in module templates
Replace every `CornerRadius` in the module template files with the nearest ladder value: card-sized surfaces 22, rows, inputs and thumbnails 14, pill-shaped things keep the existing pill converter. Files: `Modules/*/*Templates.xaml` and `Modules/Weather/WeatherDetails.xaml`. Skip `Theme/`, `Shell/`, `Settings/`, `Onboarding/` (those are separate).
Check: build passes; no `CornerRadius` value outside 22, 14 or the pill converter remains in those files.

### C2. Radius ladder in Settings and Onboarding
Same rule for `Settings/SettingsWindow.xaml`, `Settings/ColorPicker.xaml` and `Onboarding/OnboardingWindow.xaml`. These windows are not the island, so keep their own shell corner but use 14 for controls and 22 for cards.
Check: build passes, windows open, no clipped content.

### C3. Spacing pass on Home
In `Modules/Home/HomeTemplates.xaml` set card padding 14, gap 10, and the grid to 4 columns with 130 px rows to match `home-notch.png`. Layout order stays: now playing 2x2, weather, timer, today agenda 2x1.
Check: render the template and compare with the mockup; all four cards visible with no clipping.

### C4. Hard-coded colour audit
Search all `.xaml` for literal colours (`#RRGGBB`, `White`, `Black`, `Color=`) that are not module tints or the artwork palette. Replace each with the matching `Island.*` token (`Surface`, `Raised`, `Hover`, `Subtle`, `Foreground`, `SelectedBackground`, `SelectedForeground`). Write a list of anything you could not map into `docs/design/color-audit.md` instead of guessing.
Check: build passes; the list is committed.

### C5. Settings string for the shape option
Add the label and helper text for the **Island shape** setting (Notch / Pill) to Settings > General as a two-option control, wired to a new `IslandShape` enum property in the settings model with default `Pill`. Persist it with the other settings. Do not change how the island renders yet; Claude does that in S2.
Check: setting saves and reloads; a unit test covers the default and round-trip in `tests/Atoll.Tests`.

### C6. Tests
Add a test that fails if any template under `Modules/` uses a `CornerRadius` outside the ladder (read the XAML as text). Run after C1.
Check: test passes on the branch and fails if you change a value to 9.

## Claude tasks (shell, shape, glass, risky)

- **S1. Theme tokens and shared styles:** add `Radius.*` resources, rework `Theme.xaml` styles (tab, chip, segmented switch, row, card, ring, button) to the ladder and surfaces.
- **S2. Shape system:** render Notch or Pill from `IslandShape`: outline, shoulders, 10 px gap, shadow rules, compact pill and side capsules in both shapes, glass region following the shape (`PillBorder`, `GlassSampler`, `SystemBackdrop`).
- **S3. Header and navigation:** icon-first tab strip with the active label, chip row, three-dot menu, bookmark rules unchanged.
- **S4. Rings as strokes:** timer and plan-usage rings drawn as arcs so they work on glass.
- **S5. Page layouts:** Media, Agents, Calendar, Weather, Notes, System, Palette, then the remaining tabs, matching the mockups.
- **S6. Glass and theme verification:** contrast on bright wallpapers, light themes, reduced motion and transparency, fullscreen and game mode.
- **S7. First-run and docs:** onboarding preview shows the shape choice; fold the rules into `DESIGN.md`; update `README.md` and screenshots.

## Order of work

C5 can start now. C1, C2 and C4 can run in parallel. S1 and S2 land first on Claude's side because C3 and the page work depend on the shared styles. C3 waits for S1. C6 waits for C1.

## Status (branch `design/s1-tokens`)

- Done: S1 (radius ladder and shared styles), shell radius 34, Home card layout, Media cards, `Island.Card` and `Island.PrimaryRoundButton`, DESIGN.md rules.
- Already existed, no work needed: the Notch / Pill setting and rendering (`ThemeSettings.Shape`, `PillBorder`, glass shapes), the icon-first tab strip with the selected tab labelled, side-capsule rings drawn as strokes. So **C5 is redundant** and S2 to S4 reduced to the shell radius constant.
- Still open: C1 and C2 (Codex, radii in module templates and Settings), C3 (superseded by the Home rebuild: skip), C4, C6; S5 for Agents, Notes, Calendar, Weather, System, Palette (they already use the same surfaces; remaining work is card grouping and radii); S6 on-screen glass check in the running app; S7 onboarding preview and README screenshots.
