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
- Calendar is its own tab: a month grid with calendar-colored event dots and a selected-day agenda. Home's month grid is a shortcut into it, not a second calendar implementation. Keep the calendar read-only; Join opens a meeting link without editing the event.
- The Launcher is one tab with Apps and Games sections. Start Menu apps are searchable; the Games section keeps game mode, the game library and the Minecraft server together.
- Timer is one tab with Timer and Pomodoro sections. The Timer section keeps the minute presets and the stopwatch; the Pomodoro section sets focus length, short break, long break and how many focus sessions come before a long break. Its start button names the focus length. Both sections use the same quiet controls as the rest of the island.
- Notes is one tab with Notes, Reminders and To-do sections. Notes are editable text, reminders are one-off timed alerts, and to-dos are a simple checklist. Keep each section quiet: one segmented switch, one input row and a short list.
- Files and Clipboard are separate tabs. Files is the shelf of paths; Clipboard is recent copies held in memory only.
- The Bluetooth header chip opens a small popover with connected devices and battery levels. The chip belongs with the other status chips, not inside the System tab.
- Side activities use attached capsules beside the main pill: two on each side, each with a module icon, a short live label and a quiet module tint. Surface tints identify the module; text stays neutral. Capsules disappear when the island opens.
- Every enabled tab has three places in Settings: **bookmarked** (the star) keeps it in the header's top row, on-but-unstarred waits in the three-dot menu, and off removes it from both. Settings shows exactly one card per tab with two whole-tab controls: the star bookmarks the tab and the switch turns it off. Switches inside a card only fine-tune its parts and never carry stars. Bookmarks follow the Settings card order, so dragging a card reorders the top row. Choosing a tab from the three-dot menu opens it for that visit; its pin turns it into a bookmark. When every tab is bookmarked, the three-dot button disappears. Keep the same icon and name treatment in the menu as in Settings.
- The default top row is Home, Agents, Media, Notifications, Calendar, Files, Clipboard and Weather. Everything else ships on, in the three-dot menu.
- Games get their own Settings page: island behavior while playing, notification mute, performance and play-time readouts, start announcements, automatic detection, which libraries to scan, and the Minecraft server with its check interval. Keep each group short and independent.
- First run keeps the Settings chrome but leads with the island itself: a near-black stage showing the compact pill and side capsules, a top-row picker whose strip preview updates as tabs are starred, and a ready screen with that same strip. One decision per screen; skipping keeps the defaults and never leaves a tab off. The star is the only bookmark control: outline at rest, filled accent when pinned.
- Use sender icons for notifications, with a native bell fallback. The notification view must work independently of coding-agent templates.
- The command palette is one search surface over tabs, Start Menu apps, quick actions and timers. It opens over the panel body, keeps the header, starts with an empty query, and runs the selected row with Enter. Escape returns focus to the selected tab. A terminal action closes the island; opening a tab keeps it open.
- Motion communicates opening, closing, and selection. Respect Windows reduced-motion settings; keep input available during transitions.
