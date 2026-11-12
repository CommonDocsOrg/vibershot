# Vibershot — runnable screenshot app

Windows desktop app using C# / .NET 10 / WPF. No browser, Electron, third-party packages, or C++ build is needed for the current screenshot flow. Capture calls the Windows GDI APIs directly; the process is idle between user actions.

## Run

Install the .NET 10 SDK (already present on this development machine), then run from this folder:

```powershell
.\run.ps1
```

Or build directly:

```powershell
dotnet build .\Vibershot.UI\Vibershot.UI.csproj -c Release
.\Vibershot.UI\bin\Release\net10.0-windows\Vibershot.exe
```

The rounded floating launcher can be moved by dragging its header. Its custom controls toggle always-on-top, minimize, and close the app. The glass-like surface uses translucency and static highlights without a continuous blur or capture loop. Click **Capture region**, then hover over a window or app content to see a glowing outline. Click to select the highlighted area, hold **Shift** for the whole window, or drag over the desktop for a manual region. Chromium render hosts and accessible document bounds identify browser pages without hardcoded toolbar heights; other apps fall back to their client area or visible window frame. Release to keep the selection highlighted while the unselected desktop is dimmed. Choose **Save screenshot**, **Take video**, **Scrolling photo**, or **Cancel**. Drag again to replace the selection; **Esc** cancels. **Ctrl+Shift+S** also starts region capture. Settings can also **take over Print Screen** while Vibershot runs, either opening the capture region/window tool (default) or snapping the full desktop immediately; this is off by default, and if Windows already routes Print Screen to Snipping Tool the launcher reports the conflict and points to the Windows setting to turn off. **Full desktop** captures the virtual desktop across monitors, including monitors to the left of the primary display.

The app hides its own windows before capture. A preview opens with **Copy image** and **Save as…** (PNG, JPEG, WebP lossy or WebP lossless). Settings → Files → **Image format** picks the autosave format; choosing **WebP · custom quality** reveals a 1–100% quality slider (default 80), while **WebP lossless** keeps exact pixels in a file smaller than PNG. WebP encoding uses SkiaSharp since WPF has no in-box WebP encoder. Settings control the region action chooser, whether **Save As** opens after a photo, automatic saving, clipboard copying, and the output folder. Turn off the chooser and leave automatic saving enabled for immediate screenshot saving. Choosing **Save screenshot** explicitly saves to the configured folder unless **Save As** is enabled; cancelling Save As leaves the image available in preview. By default screenshots save under `%APPDATA%\Vibershot\Screenshots`. Closing the launcher hides it to the tray; Vibershot keeps running so hotkeys and Print Screen still work. Use the tray icon to show the launcher again or choose **Exit** to quit. **Start with Windows** in Settings adds a per-user Run entry that launches Vibershot straight into the tray (`Vibershot.exe --tray`) at sign-in; the entry is refreshed on each start so it follows a moved executable.

## Editor

**Edit** on the preview (the primary button there) opens the capture in the image editor; the launcher's **Editor** row opens whatever image is on the clipboard, or a blank 1280×720 canvas. Setting `Editor.OpenEditorAfterCapture` to `true` in `config.json` skips the preview and opens the editor after every screenshot.

The editor is a layered vector document: the capture is an image item on a base layer, annotations go on a layer above, and nothing is baked until export. Tools on the left rail, with keyboard letters: **Select** (V), **Marquee** (M), **Crop** (C), **Arrow** (A), **Shape** (S; press again to cycle rectangle, ellipse and line, or open the flyout), **Text** (T) and **Image** (I, places an image file). Drag to draw; Shift constrains to squares and 45° angles. Click selects, Shift-click adds, dragging on empty space selects every item the box touches; the selected item shows eight resize handles and a rotate handle; arrow keys nudge by 1 px (Shift: 10 px); Delete removes; Ctrl+A selects all; the context menu moves items forward and back. Double-click a text item (or finish creating one) to type in place; the properties panel on the right edits stroke, fill, width, opacity, corner radius, arrow head size, drop shadow, and for text the font family, size, bold/italic/underline, colour, alignment, background and padding. Ctrl+wheel zooms around the pointer, **Fit** and **100%** set the view, and Space-drag or the middle button pans.

