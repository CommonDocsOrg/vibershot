<img src="docs/images/logo.png" alt="Vibershot logo" width="96" align="left" style="margin-right:16px">

# Vibershot

**Screenshots, screen recordings and annotations for Windows 11, without the clutter.**

Vibershot is a small, fast capture tool. Press a key, pick what you want on screen, and it is saved, copied, recorded or opened for editing. Everything you capture lands in a searchable gallery, and nothing you draw on a picture is ever baked in until you export it.

<br clear="all">

![The Vibershot launcher](docs/images/launcher.png)

## Why Vibershot

- **One key, any part of the screen.** Print Screen or Ctrl+Shift+S opens the capture tool over whatever you are looking at.
- **Smart selection.** Hover and Vibershot outlines the window, panel or page content under the cursor. Click to take it, or drag your own region.
- **Still or moving.** The same selection becomes a screenshot, an MP4 recording or a stitched scrolling capture.
- **Edit without losing anything.** Arrows, shapes, text, crops and layers stay editable in the Vibershot project format.
- **A home for every capture.** The gallery keeps everything, with search, tags and favourites.
- **Lightweight.** A native Windows app that sits in the tray and does nothing until you ask it to.

## Capture any region

Press the hotkey and move the mouse. Windows and app content light up as you pass over them, so grabbing a dialog, a browser page or a panel is a single click. Hold Shift to take the whole window frame, or drag to mark out exactly the area you want.

![Hovering over a window outlines it automatically](docs/images/autoselect.png)

Once you have a selection, choose what happens next: save a screenshot, record the area as a video, or build a scrolling capture one shot at a time. Press Esc at any point to cancel. **Full desktop** grabs every monitor at once, including multi-monitor layouts.

![Pick a screenshot, video or scrolling photo from the selection](docs/images/capture-chooser.png)

After a capture, Vibershot does what you have told it to: save to the library, copy to the clipboard, open the editor, open the gallery, or just show a small preview with **Copy**, **Save as…** and **Edit**. Presets switch between "quick copy", "save to library", "save and edit" and "save and review" in one click.

## Record video

Choose **Take video** and the selected region is recorded as an H.264 MP4 at the frame rate you pick, using your GPU's hardware encoder when one is available. A thin outline marks the recorded area and never appears in the footage, and the control panel shows live frame rate, elapsed time and file size. Pause and resume as often as you like. Recordings are written in a crash-safe format while they run, so an unexpected shutdown still leaves a playable file.

![Recording a window with the live status panel](docs/images/video-recording.png)

## Annotate in the editor

Captures open in a layered editor. Draw arrows, rectangles, ellipses and lines, add text, place other images, crop, and move anything later. Items live on layers that can be hidden, locked, reordered and faded. Marquee-select pixels to lift, move, copy or clear them. Undo and redo cover every step.

![Annotating a capture with arrows, shapes and text](docs/images/editor.png)

Save as a **Vibershot project** (`.vibeshot`) to keep text, shapes and layers editable, or **Export** a flat PNG, JPEG or WebP with a live preview of the compression result. **Copy image** puts the finished picture on the clipboard for pasting anywhere.

## Find it again in the gallery

Every capture saved to the library shows up in the gallery, grouped by day, with the app and window it came from. Filter by images, videos, projects or favourites, search by file name, app, window title or tag, and sort by date, name or size. Rename, tag, favourite, copy or delete from the keyboard. Drag tiles out to other apps, or drop files in to import them. The gallery can stay open as a home base: **Capture region** in its header starts a new capture and shows the result selected.

![The gallery with search, tags and a details pane](docs/images/gallery.png)

Old screenshots and recordings can be moved to the Recycle Bin automatically after a number of days you choose. The library is a plain folder of files, so it works with Explorer, backups and sync tools.

## Install

1. Download `VibershotSetup-<version>.exe` from the [latest release](https://github.com/CommonDocsOrg/vibershot/releases/latest).
2. Run it. The installer works per user and needs no administrator rights. It asks where your library should live and whether to start with Windows.
3. Press **Print Screen** or **Ctrl+Shift+S** and capture something.

Vibershot is self-contained: nothing else needs to be installed. It checks for updates once a day and can download and install a new version in place.

Requires Windows 11 (Windows 10 version 2004 or later also works).

## Default shortcuts

| Action | Shortcut |
|---|---|
| Capture a region or window | Print Screen, Ctrl+Shift+S |
| Full desktop | Set in Settings → Hotkeys |
| Record video | Set in Settings → Hotkeys |
| Open the gallery | Set in Settings → Hotkeys |
| Open the editor | Set in Settings → Hotkeys |

Every shortcut can be changed in Settings. If you would rather keep Print Screen for Windows, turn the takeover off there.

## Build from source

Vibershot is a C# / .NET 10 / WPF application. With the .NET 10 SDK installed:

```powershell
.\run.ps1
```

This builds the app, fetches the FFmpeg binaries used for MP4 recording and starts Vibershot. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for the project layout, settings reference and manual test checklist, and [docs/RELEASING.md](docs/RELEASING.md) for building the installer and publishing a release.

## License

Vibershot is free software released under the [GNU General Public License v3.0](LICENSE). MP4 recording uses a separately downloaded FFmpeg build; its licence ships alongside it.
