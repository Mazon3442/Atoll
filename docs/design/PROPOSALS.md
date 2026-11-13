# ATOLL design system: direction proposals

Status: **draft for decision**. Nothing here changes `DESIGN.md` or the app until one direction is picked.
Reference apps are described from memory of their public behaviour, not from fresh teardown; verify before copying any detail.

## Where we are

- The open island is a 760 x 320 panel: a header row (tab strip left, status chips right) over one content body.
- Every tab is its own layout. Content is laid out per module, so Home, Media, Weather and Calendar share a palette but not a grid.
- Corner radii in `Theme.xaml` are ad hoc: 6, 8, 9, 10, 12, 15 and 18, plus pill-by-height. `DESIGN.md` fixes type sizes and spacing but not radii, elevation or a content grid.
- Navigation is a 3-level system (top row, bookmarks, three-dot menu, command palette). It is powerful but it is the part that looks most like a settings app and least like an island.

The goal is consistency: one grid, one radius ladder, one set of surfaces, one motion vocabulary. These directions differ in how the island *feels*, not in what it can do.

## What every direction shares

These do not need a vote.

1. **Radius ladder** derived from the shell: shell, panel card, control, chip. Nested radius = outer radius minus padding. No other values.
2. **Three surface levels**: shell (the island), inset (`Island.Surface`), raised (`Island.Raised`). No borders, no shadows inside the island.
3. **4/8 spacing and a fixed content grid** so a module is composed from slots, not hand-placed.
4. **Component set**: icon button, segmented switch, row, chip, ring, slider, popover. One style each, used everywhere.
5. **Tokens in one place**, custom colours and light themes preserved, keyboard operation and reduced motion preserved (per `AGENTS.md`).
6. **Docs split**: `docs/design/` holds foundations, layout, components and motion; `DESIGN.md` stays the short rulebook that links to them.

## Direction A: Notch (Boring Notch, NotchNook)

**Idea:** the open island is one seamless piece of the screen edge that grew downward. Two or three fixed views, very little chrome.

- Shell grows from the notch with concave shoulders (we already have them) and a single opaque black body.
- A **Home view** is the default: media on the left, a shelf or the next calendar item on the right. Most visits never leave it.
- The tab strip collapses into a small row of icons at the bottom edge, like NotchNook's switcher. Fewer tabs visible, the rest through the palette.
- Panel height is fixed per view, so opening never reflows.

Pros: closest to the "island" feeling; fewest decisions per screen; easy to keep calm.
Cons: demotes the 15+ tabs; Agents, Weather and Calendar need a compact variant; users who bookmarked tabs lose the top row.
Cost: shell + navigation rewrite, medium module rework.

## Direction B: Live Activity (Apple Dynamic Island, Alcove)

**Idea:** the island is organised around *what is happening now*, not around tabs.

- Each module defines three sizes: **compact** (the pill and side capsules we have), **expanded** (a focused card for one activity) and **full** (today's tab).
- Opening the island shows the stack of live activities (music, timer, agent run, download) as expanded cards. A quiet "browse" affordance reaches the full tabs.
- Transitions are shared-element: the compact artwork grows into the expanded card, the timer ring grows into the timer card.
- Idle state is the Home dashboard.

Pros: best match to ATOLL's strongest idea (activities, side capsules, alerts); motion becomes the design language.
Cons: every live module needs two new layouts; the activity stack needs ranking rules; most animation work of the three.
Cost: highest, but it can ship incrementally, one module at a time.

## Direction C: Widget deck (NotchNook, DynamicLake)

**Idea:** keep tabs, but make every tab a bento grid of same-grid cards.

- Content area is a 12-column grid with fixed cell sizes: 1x1, 2x1, 2x2, 4x2. Every module composes its tab from those cells.
- The tab strip becomes a left icon rail (about 44 px) that never scrolls horizontally; bookmarked tabs sit above a divider, the rest under the three-dot menu.
- Cards share one header pattern (icon, title, optional action) and one padding.
- Home is the same grid with user-chosen cards, which we already allow.

Pros: the least risky for existing features; guarantees consistency mechanically; simplest to document and test.
Cons: reads more like a dashboard than an island; a rail costs about 44 px of width; hero modules (Media, Weather chart) fight the cell sizes.
Cost: medium. Mostly layout and style work, little behaviour change.

## Comparison

| | A Notch | B Live Activity | C Widget deck |
| --- | --- | --- | --- |
| Feels like | the screen edge | a living status | a control centre |
| Navigation | icon row, palette | activity stack, browse | left rail |
| Consistency comes from | fewer views | shared size contract | fixed grid |
| Existing tabs | demoted | kept as "full" | kept as-is |
| Motion weight | low | high | low |
| Effort / risk | medium / medium | high / medium | medium / low |

## Recommendation

**C for the structure, B for behaviour, A for the look.**

1. Adopt the shared foundations and the grid from C first. They are the part that actually fixes the consistency problem and carry the least risk.
2. Take A's visual restraint: one opaque body, no inset cards beyond the grid, icon-first navigation.
3. Introduce B's expanded size per module after the grid lands, starting with Media and Timer where compact and full already exist.

## Decisions needed from you

1. Which direction (or mix) to proceed with?
2. Tab navigation: keep a top strip, or move to a left rail or bottom icon row?
3. Panel size: keep 760 x 320, or fix a taller default so every tab uses the same height?
4. Should the radius ladder allow the current pill-by-height chips, or round everything on the ladder?

## Proposed rollout once chosen

1. `docs/design/foundations.md`: tokens, radius ladder, surfaces, type, spacing. Update `Theme.xaml` to match, no visual change beyond radii.
2. `docs/design/layout.md` and the shell: grid, header, navigation.
3. `docs/design/components.md`: one pass per component style, rendered in a WPF gallery for review.
4. Module-by-module migration, one PR each, Home and Media first.
5. Fold the final rules back into `DESIGN.md`.

## Update: shape setting and mockups

- Requirement: every current feature stays. This is a redesign of look and layout only.
- New setting **Island shape** (Settings > General): **Notch** (flush to the screen edge, flat top, concave shoulders) or **Pill** (floating 10 px below the edge, fully rounded). One shell template, one switch; every tab and capsule uses the same radius ladder in both shapes.
- Mockups for the combined direction (grid cards, icon-first header with the active tab labelled, restrained black shell) are in `docs/design/mockups/`. Source: `mockups.html`; open it with `#compact`, `#home-notch`, `#home-pill`, `#media-pill`, `#agents-notch`, `#weather-notch`, `#more-pill` or `#palette-notch` to regenerate a view.

## Decision log

- Direction: grid cards for structure, restrained black shell, expanded activities later. Approved from mockups.
- Island shape setting (Notch or Pill) is required.
- Home keeps the original layout (now playing 2x2, weather, timer, today agenda). Variants A to D are archived in `docs/design/mockups/`.
- Values and layout rules are in `foundations.md` and `layout.md`.