**Crop** draws a rectangle with handles; Enter (or **Apply crop**) keeps it as a non-destructive crop and **Reset crop** brings everything back. **Marquee** selects pixels: Ctrl+C then copies that region as a flat image. Copy, Cut and Paste work on items (Ctrl+C/X/V, Ctrl+D duplicates): items travel as JSON under the private `Vibershot.Items` clipboard format next to a flattened PNG, so they paste into the editor as editable items and into other apps as a picture; an image on the system clipboard pastes as an image item centred in view (images inside images), text pastes as a text box, and image files dropped on the canvas become image items. Undo and redo (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z) treat each drag as one step.

**Save project** (Ctrl+S) writes a `.vibeshot` file (a zip with `document.json` and the embedded PNG assets) that reopens with every item still editable via **Open…** or by dropping it on the canvas; **Export…** (Ctrl+E) flattens to PNG, JPEG or WebP through the same encoder as the preview, honouring the crop and rendering tall canvases in 4096 px bands; **Copy image** puts the flattened picture on the clipboard. Closing with unsaved edits asks to save, discard or cancel. Defaults for new items live under `Editor` in `config.json` (`DefaultFontFamily`, `DefaultFontSize`, `DefaultStrokeColor`, `DefaultStrokeWidth`).

## Layout and next steps

- `Vibershot.UI`: floating launcher, hotkey, region overlay, preview, editor, settings, tray.
- `Vibershot.Core`: one-shot screen capture, clipboard, image encoding, settings.
- `Vibershot.Editing`: the editor's document model, history, renderer, project file and item clipboard (no windows, so the console checks can test it).
- `Vibershot.Media`: ffmpeg process runner, ffprobe, encoder probe, recording argument builders and the MP4 recorder session (no WPF windows).
- `Vibershot.Engine`: original C++ stub, retained but not built or loaded by this version.

Working now: region and full-desktop screenshots, compact launcher, dimmed region action chooser, preview, image editor with arrows, shapes, text, placed images, crop and projects, PNG/JPEG/WebP output, clipboard, persisted settings, MP4 region video at a chosen frame rate with pause/resume, and manually advanced scrolling/stitching. `tests\CaptureChecks` is a plain console program; it runs the live ffmpeg checks when `tools\ffmpeg` is present and skips them otherwise.

**Video:** **Take video** records the selected region as an H.264 MP4 through the bundled FFmpeg (see below). Settings → Video picks the format (MP4, or the legacy AVI), the frame rate (24 / 25 / 29.97 / 30 / 50 / 59.94 / 60, stored as an exact rational such as `30000/1001`), a 1–100 quality slider mapped to the encoder's CRF/CQ, whether to prefer a hardware encoder (NVIDIA NVENC, Intel Quick Sync, AMD AMF; software `libx264`, Media Foundation or OpenH264 otherwise; probed once per session with a one-second null encode) and whether to draw the cursor. Capture uses Desktop Duplication (`ddagrab`) on the monitor containing the region and drops to GDI `gdigrab` when it produces no frames within three seconds; the session panel shows the real fps, elapsed time, frame count, file size, encoder and grabber. While recording, ffmpeg writes a fragmented MP4 under `%LOCALAPPDATA%\Vibershot\Recordings` so a crash still leaves a playable file; **Pause** ends a segment and **Resume** starts another; **Stop & save…** joins the segments (stream copy) into a normal faststart MP4 and offers a Save As (Cancel keeps the recovery copy). There is no audio yet. If ffmpeg is missing or both grabbers fail, the session falls back to the original GDI Motion JPEG AVI recorder at 10 fps and says so in its status line; that recorder stops at 1.8 GB. Windows capture exclusion keeps session controls out of captures on supported Windows versions; keep controls outside the region on older systems.

## FFmpeg

