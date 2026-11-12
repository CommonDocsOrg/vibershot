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

## Layout and next steps

- `Vibershot.UI`: floating launcher, hotkey, region overlay, preview, settings, tray.
- `Vibershot.Core`: one-shot screen capture, clipboard, image encoding, settings.
- `Vibershot.Engine`: original C++ stub, retained but not built or loaded by this version.

Working now: region and full-desktop screenshots, compact launcher, dimmed region action chooser, preview, PNG/JPEG/WebP output, clipboard, persisted settings, basic region video, and manually advanced scrolling/stitching.

**Video:** choose an AVI destination, then record the selected region at a target of 10 fps using Motion JPEG. Finish keeps the video; Cancel discards the temporary recording. There is no audio. Recording stops at 1.8 GB. Windows capture exclusion keeps session controls out of captures on supported Windows versions; keep controls outside the region on older systems. This basic GDI recorder is intended for short clips, rather than high-frame-rate recording.

**Scrolling photo:** the initial region becomes the first shot. Scroll the browser down by less than a viewport, click **Add shot**, and return to the browser during the two-second delay. Keep at least 20% overlap, exclude sticky browser/page headers, and avoid moving content. The app aligns each shot vertically and appends the new content. Finish opens the usual photo save/preview flow; Cancel discards the session. This is a manually advanced browser stitcher, not automated full-page browser capture. Images are limited to 80 megapixels.

Still to implement: automatic browser scrolling, annotations and editing, hardware-accelerated video, microphone/system audio, and packaging. GDI is appropriate for initial still captures; a sustained video pipeline should use Windows Graphics Capture or DXGI and hardware encoding. The old `../plan.md` describes aspirations and contains outdated build/API assumptions; use this README for the current build.

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
9. Record a short AVI; Finish and play it in an AVI-capable player. Start another recording and Cancel; an existing destination must remain intact.
10. Stitch overlapping browser shots, then test unchanged content and an excessive scroll; alignment failures should keep the previous image intact.

11. Hover over a Chrome/Edge page: verify the outline excludes tabs and address bar. Hold Shift to select the visible window frame. Click and test screenshot/video/scrolling actions. Test another app, overlapping windows, maximized windows, negative monitor coordinates, and mixed DPI. Drag to override an unsuitable automatic area. Window/content capture records a fixed visible desktop rectangle; it does not follow moved windows or capture through occluding windows.
12. Enable **Take over Print Screen** in Settings; press Print Screen from another app with each mode. Disable it and confirm Windows handles the key again without restarting.
