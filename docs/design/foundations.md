# Foundations

Status: **draft**, derived from the approved mockups in `docs/design/mockups/`. `DESIGN.md` stays the short rulebook; this file holds the values. Nothing here is implemented yet.

## Island shape

One setting, **Island shape** (Settings > General), with two values. Everything else in this document is identical for both.

| | Notch | Pill |
| --- | --- | --- |
| Position | Flush to the top screen edge | 10 px below the top edge |
| Outline | Flat top, rounded bottom corners, concave 14 px shoulders where it meets the edge | Fully rounded on all four corners |
| Depth | None | Soft drop shadow and a 1 px white 6% ring |
| Compact | 36 px tall, bottom corners 18 | 36 px tall, all corners 18 |
| Side capsules | Same flush top and shoulders, no gap to the edge | Same pill outline, 6 px gap between capsules |

Shell radius is 34 in both shapes. Glass, reveal, hold and fullscreen behaviour are unchanged by the shape.

## Radius ladder

Nested radius is outer radius minus the padding between them. Use only these values.

| Token | Value | Used for |
| --- | --- | --- |
| `Radius.Shell` | 34 | Open island, compact pill |
| `Radius.Card` | 22 | Cards, popovers, menus |
| `Radius.Control` | 14 | Rows, inputs, artwork thumbnails inside cards, menu items |
| `Radius.Chip` | full (height / 2) | Chips, tabs, buttons, segmented switches, rings |

This replaces the current mix of 6, 8, 9, 10, 12, 15 and 18.

## Surfaces

Three levels, no borders and no shadows inside the island.

| Token | Value | Used for |
| --- | --- | --- |
| `Island.Background` | black, themeable | The shell |
| `Island.Surface` | white 7% (the theme's foreground at 7%) | Cards, tab strip, chips, rows |
| `Island.Raised` | white 14% | Selected segment, search field, pressed state |
| `Island.Hover` | white 12% | Hover on rows and icon buttons |

The active tab and the primary button use `Island.SelectedBackground` (near white) with `Island.SelectedForeground` (near black). Custom and light themes override these tokens only; no template hard-codes a colour.

## Colour roles

- Accent `#0A84FF` marks selection and chart readings only.
- Status: green live, amber needs attention, red error or now-line.
- Module tints identify a module on side capsules and calendar dots; text stays neutral.

## Type

Segoe UI Variable. Sizes stay as in `DESIGN.md`: 11 caption, 12 secondary, 13 body, 14 title, 16 and 18 headings, 20 and 24 figures, 32 and up display. Display numerals use the Display optical size, weight 300, tracking -0.02 em. Captions use +0.02 em.

## Spacing

4/8-point scale. Open panel padding 12 top, 14 sides, 14 bottom. Card padding 14, or 16 for hero cards. Gap between cards 10. Control height 34 for header items, 30 for buttons inside cards, 36 for list rows that carry two lines.

## Motion

Motion communicates opening, closing and selection only. Default springs are critically damped (damping 1.0, response 0.3 to 0.4 s). Add bounce only after a flick or throw. Every animation starts from the live value so it can be interrupted. Honour Windows reduced motion: cross-fade instead of slide or spring, no overshoot. Reduced transparency makes glass solid.

## Accessibility

Keyboard operation is unchanged: every control stays reachable by Tab, tabs by Ctrl+1 to 9, palette by Ctrl+K. Text contrast on `Island.Surface` stays at 4.5:1 or better; secondary text is white at 60% minimum. Focus rings use the foreground colour at 1 px, offset 2.

## Themes and frosted glass

The design is built on the existing theme tokens, so it works with custom colours, light themes and frosted glass. Rules that keep it that way:

- Every colour in a template comes from `Island.*` tokens. `Island.Surface` (foreground 8%), `Island.Raised` (16%) and `Island.Hover` (12%) are derived from the theme foreground, so they flip correctly on a light theme. The active tab, primary button and selected day use `Island.SelectedBackground` and `Island.SelectedForeground`, never a fixed near-white.
- Glass only changes `Island.Background` (the theme background tinted at 60% over the blurred screen). Cards stay translucent washes on top of it. Do not give a card an opaque fill, or the blur stops reading through.
- Rings and progress tracks are drawn as strokes on the card, not as an opaque disc, so they work on glass. Status colours (green, amber, red) and module tints get a light-theme variant checked for contrast, like `Island.AccentText`.
- The outline is `Island.Rim`, not a fixed white ring. The Pill drop shadow is allowed only when glass is off or as a faint shadow outside the glass region; it never sits behind the blurred area.
- The Notch shoulders and the Pill outline are part of the island shape, so the glass region (Windows blur region or captured brush) must follow the shape in both. Check the concave shoulders when the shape setting changes.
- Glass still hides the island from screen capture. The shape setting does not change that.

Known risks to test on real hardware: secondary text (60%) on a bright wallpaper under dark glass is close to the contrast floor; if it fails, raise the glass wash or the secondary text opacity on glass only. Mockups `theme-*.png` show dark glass, light glass and light solid on a bright wallpaper; they are CSS approximations, not WPF renders.
