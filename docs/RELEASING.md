# Releasing Vibershot and shipping updates

Everything runs on your own PC: no GitHub Actions, no build minutes. A release is a tag plus a
GitHub release with two assets, and installed copies find new versions by reading one of them.

## How it fits together

| Piece | Where | Job |
|---|---|---|
| `tools\installer\Vibershot.iss` | repo | Inno Setup script: wizard pages, Run key, `.vibeshot` association, silent-update support |
| `tools\build-installer.ps1` | repo | Publishes the app self-contained, compiles the installer, writes `latest.json` |
| `tools\publish-release.ps1` | repo | Tags the commit and creates the GitHub release with both assets via `gh` |
| `VibershotSetup-<version>.exe` | release asset | The installer users download and the app downloads for updates |
| `latest.json` | release asset | Version, installer URL, SHA-256, size, notes. The app reads it from the *latest* release |
| `Vibershot.Core\Updates.cs` | repo | `AppVersion`, `UpdateManifest`, `UpdateService`, `InstallChoices` |
| `Vibershot.UI\UpdateWindow.xaml` | repo | The "A new version is ready" dialog |

The app reads the feed at

```
https://github.com/CommonDocsOrg/vibershot/releases/latest/download/latest.json
```

GitHub redirects `releases/latest/download/<asset>` to the asset on whichever release is marked
**Latest**. Drafts and pre-releases are never "latest", so they are invisible to installed copies.

## One-time setup

1. Install Inno Setup 6 (per-user is fine): `winget install --id JRSoftware.InnoSetup -e`
2. Install and sign in to the GitHub CLI: `winget install --id GitHub.cli -e`, then `gh auth login`
3. Make sure `tools\ffmpeg\` exists (`tools\get-ffmpeg.ps1`); the build script fetches it if missing.

## Cutting a release

1. **Bump the version** in `Vibershot.UI\Vibershot.UI.csproj` (`<Version>0.2.0</Version>`). This is
   the only place the version lives; the installer, the `Apps & features` entry, the launcher
   footer and the update check all read it from there. Use `1.2.3` or `1.2.3-beta.1`.
2. **Commit** that change and anything else going into the release.
3. **Build** from `src\` (exit Vibershot first; the publish step overwrites files it holds open):

   ```powershell
   .\tools\build-installer.ps1 -Notes .\RELEASE_NOTES.md -RunChecks
   ```

   `-Notes` takes a file or a string; it is shown in the update dialog. `-RunChecks` runs
   `tests\CaptureChecks` first and stops on failure. Output lands in `artifacts\installer\`:
   `VibershotSetup-0.2.0.exe` and `latest.json`.
4. **Rehearse the installer** at least once per release (see below). It takes a minute.
5. **Publish** as a draft first, look at it on GitHub, then make it live:

   ```powershell
   .\tools\publish-release.ps1            # creates tag v0.2.0 and a draft release with both assets
   .\tools\publish-release.ps1 -Publish   # flips the draft to published and marks it Latest
   ```

   Or skip the draft with `-Publish` straight away. Without `gh`, upload the two files by hand to a
   release named `v<version>` and tick **Set as the latest release**.

Within a day every installed copy with the automatic check on sees the new version.

## What users see

- **Fresh install.** Wizard: licence, per-user or all-users, install folder, **library folder**
  (defaults to `Pictures\Screenshots\Vibershot Gallery`), then tasks: start with Windows (on),
  `.vibeshot` association (on), desktop shortcut (off). The installer writes
  `%APPDATA%\Vibershot\install-choices.json`; the app's first launch turns that into a fresh
  `config.json`, so the welcome page only asks about Print Screen.
- **Upgrade by hand.** Running a newer installer over an existing copy keeps the folder, the task
  choices and the per-user/all-users mode without asking (`UsePrevious*` in the script). The
  library page is skipped whenever a `config.json` exists.
- **Automatic update.** Twenty seconds after launch, at most once a day, the app fetches
  `latest.json`. If the version is newer it shows the update dialog with the notes. *Install and
  restart* downloads the installer to `%LOCALAPPDATA%\Vibershot\Updates`, checks the SHA-256
  against the feed, runs it with `/VERYSILENT ... /UPDATE=1`, and exits. The installer replaces the
  files and relaunches `Vibershot.exe --updated`, which reports "Updated to Vibershot x.y.z" in the
  launcher footer. *Later* asks again next day; *Skip this version* silences that version.
  Settings › General has the switch and a **Check now** link; the tray menu has **Check for updates…**.
- **Uninstall.** Removes the app, Run key, association, update downloads and thumbnail cache. An
  interactive uninstall asks whether to delete settings. The library is never touched.

## Rehearsing a release without publishing

The feed and the installer URL may be local paths, so a build can be tested end to end on this PC.

1. Build the installer as above.
2. Install it: double-click the exe, or silently:

   ```powershell
   .\artifacts\installer\VibershotSetup-0.2.0.exe /VERYSILENT /DIR="D:\VibershotTest" /LIBRARY="D:\VibershotTestLib" /MERGETASKS="startup,!fileassoc"
   ```

   `/LIBRARY=` sets the library folder (handy for scripted installs too). A log is written to
   `%TEMP%\Setup Log <date>.txt`.
3. To test the update dialog against this build, give it a higher version in a copy of the feed
   and point the installed copy at it. In `%APPDATA%\Vibershot\config.json`:

   ```json
   "Updates": { "CheckAutomatically": true, "FeedUrl": "D:\\test\\latest.json", "LastCheckUtc": null }
   ```

   with `D:\test\latest.json` holding `"version": "9.9.9"` and `"installerUrl": "E:\\...\\VibershotSetup-0.2.0.exe"`
   (keep the real `sha256`). Launch the app; the dialog appears after twenty seconds and *Install
   and restart* runs the whole silent update cycle. Remove `FeedUrl` afterwards.
4. Uninstall from Apps & features or `unins000.exe /VERYSILENT` in the install folder.

## Rules that keep updates working

- **Never change `AppId`** in `Vibershot.iss`. It is how a newer installer recognises the old copy.
- **Keep the asset names.** The feed points at `VibershotSetup-<version>.exe`; the app also names
  its download after it. `latest.json` must be attached to the release that is marked Latest.
- **Keep the version monotonic.** The app only offers versions that sort above the running one
  (`AppVersion.Compare`: numeric parts, then `-beta` style tags sort below the release).
- **Config changes must be additive.** An update keeps the user's `config.json`; new settings
  need defaults and old sections must still load (see `AppConfig.MigrateLegacy`).
- **A breaking installer change** (new AppId, new folder layout) should set `"minimumVersion"` in
  `latest.json` to the first version that can upgrade silently; older copies then get a
  *Download page* button instead of the in-place install.

## Not done yet

- **Code signing.** The installer is unsigned, so SmartScreen warns on download until enough
  people have run it. Azure Trusted Signing is the cheapest fix; `signtool` would run in
  `build-installer.ps1` after `ISCC` and the app would verify the Authenticode signature in addition
  to the SHA-256.
- **Delta updates.** Every update downloads the full installer (about 145 MB, mostly the .NET
  runtime and ffmpeg). Velopack would bring deltas but drops the install-folder and library pages.
- **An app icon.** `SetupIconFile` and the shortcut icons fall back to the default executable icon.
