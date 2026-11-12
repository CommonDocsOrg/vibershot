# Vibershot style guide

How the Vibershot desktop UI should look and feel. The capture window (`Vibershot.UI/MainWindow.xaml`) is the reference implementation; new windows and controls should follow the same rules. All color and font tokens live in `Vibershot.UI/App.xaml` so XAML can reference them with `{StaticResource Name}`.

## Principles

- **Solid, not glass.** Opaque surfaces with a 1px edge and a real drop shadow. No gradients, transparency effects or radial glows.
- **Ink does the hierarchy.** Near-black type on paper white carries structure through size and weight. Color is not used to rank text.
- **One accent, used once.** Tangerine appears at full strength on exactly one element per window: the primary action. Everywhere else it is a pale tint or a hover color.
- **Space instead of rules.** Separate groups with padding and gaps, not dividers. Rows show a fill only on hover.
- **Monospace for data.** Keyboard shortcuts, version tags and filenames use the mono font so they scan as data, not prose.
- **Placeholders look like placeholders.** Features that are not wired up yet are disabled, drawn in muted ink, and carry a `SOON` chip instead of a shortcut.

## Color tokens

| Token | Hex | Use |
|---|---|---|
| `Paper` | `#FBFBFC` | Window and panel surface |
| `PaperEdge` | `#C9CDD3` | 1px outer border of a window |
| `Rule` | `#E4E7EB` | Chip borders, pressed row fill, chrome hover |
| `Hover` | `#F1F2F4` | Row hover fill, disabled icon tile |
| `Well` | `#EEF0F3` | Page background behind grouped cards (Settings) |
| `Ink` | `#15181C` | Primary text, wordmark, icon tile for the logo |
| `Ink2` | `#5B636D` | Secondary text, hints, chrome icons, pin off state |
| `Ink3` | `#9AA2AB` | Section labels, chips, version tag, neutral status |
| `Tangerine` | `#F0612A` | Primary action fill, pin on state, row icons, focus ring |
| `TangerineDeep` | `#E2561F` | Primary action hover |
| `TangerineSoft` | `#FDEAE2` | Tint behind row icons |
| `TangerineInk` | `#FFF5F0` | Text and glyphs on a tangerine fill |
| `Ok` | `#1F9D6A` | Status dot after a save or copy |
| `Danger` | `#D64545` | Status dot after a failure |

Semantic colors (`Ok`, `Danger`) are separate from the accent and never used for decoration.

Every window uses this palette: the capture window, Settings, Preview, the recording and scrolling control panel, and the region overlay's hint and action bar. The default control styles in `App.xaml` (Button, PrimaryButton, TextBox, CheckBox toggle, RadioButton, ComboBox, Slider) are already themed, so a new window gets the look by using them. Standard windows call `WindowAppearance.Apply` so the native title bar is paper with ink text and rounded corners.

## Typography

Font: **Segoe UI Variable** with Segoe UI fallback. Mono: **Cascadia Mono** with Consolas fallback (`MonoFont` resource).

| Role | Size | Weight | Color |
|---|---|---|---|
| Wordmark | 13 | Bold | `Ink` |
| Primary action label | 16 | Bold | `TangerineInk` |
| Primary action hint | 11.5 | SemiBold, 85% opacity | `TangerineInk` |
| Row title | 13.5 | Bold | `Ink` (`Ink2` when disabled) |
| Row hint | 11.5 | Normal | `Ink2` |
| Section label | 10.5 | Bold, uppercase | `Ink3` |
| Chip / shortcut | 10.5 mono | Normal | `Ink3` (on paper) or `TangerineInk` (on accent) |
| Footer status | 11 | SemiBold | `Ink2` |
| Footer link | 11 | Bold | `Ink`, `Tangerine` on hover |
| Version tag | 10 mono | Normal | `Ink3` |

Sizes are device-independent pixels as WPF `FontSize`.

## Spacing and shape

Base unit is 4px. Use these values and avoid inventing new ones.

| Where | Value |
|---|---|
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
|---|---|
| Window | 16 |
| Primary action | 12 |
| Row, icon tile on primary | 10 |
| Row icon tile | 9 |
| Chrome button | 6 |
| Chip, shortcut cap | 5 |
| Logo tile | 4 |

Shadow on a window: color `#14181C`, blur 40, depth 10, opacity 0.22.

## Components

**Window chrome.** 30×30 icon-only buttons using Segoe Fluent Icons at 13pt, `Ink2`, no border. Hover fills with `Rule`. Order from left: pin, settings, minimize, close. The pin shows `Tangerine` when on and `Ink2` when off.

**Primary action.** Full-width tangerine block with a 40px icon tile at 18% white, a bold label, a one-line hint, and the shortcut in a mono chip at 18% black on the right. Only one per window.

**Row.** Borderless button, `Hover` fill on mouse over, `Rule` on press. Layout is icon tile, title plus hint, optional right-aligned chip. Row icons sit on `TangerineSoft` in `Tangerine`. A disabled row uses `Hover` for the tile, `Ink3` for the glyph, `Ink2` for the title and a `SOON` chip.

**Chip.** 1px `Rule` border, radius 5, 6×3 padding, mono text in `Ink3`. Shows a real shortcut only when that shortcut is actually registered (for example the Print Screen chip appears only when Print Screen is set to Full desktop).

**Section label.** Uppercase, 10.5 bold, `Ink3`, inset 4px past the content edge.

**Footer.** No rule above it. A 7px status dot, the status text trimmed with an ellipsis and a tooltip holding the full message, and a text link on the right.

**Settings group.** Cards use the `Group` style: paper fill, `PaperEdge` border, radius 12, 20px padding, on a `Well` page background. A `GroupLabel` (uppercase, 10.5 bold, `Ink3`) heads each card. Toggles sit on the right of a bold title and a `Hint` line, separated by `Divider` rules. Dividers are allowed inside a card because the rows are settings, not actions.

**Preview well.** Captures sit on a light checkerboard (`#F3F4F6` and `#E6E8EC`, 10px squares) inside a `PaperEdge` border so transparent and white images both read.

**Overlay chrome.** On the region selector the selection stroke and glow are `Tangerine`. The hint and the action bar are paper panels with a `PaperEdge` border and a shadow. The first action is the primary button.

**Focus.** Every interactive element shows a 2px `Tangerine` border when keyboard focused. On the tangerine primary action the ring is `Ink` instead.

## Writing

- Labels name what happens: "Capture region", "Open folder". Not "Go" or "OK".
- Hints are one short line in plain words: "Every monitor, saved instantly".
- Status messages lead with the outcome: "Saved: …", "Capture cancelled.", "Capture failed: …". The status dot color is derived from these words, so keep them.
- Never show a shortcut the app has not registered.
