# Vibershot style guide

How the Vibershot desktop UI should look and feel. The capture window (`Vibershot.UI/MainWindow.xaml`) is the reference implementation; new windows and controls should follow the same rules. All color and font tokens live in `Vibershot.UI/App.xaml` so XAML can reference them with `{StaticResource Name}`.

## Principles

- **Solid, not glass.** Opaque surfaces with a 1px edge and a real drop shadow. No transparency effects. The only gradient and glow in the app are on the launcher's hero button.
- **Ink does the hierarchy.** Off-white type on dark slate carries structure through size and weight. Color is not used to rank text.
- **One accent, used once.** Purple appears as a fill on exactly one element per window: the primary action. Everywhere else it is a glyph, a stroke, a dim tint or a hover color. The light Dracula purple (`Accent`) is for glyphs and strokes on dark surfaces; anything that holds white text is filled with the deeper `AccentFill`.
- **Space instead of rules.** Separate groups with padding and gaps, not dividers. Rows show a fill only on hover.
- **Monospace for data.** Keyboard shortcuts, version tags and filenames use the mono font so they scan as data, not prose.
- **Placeholders look like placeholders.** Features that are not wired up yet are disabled, drawn in muted ink, and carry a `SOON` chip instead of a shortcut.

## Color tokens

| Token | Hex | Use |
| --- | --- | --- |
| `Paper` | `#282A36` | Window and panel surface (Dracula background) |
| `PaperEdge` | `#44475A` | 1px outer border of a window, scrollbar thumb |
| `Rule` | `#3C3F52` | Chip borders, pressed row fill, chrome hover |
| `Hover` | `#343746` | Row hover fill, disabled icon tile |
| `Well` | `#21222C` | Page background behind grouped cards (Settings), launcher tiles |
| `Ink` | `#F8F8F2` | Primary text, wordmark |
| `Ink2` | `#B3B8CF` | Secondary text, hints, chrome icons, pin off state |
| `Ink3` | `#6272A4` | Section labels, chips, version tag, neutral status (Dracula comment) |
| `Accent` | `#BD93F9` | Primary action fill, pin on state, row icons, focus ring, selection chrome (Dracula purple) |
| `AccentDeep` | `#CBA8FF` | Lighter purple for hover on accent-coloured text or glyphs |
| `AccentFill` | `#7B52D1` | Fill of primary buttons, checked toggles and tool toggles (deep enough for white text) |
| `AccentFillHover` | `#8D66E3` | Primary button hover |
| `AccentSoft` | `#3F3A5E` | Tint behind row icons, active rows and tiles |
| `AccentInk` | `#FFFFFF` | Text and glyphs on an `AccentFill` fill |
| `AccentEdge` | `#5C4F88` | Border of an accent-tinted panel (the editor's tool accordion), tile hover edge |
| `AccentWash` | `#2E2D42` | Fill of an accent-tinted panel |
| `WarningInk` | `#F1FA8C` | Text inside a `WarningBox` (Dracula yellow) |
| `Ok` | `#50FA7B` | Status dot after a save or copy (Dracula green) |
| `Danger` | `#FF5555` | Status dot after a failure (Dracula red) |

Semantic colors (`Ok`, `Danger`) are separate from the accent and never used for decoration.

The shared control templates in `App.xaml` read the accent through `DynamicResource`, so a window could override the four `Accent*` tokens in its own resources if it ever needed a different accent; today every window uses purple. The launcher adds two local gradient brushes: `HeroFill` (deep purple into magenta, diagonal) for the primary action and `Hairline` (purple to pink, fading out at both ends) for the 2px line at the top of the window.

## Typography

Font: **Segoe UI Variable** with Segoe UI fallback. Mono: **Cascadia Mono** with Consolas fallback (`MonoFont` resource).

| Role | Size | Weight | Color |
| --- | --- | --- | --- |
| Wordmark | 13 | Bold | `Ink` |
| Primary action label | 16 | Bold | `AccentInk` |
| Primary action hint | 11.5 | SemiBold, 85% opacity | `AccentInk` |
| Row title | 13.5 | Bold | `Ink` (`Ink2` when disabled) |
| Row hint | 11.5 | Normal | `Ink2` |
| Section label | 10.5 | Bold, uppercase | `Ink3` |
| Chip / shortcut | 10.5 mono | Normal | `Ink3` (on paper) or `AccentInk` (on accent) |
| Footer status | 11 | SemiBold | `Ink2` |
| Footer link | 11 | Bold | `Ink`, `Accent` on hover |
| Version tag | 10 mono | Normal | `Ink3` |

Sizes are device-independent pixels as WPF `FontSize`.

## Spacing and shape

Base unit is 4px. Use these values and avoid inventing new ones.

| Where | Value |
| --- | --- |
| Window outer margin (shadow room) | 16 |
| Window side padding | 20 (22 for header and footer text so it aligns optically) |
| Header height | 56 |
| Gap between groups | 18 |
| Gap between rows | 6 |
| Primary action padding | 18 horizontal, 20 vertical |
| Row padding | 14 horizontal, 12 vertical |
| Icon tile to text | 12 (primary), 14 (rows) |
| Footer padding | 16 top, 18 bottom |

| Element | Corner radius |
| --- | --- |
| Window | 16 |
| Primary action | 12 |
| Row, icon tile on primary | 10 |
| Row icon tile | 9 |
| Chrome button | 6 |
| Chip, shortcut cap | 5 |
| Logo tile | 4 |

Shadow on a window: black, blur 40, depth 10, opacity 0.55. Popup shadow: black, blur 24, depth 6, opacity 0.45.

## Components

**Window chrome.** 30×30 icon-only buttons using Segoe Fluent Icons at 13pt, `Ink2`, no border. Hover fills with `Rule`. Order from left: pin, settings, minimize, close. The pin shows `Accent` on `AccentSoft` when on and `Ink2` when off. Native-framed windows get a `Paper` caption with `Ink` text through DWM.

**Primary action (hero).** Full-width `HeroFill` gradient block (`AccentFill` into a deep magenta), radius 14, with a purple glow behind it (`DropShadowEffect` in `Accent`, blur 30, depth 0). Inside: a 42px icon tile at 18% white, a bold 17pt label, a one-line hint, and the shortcut in a bold mono chip at 22% black on the right, all in `AccentInk`. Hover adds a 10% white sheen and brightens the glow. Only one per window.

**Tile.** The launcher's secondary actions are three equal tiles on `Well` with a `PaperEdge` border, radius 12, 12px padding. Each holds a 32px icon tile (`AccentSoft` behind an `Accent` glyph), a bold title and a wrapping hint; an optional chip sits in the top-right corner. Hover lifts to `Hover` with an `AccentEdge` border; press fills `AccentSoft` with an `Accent` border.

**Row.** Borderless button, `Hover` fill on mouse over, `Rule` on press. Layout is icon tile, title plus hint, optional right-aligned chip. Row icons sit on `AccentSoft` in `Accent`. A disabled row uses `Hover` for the tile, `Ink3` for the glyph, `Ink2` for the title and a `SOON` chip.

**Scrollbar.** 10px wide, transparent track, no arrows. The thumb is a `PaperEdge` rounded bar, `Ink3` on hover and `Accent` while dragging.

**Chip.** 1px `Rule` border, radius 5, 6×3 padding, mono text in `Ink3`. Shows a real shortcut only when that shortcut is actually registered (for example the Print Screen chip appears only when Print Screen is set to Full desktop).

**Section label.** Uppercase, 10.5 bold, `Ink3`, inset 4px past the content edge.

**Footer.** No rule above it. A 7px status dot, the status text trimmed with an ellipsis and a tooltip holding the full message, and a text link on the right.

**Settings group.** Cards use the `Group` style: paper fill, `PaperEdge` border, radius 12, 20px padding, on a `Well` page background. A `GroupLabel` (uppercase, 10.5 bold, `Ink3`) heads each card. Toggles sit on the right of a bold title and a `Hint` line, separated by `Divider` rules. Dividers are allowed inside a card because the rows are settings, not actions.

**Preview well.** Captures sit on a dark checkerboard (`#2C2E3A` and `#363947`, 10px squares) inside a `PaperEdge` border so transparent, white and dark images all read.

**Overlay chrome.** On the region selector the selection stroke and glow are `Accent`. The hint and the action bar are `Paper` panels with a `PaperEdge` border and a shadow. The first action is the primary button.

**Warning box.** Dim yellow panel (`#3A3A2B` fill, `#6E6E45` border) with `WarningInk` text, for a problem Windows must fix.

**Focus.** Every interactive element shows a 2px `Accent` border when keyboard focused. On the accent primary action the ring is `Ink` instead.

## Writing

- Labels name what happens: "Capture region", "Open folder". Not "Go" or "OK".
- Hints are one short line in plain words: "Every monitor, saved instantly".
- Status messages lead with the outcome: "Saved: …", "Capture cancelled.", "Capture failed: …". The status dot color is derived from these words, so keep them.
- Never show a shortcut the app has not registered.
