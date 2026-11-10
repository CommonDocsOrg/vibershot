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

The small always-on-top launcher can be moved using its title bar. Click **Capture region**, then drag over the desktop. Release to capture, or press **Esc** to cancel. **Ctrl+Shift+S** also starts region capture. **Full desktop** captures the virtual desktop across monitors, including monitors to the left of the primary display.

The app hides its own windows before capture. A preview opens with **Copy image** and **Save as…** (PNG/JPEG). Settings control automatic saving, clipboard copying, and the output folder. By default screenshots save under `%APPDATA%\Vibershot\Screenshots`. A tray menu provides access while the launcher is minimized. Closing the launcher exits the app.

## Layout and next steps

- `Vibershot.UI`: floating launcher, hotkey, region overlay, preview, settings, tray.
- `Vibershot.Core`: one-shot screen capture, clipboard, image encoding, settings.
- `Vibershot.Engine`: original C++ stub, retained but not built or loaded by this version.

Working now: region and full-desktop screenshots, preview, PNG/JPEG output, clipboard, persisted settings.

Still to implement: individual window capture, scrolling/stitching, annotations and editing, video recording, microphone/system audio, and packaging. GDI is appropriate for initial still captures; a sustained video pipeline should use Windows Graphics Capture or DXGI and hardware encoding. The old `../plan.md` describes aspirations and contains outdated build/API assumptions; use this README for the current build.

## Manual smoke checks

1. Start the launcher; capture a region and verify preview dimensions match the selected area.
2. Check that the launcher and selection border are absent in the output.
3. Paste the copied image into an image-capable application; check the autosaved PNG.
4. Capture the full desktop; test a monitor with negative coordinates if available.
5. Press Esc during selection; the launcher should return without creating a capture.
6. Disable autosave/clipboard in Settings; capture and save manually from the preview.
7. Check Ctrl+Shift+S from another app; if occupied, the launcher reports the conflict.

Protected video surfaces and secure desktops are outside the current capture scope. Mixed-DPI monitor layouts should receive additional testing on real hardware.
