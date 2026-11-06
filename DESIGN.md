# Atoll island design

Direction: Apple's Dynamic Island and the weather reference supplied by the owner on November 6, 2026.
Energy 1 / Rhythm 2 / Motion 2: quiet controls, content-led layouts, responsive transitions.

- Use a dark, near-black island by default because it belongs at the edge of the desktop. Preserve the user's light and custom themes.
- Use the Windows system variable font for legibility and reliable installation. Large, light temperature numerals lead weather; secondary readings use smaller, regular type.
- The island is the enclosing surface. Use subtle inset surfaces for selected controls and rows; avoid stacking decorative cards inside it.
- Use generous outer rounding, smaller control rounding, and a consistent 4/8-point spacing scale to distinguish the shell from its controls.
- Use the chosen accent for selection and chart readings. Neutral text and surfaces carry the rest of the interface.
- The weather chart answers how precipitation, wind, or temperature changes across the selected day's hours. Plot only forecast data, preserve gaps, and expose exact values through pointer and keyboard selection.
- Weather follows the supplied composition: location and current temperature, condition and high/low, metric switch, hourly chart, daily forecast rows. Location editing and refresh belong in Settings, not the weather header.
- Use sender icons for notifications, with a native bell fallback. The notification view must work independently of coding-agent templates.
- Motion communicates opening, closing, and selection. Respect Windows reduced-motion settings; keep input available during transitions.