MP4 recording runs `ffmpeg.exe` as a child process; nothing links against its libraries. The binaries are not in the repository: `tools\get-ffmpeg.ps1` downloads the current `ffmpeg-master-latest-win64-lgpl` build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases) (GPL only if the LGPL asset is unavailable) into `tools\ffmpeg\` (`ffmpeg.exe`, `ffprobe.exe`, `LICENSE`, `SOURCE.txt`), which is git-ignored. `run.ps1` calls the script when `ffmpeg.exe` is absent, and `Vibershot.UI.csproj` copies the folder to `ffmpeg\` next to the executable. At run time `FfmpegLocator` looks in that output folder, then `tools\ffmpeg` above the executable for development builds, then `PATH`; the launcher footer reports a missing or broken binary at startup. The LGPL build has no `libx264`; it uses the GPU encoders, Media Foundation or OpenH264 instead. Keep `tools\ffmpeg\LICENSE` with any redistributed copy: the LGPL build must be shipped as separate, replaceable executables, which is how Vibershot uses it.

**Scrolling photo:** the initial region becomes the first shot. Scroll the browser down by less than a viewport, click **Add shot**, and return to the browser during the two-second delay. Keep at least 20% overlap, exclude sticky browser/page headers, and avoid moving content. The app aligns each shot vertically and appends the new content. Finish opens the usual photo save/preview flow; Cancel discards the session. This is a manually advanced browser stitcher, not automated full-page browser capture. Images are limited to 80 megapixels.

Still to implement: automatic browser scrolling, editor depth (callouts, stamps, blur and highlight items, a layers panel, rich text runs, canvas resizing, editor settings in the Settings window), microphone/system audio, a video editor, the gallery, and packaging (with the ffmpeg binaries and license). GDI is appropriate for still captures; video uses DXGI Desktop Duplication and hardware encoding through ffmpeg. The old `../plan.md` describes aspirations and contains outdated build/API assumptions; use this README for the current build.

## Manual smoke checks

1. Start the launcher; capture a region and verify preview dimensions match the selected area.
2. Check that the launcher and selection border are absent in the output.
3. Paste the copied image into an image-capable application; check the autosaved PNG.
4. Capture the full desktop; test a monitor with negative coordinates if available.
5. Press Esc during selection; the launcher should return without creating a capture.
6. Disable the action chooser and autosave/clipboard in Settings; capture and save manually from the preview. Test Save As enabled/disabled independently, including cancelling the dialog.
7. Check Ctrl+Shift+S from another app; if occupied, the launcher reports the conflict.

Protected video surfaces and secure desktops are outside the current capture scope. Mixed-DPI monitor layouts should receive additional testing on real hardware.

8. Select a region and cancel using the chooser; verify no screenshot is saved. Redraw the region and check dimming follows the new selection.
9. Record a short MP4 with Pause and Resume; the status line should show the encoder, grabber, fps and size. Stop & save and play it in Windows Media Player and VLC; the frame rate must match Settings → Video. Switch the format to AVI, record again and play it in an AVI-capable player. Cancel a Save As; the recovery copy under `%LOCALAPPDATA%\Vibershot\Recordings` must remain.
10. Stitch overlapping browser shots, then test unchanged content and an excessive scroll; alignment failures should keep the previous image intact.

11. Hover over a Chrome/Edge page: verify the outline excludes tabs and address bar. Hold Shift to select the visible window frame. Click and test screenshot/video/scrolling actions. Test another app, overlapping windows, maximized windows, negative monitor coordinates, and mixed DPI. Drag to override an unsuitable automatic area. Window/content capture records a fixed visible desktop rectangle; it does not follow moved windows or capture through occluding windows.
12. Enable **Take over Print Screen** in Settings; press Print Screen from another app with each mode. Disable it and confirm Windows handles the key again without restarting.
13. Capture a region and click **Edit**. Draw an arrow (A), add a text box (T) and type, copy an image elsewhere and Ctrl+V it into the picture, crop (C, drag, Enter), then undo three times and redo. Export a PNG and check it matches the canvas and the crop. Save the project, close, reopen it with **Open…** and confirm every item is still selectable and editable; close with unsaved edits and check the Save / Discard / Cancel prompt.
14. Run `tests/CaptureChecks` (`dotnet build tests/CaptureChecks/CaptureChecks.csproj -c Release` then the built exe); it must print `PASS`, covering history undo/redo/coalescing, document operations, project round trips, JSON polymorphism and banded flattening.
