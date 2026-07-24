# ClipStack

ClipStack is a lightweight Windows clipboard-history utility. It remembers the latest copied items and lets you paste any of them with a global shortcut.

It is a native **C# / WPF / .NET 10** desktop app (not Electron, not Chromium, not a web UI). The goal is low idle CPU, low memory, and fast popup opening.

## Features

- Event-driven clipboard capture (`AddClipboardFormatListener`) — no polling
- Default history of **50** items (configurable 1–50)
- Global shortcut default: **Ctrl + Shift + S**
- Compact floating popup near the cursor
- System tray app (no taskbar window during normal use)
- Local-only storage under `%LocalAppData%\ClipStack`
- Velopack installer + automatic GitHub update support

## Supported clipboard types

| Kind | Behavior |
|------|----------|
| Text | Unicode / plain text, including large text, URLs, multiline |
| Rich text | Text + HTML + RTF stored as one history item |
| Images | Bitmap clipboard images stored as PNG + thumbnail |
| Files/folders | File-drop path lists only (files are not copied into ClipStack) |

Private/app-specific clipboard formats are ignored safely.

## Privacy

Clipboard history is stored **only on this device**. ClipStack does not upload clipboard contents, does not include clipboard data in logs, and does not sync between devices.

Update checks only request public ClipStack release metadata from GitHub. Clipboard contents are never included.

## Default shortcut and keyboard controls

**Open history:** `Ctrl + Shift + S`

In the popup:

| Key | Action |
|-----|--------|
| ↑ / ↓ | Change selection |
| Enter | Paste selected |
| `1`–`9` | Paste items 1–9 |
| `0` | Paste item 10 |
| Delete | Remove selected item |
| Esc | Hide popup |

Mouse click also pastes. Window deactivation hides the popup.

## Tray menu

- Show Clipboard History
- Pause / Resume Clipboard Capture
- Clear History
- Settings
- Check for Updates
- Start with Windows
- Exit ClipStack

Double-click the tray icon to show history.

## Data location

```text
%LocalAppData%\ClipStack\
  index.json
  settings.json
  release-config.json
  logs\
  items\<guid>\...
```

Clear local data:

```powershell
.\tools\clean-local-data.ps1
```

## Prerequisites

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build / test / run

```powershell
cd C:\Users\souna\Projects\ClipStack

dotnet restore .\ClipStack.sln
dotnet build .\ClipStack.sln -c Release
dotnet test .\ClipStack.sln -c Release

# Run (tray app)
dotnet run --project .\src\ClipStack.App\ClipStack.App.csproj -c Release
# or
.\src\ClipStack.App\bin\Release\net10.0-windows\ClipStack.exe
```

Approximate idle memory (does not kill an existing instance):

```powershell
.\tools\measure-memory.ps1
```

## Release packaging (Velopack)

Pinned package/tool version: **Velopack / vpk 1.2.0**

```powershell
.\tools\build-release.ps1 -Version 1.0.1
```

This will:

1. Restore tools and packages
2. Run tests
3. Publish self-contained `win-x64` (not single-file)
4. Package with `vpk pack`
5. Write installer/update files under `artifacts\releases`

### Automatic updates

Public releases use the GitHub repository configured in `release-config.json`:

`https://github.com/sounak1125/ClipStack`

When **Download updates automatically** is enabled, ClipStack checks the public
GitHub release feed in the background while the app is open. A newer version is
downloaded silently, then a non-modal notification lets the user choose
**Restart & update** or **Later**. Manual checks remain available from Settings
and the tray menu.

### Publish a new version

Push a semantic-version tag to run the release workflow:

```powershell
git tag v1.0.1
git push origin v1.0.1
```

The GitHub Actions workflow downloads the previous Velopack release metadata,
runs tests, builds the self-contained application, creates full/delta packages,
and publishes the installer and update assets to GitHub Releases.

Installed copies check the `win` release channel and preserve clipboard history
under `%LocalAppData%\ClipStack` across updates.

### Code signing

Sign the final public installer/update packages with your own certificate. Do not commit secrets or signing passwords to the repository.

## Startup with Windows

Installed builds default to enabling per-user startup via:

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `ClipStack`

Debug/unpackaged runs do not auto-enable startup. The tray checkbox and Settings stay synchronized with the registry.

## Known Windows limitations

- Auto-paste uses `SendInput` and cannot paste into elevated windows unless ClipStack itself is elevated (ClipStack intentionally runs `asInvoker` and will not request admin). If auto-paste fails, content remains on the clipboard — press `Ctrl+V`.
- Some apps use private clipboard formats that ClipStack cannot reconstruct.
- Extremely large clipboard payloads can be skipped when over the configured size limit (default 200 MB).

## How to rename the app

Change values in one place:

`src/ClipStack.Core/AppIdentity.cs`

Also update `Directory.Build.props`, `release-config` packaging IDs in `tools/build-release.ps1`, and the tray/menu strings if you customize further.

## How to replace the icon

Replace these generated project assets:

- `src/ClipStack.App/Assets/clipstack-icon.png` — transparent 1024px master
- `src/ClipStack.App/Assets/clipstack.ico` — Windows multi-resolution icon

The `.ico` is embedded into the executable and used by application windows,
the system tray, shortcuts, portable builds, and installer packages.

## Manual validation checklist

### Text
- Copy one line / multiple lines / emoji / non-English / large document
- Paste into Notepad and a browser
- Confirm duplicates do not create duplicate rows

### Rich text
- Copy from browser / Word
- Paste into a rich editor and Notepad
- Confirm plain-text fallback

### Images
- Copy screenshot / browser image
- Confirm popup shows thumbnail only
- Paste original; restart app; confirm persistence

### Files
- Copy file(s) / folder; paste into Explorer via ClipStack
- Delete a source file; confirm missing-path handling

### Popup / hotkey / tray / updates
- Mouse, arrows, Enter, `1`–`0`, Esc, Delete
- Multi-monitor and mixed DPI
- Change shortcut; conflicting shortcut; restart
- Pause/resume; clear; startup toggle; exit; second instance
- Unpackaged updater fails safely; installed update path when feed configured

## Architecture notes

- `ClipStack.Core` — models, hashing, atomic JSON storage (no WPF)
- `ClipStack.App` — tray, WPF popup/settings, Win32 interop, Velopack
- Hidden `HwndSource` window owns clipboard listener + hotkey
- Self-copy suppression avoids restoring history items as new duplicates
