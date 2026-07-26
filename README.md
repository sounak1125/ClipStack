# ClipStack

ClipStack is a lightweight Windows clipboard-history utility. It remembers the latest copied items and lets you paste any of them with a global shortcut.

It is a native **C# / WPF / .NET 10** desktop app (not Electron, not Chromium, not a web UI). The goal is low idle CPU, low memory, and fast popup opening.

## Features

- Event-driven clipboard capture (`AddClipboardFormatListener`) — no polling
- Capture work runs off the UI thread, so large images and files never freeze the popup
- Honours password-manager clipboard opt-out formats
- Default history of **50** items (configurable 1–50)
- Global shortcut default: **Ctrl + Shift + S**
- Compact floating popup near the cursor, with a filter box (`/`)
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

ClipStack only reads the formats above; any other app-specific format is left alone.

## Privacy

Clipboard history is stored **only on this device**. ClipStack does not upload clipboard contents, does not include clipboard data in logs, and does not sync between devices.

Update checks only request public ClipStack release metadata from GitHub. Clipboard contents are never included.

### Excluded clips

Before reading any content, ClipStack checks the clipboard for the opt-out formats that
password managers, browsers, and banking apps use to tell history tools to skip a clip:

| Format | Meaning |
|--------|---------|
| `ExcludeClipboardContentFromMonitorProcessing` | Presence alone excludes the clip |
| `Clipboard Viewer Ignore` | Presence alone excludes the clip |
| `ClipboardViewerIgnore` | Presence alone excludes the clip |
| `CanIncludeInClipboardHistory` | DWORD; `0` excludes the clip |

An excluded clip is never hashed, previewed, or written to disk — only the format name
that triggered the skip reaches the log. A marker that is present but whose value cannot
be parsed is treated as exclusion.

`CanUploadToCloudClipboard` is deliberately **not** honoured as a local opt-out: it
governs cross-device sync only, and Windows itself still keeps those clips in local
history, so treating it as a storage opt-out would silently drop clips you expect to keep.

This depends on the source application setting one of these formats. An app that copies a
password without marking it — including most web pages — cannot be detected, and that clip
is stored like any other text.

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
| `/` or Ctrl + F | Open the filter box |
| Esc | Hide popup |

Mouse click also pastes. Window deactivation hides the popup.

### Filtering

The list keeps focus when the popup opens, so every shortcut above works immediately.
Press `/` (or Ctrl + F, for layouts where `/` is not on the `OemQuestion` key) to reveal
the filter box.

| Key | Action while the filter box has focus |
|-----|----------------------------------------|
| Any character | Types into the filter |
| ↑ / ↓ | Change selection without leaving the box |
| Enter | Paste selected |
| Esc | Return focus to the list, keeping the filter applied |

Terms are separated by spaces and **all** must match (AND). Matching is case-insensitive
and runs against each clip's stored preview text, its file paths, and its kind — so
`image` or `files` narrows by type. The header shows `matched / total` while a filter is
active, and the numeric shortcuts renumber to the visible rows.

Filtering reads only the in-memory index, never payload files on disk, so it stays
instant across a full history. This does mean it matches the stored preview (the first
~240 characters of a clip), not the full body of a long text clip.

The filter resets every time the popup closes.

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
.\tools\build-release.ps1 -Version 1.0.2
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
GitHub release feed shortly after every installed app launch, then in the
background while the app remains open. A newer version is
downloaded silently, then a non-modal notification lets the user choose
**Restart & update** or **Later**. Manual checks remain available from Settings
and the tray menu.

### Publish a new version

Push a semantic-version tag to run the release workflow:

```powershell
git tag v1.0.2
git push origin v1.0.2
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

### Excluded clips
- Copy a password from KeePass / Bitwarden / 1Password
- Confirm no new row appears and no folder is written under `items\`
- Confirm `logs\clipstack.log` records only the format name, never the value
- Copy ordinary text from the same app; confirm it still captures

### Filtering
- `/` and Ctrl + F both open the filter box
- Type to narrow; confirm header shows `matched / total`
- Digits and Delete edit the query instead of pasting/deleting while the box has focus
- ↑ / ↓ and Enter still work from inside the box
- Esc returns to the list with the filter applied; `1`–`9` then act on visible rows
- Close and reopen; confirm the filter is cleared

### Capture responsiveness
- Copy a large image or a ~100 MB file
- Confirm the popup still opens instantly and the hotkey responds during capture

### Popup / hotkey / tray / updates
- Mouse, arrows, Enter, `1`–`0`, Esc, Delete
- Multi-monitor and mixed DPI
- Change shortcut; conflicting shortcut; restart
- Pause/resume; clear; startup toggle; exit; second instance
- Unpackaged updater fails safely; installed update path when feed configured

## Architecture notes

- `ClipStack.Core` — models, hashing, search predicate, atomic JSON storage (no WPF)
- `ClipStack.App` — tray, WPF popup/settings, Win32 interop, Velopack
- Hidden `HwndSource` window owns clipboard listener + hotkey
- Self-copy suppression avoids restoring history items as new duplicates

### Capture threading

Capture runs in two phases, split at `ClipboardSnapshot`:

1. **Read** (`ClipboardFormatReader.ReadSnapshotAsync`) — UI/STA thread. Checks the
   exclusion formats, then marshals clipboard data into strings, byte arrays, and a
   *frozen* `BitmapSource`. STA is required here; the work is just marshalling.
2. **Build** (`ClipboardFormatReader.BuildItemData`) plus `HistoryStore.AddOrPromote` —
   thread pool, via `Task.Run`. Reads original image files, generates thumbnails, encodes
   PNGs, hashes with SHA-256, and writes payloads to disk.

Everything crossing that boundary is immutable or frozen, which is what makes phase 2
safe off-thread. Freezing the clipboard bitmap in phase 1 is load-bearing — a bitmap that
cannot be frozen is dropped rather than passed across threads.
