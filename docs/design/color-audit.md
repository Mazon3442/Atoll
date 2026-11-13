# XAML colour audit

Scope: every tracked `.xaml` file, audited for hexadecimal and named colours and `Color` properties. Transparent fills remain transparent. Windows window chrome continues to use its existing system theme resources.

## Mapped to island tokens

| File | Previous literals | Role and replacement |
| --- | --- | --- |
| `src/Atoll.App/Onboarding/OnboardingWindow.xaml` | `#0A0A0D` | Preview backdrop: `Island.Menu`, the opaque version of the theme background, so preview labels remain readable in a light window even when the actual island background is translucent |
| Same | `#151519`, `#121216`, `#17171C` | Preview chips: `Island.Surface` |
| Same | `#25252C` | Inset artwork fallback: `Island.Raised` |
| Same | `#26262D`, `#303038`, `#2A2A31` | Preview outlines: `Island.Hover` |
| Same | `#ECECF1`, `#F4F4F7`, `#D8D8DE`, `#F2F2F6` | Primary preview text and icons: `Island.Foreground` |
| Same | `#8D8D96`, `#787881` | Secondary preview text: `Island.Subtle` |
| `src/Atoll.App/Settings/SettingsWindow.xaml` | `#22808080` | Preset thumbnail outline: `Island.Hover` |
| Same | `Black` | Pill and notch shape previews: `Island.Background` |

## Unmapped: require a semantic colour or contrast decision

These literals are retained rather than assigning an unrelated token. This task adds no tokens or new theme behaviour.

| File | Literal and use | Why no mapping was made |
| --- | --- | --- |
| `src/Atoll.App/Modules/Calendar/CalendarTemplates.xaml` | `White` on the accent-filled today marker | No contrast-on-accent token exists. `SelectedForeground` is paired with `SelectedBackground`, while this marker uses `Accent`. |
| `src/Atoll.App/Modules/Home/HomeTemplates.xaml` | `White` on the accent-filled today marker | Same contrast-on-accent issue. |
| `src/Atoll.App/Modules/Tide/TideTemplates.xaml` | `White` in `Tide.Primary` | Foreground of an accent-filled action; needs contrast-on-accent treatment. |
| Same | `#E5484D` stop-button fill, `White` stop symbol | Recording/error status needs a theme-aware status colour and a matching contrasting foreground. |
| `src/Atoll.App/Modules/Games/GamesTemplates.xaml` | `#FF453A`, `#30D158` | Offline/online status; no error/success token exists. |
| `src/Atoll.App/Shell/IslandWindow.xaml` | `#FF9F0A` | Battery warning status; no warning token exists. |
| `src/Atoll.App/Modules/Stats/StatsTemplates.xaml` | `#FF9F0A` | Battery warning status; same missing warning role. |
| `src/Atoll.App/Settings/ColorPicker.xaml` | `#80808080` swatch border; `#202020` and `White` hue-slider thumb | Colour editing controls must remain distinguishable against arbitrary selected colours and hue backgrounds. No single island foreground/background pair guarantees that contrast. |

Status colour contrast on light themes belongs to the theme/verification work in S1 and S6. In particular, substituting the selection accent for warning, error or success would erase status meaning.

## Retained for their defined purpose

| File | Literals | Reason |
| --- | --- | --- |
| `src/Atoll.App/Theme/Theme.xaml` | Default `Island.*` brush colours | These define the tokens; `ThemeService.Apply` overrides them for custom/light themes. Replacing a token definition with a reference to itself is invalid. |
| `src/Atoll.App/Theme/Logo.xaml` | `#F1EEE7`, `#0B0C0E` | Brand artwork palette. |
| `src/Atoll.App/Modules/Claude/ClaudeTemplates.xaml` | `#D97757` | Claude module tint. |
| `src/Atoll.App/Modules/Clock/ClockTemplates.xaml` | `#32D74B` | Break/coffee mode tint. |
| `src/Atoll.App/Modules/Privacy/PrivacyTemplates.xaml` | `#FF9F0A`, `#30D158` | Microphone/camera indicator tints. |
| `src/Atoll.App/Settings/SettingsWindow.xaml` | `#D97757` | Claude glyph tint in the island preview. |
| Same | `#243544`, `#617488`, `White` | Illustrative wallpaper and its caption. The wallpaper is independent of the island theme so the preview shows the island against a desktop. |
| `src/Atoll.App/Settings/ColorPicker.xaml` | `#FFF4F4F4`, `#FFC9C9C9` | Transparency checkerboard artwork; changing these with the edited theme would alter the reference background. |
| Same | `Red`, `Yellow`, `Lime`, `Cyan`, `Blue`, `Magenta` | Hue spectrum, which must represent actual colours. |
| `src/Atoll.App/Modules/Media/MediaTemplates.xaml` | `Black`, `Transparent` in `OpacityMask` | Alpha-only lyrics fade; RGB is ignored. Theme colour/alpha would change the mask's meaning. |
| `src/Atoll.App/Modules/Tide/TideTemplates.xaml` | `#33000000`, `#66000000`, `#FF000000`, `#80000000` in `OpacityMask` | Alpha-only prompter fade; RGB is ignored. |

All other matched `Color` values are existing theme or data bindings. No unclassified literal remains in the audited XAML.
