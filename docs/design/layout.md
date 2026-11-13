# Layout and pages

Status: **draft**, derived from the approved mockups in `docs/design/mockups/`. Values live in `foundations.md`.

## Open island

- Width is the Expanded width setting (default 640; the mockups are drawn at 760). Cards share the width in two columns of equal size. Height follows the page, with a standard body of 260 to 272 px. Header 40 px plus 10 px gap.
- **Header:** tab strip on the left, status chips on the right. Only the active tab shows its label; the others are 34 px icon buttons. A tab opened from the three-dot menu appears at the end of the strip and takes the label. The three-dot button stays last and disappears when every tab is bookmarked.
- **Chips, left to right:** Do Not Disturb, Bluetooth, battery (laptops only), command palette, Settings. Bluetooth and DND open popovers.
- **Body:** one page, composed from cards on a 4-column grid with 10 px gaps. A card spans 1 or 2 columns and 1 or 2 rows.

## Grid cards

Rows are 130 px (Home, System: 110 to 112). Spans:

- 1x1: a single reading (weather, timer, CPU).
- 2x1: a list or agenda.
- 2x2: a hero (now playing).

Two-pane pages (Media, Calendar, Weather, Agents) use two flex cards at about 1.1 : 0.9 instead of the grid, still on the same radius, padding and gap.

## Home (approved, built)

The original layout, unchanged in structure:

- Now playing, 2x2: artwork 100, title, artist, level bars in the cover colour, progress, controls (shuffle, previous, play or pause, next, repeat).
- Weather, 1x1: location, 40 px light temperature, condition, high and low.
- Timer, 1x1: ring with time remaining, mode, session count.
- Today, 2x1: three agenda rows with calendar-coloured bars and a Join button.

Cards are still chosen in Settings > Modules > Home. A hidden card frees its cells for the next card.

## Other pages

| Page | Layout |
| --- | --- |
| Media | Artwork 196, source and output pickers, title, bars, progress, controls on the left; synced lyrics on the right |
| Agents | Sessions / Ask switch; permission prompt first, session rows, plan-limit rings, cost estimate |
| Notes | Notes / Reminders / To-do switch; add row, list, next-reminder card |
| Calendar | Month grid with coloured dots, selected-day agenda with Join. Read-only |
| Weather | Location and 56 px temperature, metric switch, hourly chart with pointer and keyboard readout, daily rows |
| System | CPU, memory, network, battery, volume slider, quick actions |
| Palette | Search field and result rows over the body; header stays |

Tabs not yet mocked (Notifications, Files, Clipboard, Timer, Launcher, Tide) follow the same card, row and segmented-switch patterns.

## Components

One style each, used everywhere: icon button, chip, tab, segmented switch, row, card, ring, progress, slider, popover, menu item, primary and secondary button. Compose pages from these; do not add a new one-off control.

## Compact and side capsules

- Compact pill 36 px tall, minimum 190 wide: clock when idle, artwork and title with bars while playing, the most urgent activity otherwise.
- Up to four side capsules, two per side. One or two are round dots; three or four widen non-numeric ones into capsules. Timer and agent plan usage stay rings. Capsules disappear when the island opens.

## Open decisions

- Panel body height: standard 272, or grow per page as now.
- Whether the header chips collapse into the three-dot menu below a minimum width.
