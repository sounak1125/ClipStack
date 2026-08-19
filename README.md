# ClipStack

ClipStack is a lightweight Windows clipboard-history utility. It remembers the latest copied items and lets you paste any of them with a global shortcut.

It is a native **C# / WPF / .NET 10** desktop app (not Electron, not Chromium, not a web UI). The goal is low idle CPU, low memory, and fast popup opening.

## Features

- Event-driven clipboard capture (`AddClipboardFormatListener`) — no polling
- Capture work runs off the UI thread, so large images and files never freeze the popup
- Honours password-manager clipboard opt-out formats
- History encrypted at rest with DPAPI under your Windows account
- Pin clips so they are never evicted
- Paste as plain text, stripping HTML/RTF
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
password without marking it — including most web pages — cannot be detected. Encryption
at rest is what covers those clips.

### Encryption at rest

Payload files are encrypted with **DPAPI** under your Windows account (**Encrypt history
on disk**, on by default). The key derives from your Windows credentials and is never
stored by ClipStack, so another account on the same machine cannot read the history even
with full file access.

What this does **not** protect against: code already running as you, which can decrypt
exactly as ClipStack does. It raises the cost of offline disk access, not of local malware.

Each encrypted file carries a short header and reads dispatch on that header, not on the
index. So:

- History written before this version stays readable and is never rewritten.
- Encryption applies from the next capture onward — upgrading is a no-op.
- Turning the setting off later leaves earlier encrypted clips readable, and vice versa.
- A clip that cannot be decrypted (encrypted under a different account, or after a
  credential reset) is reported rather than returned as garbage.

If a clip cannot be encrypted it is stored in the clear rather than lost, and you are
notified — a clip silently stored unencrypted while the setting claims otherwise would be
the worst outcome.

DPAPI costs roughly 50 ms per megabyte. Both encryption and decryption run on background
threads, so even a 50 MB image (~2.5 s to encrypt, ~3.4 s to decrypt) never blocks the
popup or the hotkey.

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
| Shift + Enter | Paste selected as plain text |
| Shift + `1`-`9` / `0` | Paste that item as plain text |
| Ctrl + P | Pin / unpin selected item |
| `/` or Ctrl + F | Open the filter box |
| Esc | Hide popup |

Mouse click also pastes; Shift+click pastes as plain text. Window deactivation
hides the popup.

### Plain-text paste

Pasting normally offers whatever the clip carries, so text copied from a browser or
Word brings its HTML and RTF styling with it. **Paste as plain text** in Settings makes
text-only the default, and **Shift inverts whichever way that setting is set** — so the
other behaviour is always one modifier away.

Clips with no text at all — images and file drops — restore normally even when plain
text is requested. There is no useful "plain" form of those, and pasting nothing would
be worse.

### Pinned clips

`Ctrl + P` pins the selected clip. Pinned clips sort to the top, show a marker, and are
never evicted: the history limit counts unpinned clips only, so the visible list can
exceed it by however many clips you have pinned. Pinning every slot cannot stop capture
— new clips still arrive and push each other out. Unpinning returns a clip to its normal
position by recency.

