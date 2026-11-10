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

The rounded floating launcher can be moved by dragging its header. Its custom controls toggle always-on-top, minimize, and close the app. The glass-like surface uses translucency and static highlights without a continuous blur or capture loop. Click **Capture region**, then drag over the desktop. Release to keep the selection highlighted while the unselected desktop is dimmed. Choose **Save screenshot**, **Take video**, **Scrolling photo**, or **Cancel**. Drag again to replace the selection; **Esc** cancels. **Ctrl+Shift+S** also starts region capture. **Full desktop** captures the virtual desktop across monitors, including monitors to the left of the primary display.

The app hides its own windows before capture. A preview opens with **Copy image** and **Save as…** (PNG/JPEG). Settings control the region action chooser, whether **Save As** opens after a photo, automatic saving, clipboard copying, and the output folder. Turn off the chooser and leave automatic saving enabled for immediate screenshot saving. Choosing **Save screenshot** explicitly saves to the configured folder unless **Save As** is enabled; cancelling Save As leaves the image available in preview. By default screenshots save under `%APPDATA%\Vibershot\Screenshots`. A tray menu provides access while the launcher is minimized. Closing the launcher exits the app.

## Layout and next steps

- `Vibershot.UI`: floating launcher, hotkey, region overlay, preview, settings, tray.
- `Vibershot.Core`: one-shot screen capture, clipboard, image encoding, settings.
- `Vibershot.Engine`: original C++ stub, retained but not built or loaded by this version.

Working now: region and full-desktop screenshots, compact launcher, dimmed region action chooser, preview, PNG/JPEG output, clipboard, persisted settings, basic region video, and manually advanced scrolling/stitching.

**Video:** choose an AVI destination, then record the selected region at a target of 10 fps using Motion JPEG. Finish keeps the video; Cancel discards the temporary recording. There is no audio. Recording stops at 1.8 GB. Windows capture exclusion keeps session controls out of captures on supported Windows versions; keep controls outside the region on older systems. This basic GDI recorder is intended for short clips, rather than high-frame-rate recording.

**Scrolling photo:** the initial region becomes the first shot. Scroll the browser down by less than a viewport, click **Add shot**, and return to the browser during the two-second delay. Keep at least 20% overlap, exclude sticky browser/page headers, and avoid moving content. The app aligns each shot vertically and appends the new content. Finish opens the usual photo save/preview flow; Cancel discards the session. This is a manually advanced browser stitcher, not automated full-page browser capture. Images are limited to 80 megapixels.

Still to implement: individual window capture, automatic browser scrolling, annotations and editing, hardware-accelerated video, microphone/system audio, and packaging. GDI is appropriate for initial still captures; a sustained video pipeline should use Windows Graphics Capture or DXGI and hardware encoding. The old `../plan.md` describes aspirations and contains outdated build/API assumptions; use this README for the current build.

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