Pin state lives in `index.json` and is mirrored into each clip's `meta.json`, so it
survives a history rebuilt from folders. See [Recovery](#recovery).

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
  items\<guid>\
    meta.json        (recovery sidecar; encrypted with the payloads)
    ...              (payload files)
```

### Recovery

`index.json` is the authority while it is readable. Two things can go wrong with it, and
they are handled differently:

- **One bad entry.** An entry with an unsafe payload path is dropped on load and
  everything else is kept. Treating a single bad row as whole-file corruption used to
  discard every pin, hash and timestamp in the file to remove one clip.
- **An unreadable file.** Only a file that cannot be parsed is backed up as
  `index.corrupt.<timestamp>.json` and the history rebuilt from the item folders.

Each item folder carries a `meta.json` holding what its payload files cannot express:
the content hash, capture time, kind, preview, image dimensions and pin state. A rebuild
reads it, so recovered clips keep their pins and still **deduplicate** — without it a
recovered clip got a placeholder hash that no future capture could match, and re-copying
the same text added a new row every time.

`meta.json` carries preview text, so it is encrypted exactly like a payload. Clips
captured before this existed get one written on the next launch. A folder that still has
none recovers from its payload files alone, with a placeholder hash.

`LastUsedUtc` is deliberately not stored there — it changes on every paste, and recovery
ordering by capture time is worth more than a write per paste. A rebuilt history is
therefore ordered by when clips were captured, not when they were last used.

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
.\tools\build-release.ps1 -Version 1.0.3
```

This will:

1. Restore tools and packages
2. Run tests
3. Publish self-contained `win-x64` (not single-file)
4. Package with `vpk pack`
5. Write installer/update files under `artifacts\releases`

Output:

| File | Purpose |
|------|---------|
| `ClipStack.Desktop-win-Setup.exe` | The installer new users run |
| `ClipStack.Desktop-win-Portable.zip` | Unpacked build, no installer, no updates |
| `ClipStack.Desktop-<v>-full.nupkg` | Whole app; used for a first update or when no delta applies |
| `ClipStack.Desktop-<v>-delta.nupkg` | Changed files only; what existing installs normally download |
| `RELEASES`, `releases.win.json` | The update feed |

### Installer branding

Deliberately minimal. No `--splashImage`, banner, or logo is passed, so setup shows the
app icon and a progress bar and nothing else.

`--shortcuts StartMenuRoot` overrides Velopack's `Desktop,StartMenuRoot` default:
ClipStack is a tray app that enables **Start with Windows** on install, so a desktop icon
is clutter nobody asked for. The Start Menu entry stays — without it there is no way to
launch ClipStack again after exiting from the tray.

Setup is **not signed** unless you supply `--signParams` or `--signTemplate`. Unsigned
installers trigger SmartScreen's "Windows protected your PC" on first run. See
[Code signing](#code-signing).

### Automatic updates

Public releases use the GitHub repository configured in `release-config.json`:

`https://github.com/sounak1125/ClipStack`

When **Download updates automatically** is enabled, ClipStack checks the public
GitHub release feed shortly after every installed app launch, then in the
background while the app remains open. A newer version is
downloaded silently, then a non-modal notification lets the user choose
**Restart & update** or **Later**. Manual checks remain available from Settings
and the tray menu.

Updating does **not** re-run the installer. Velopack downloads the delta package —
a few hundred KB against a ~75 MB full package — and applies it in place, then
restarts. Setup.exe is only for a first install.

### Publish a new version

Push a semantic-version tag to run the release workflow:

```powershell
git tag v1.0.3
git push origin v1.0.3
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
- Paste that large image back; confirm the popup does not freeze while it decrypts

### Plain-text paste
- Copy styled text from a browser; paste with Enter into WordPad (styling kept)
- Paste with Shift+Enter (styling stripped)
- Enable **Paste as plain text**; confirm Enter and Shift+Enter swap behaviour
- Shift+click and Shift+`1` behave the same as Shift+Enter
- Confirm an image clip still pastes when plain text is requested

### Pinning
- `Ctrl + P` pins the selected clip; it moves to the top with a marker
- Copy past the history limit; confirm the pinned clip survives
- Unpin; confirm it returns to its position by recency
- Restart; confirm pins persist
- Pin a clip past the history limit; confirm the newest unpinned clip is still listed

### Recovery
- Pin a clip, then replace `index.json` with garbage and restart
- Confirm the history comes back with pins, previews and capture times intact
- Re-copy one of the recovered clips; confirm it promotes instead of adding a second row
- Confirm `index.corrupt.<timestamp>.json` was written
- Hand-edit one entry's payload path in a valid `index.json` to `..\evil.txt` and restart;
  confirm only that clip disappears and no `index.corrupt.*` file is written

### Encryption
- With **Encrypt history on disk** on, copy text, then open `items\<guid>\text.txt`
- Confirm the file is not readable and the clip still pastes correctly
- Turn the setting off, copy again, and confirm both old and new clips still paste

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

Restore is split the same way at `RestorePayloads`: read and decrypt on the thread pool,
then assemble and publish the `DataObject` on the STA thread. Without that split, DPAPI
decryption of a large image would stall the dispatcher for seconds during paste.
