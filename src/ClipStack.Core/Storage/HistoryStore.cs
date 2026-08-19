using System.Text;
using System.Text.Json;
using ClipStack.Core.Models;
using ClipStack.Core.Utilities;

namespace ClipStack.Core.Storage;

public sealed class HistoryStore
{
    /// <summary>
    /// Marks a clip rebuilt from a folder that predates the metadata sidecar. Deliberately
    /// not a valid SHA-256, so it can never collide with a real content hash — but also
    /// never match one, which is why it is now a fallback rather than the rule.
    /// </summary>
    public const string RecoveredHashPrefix = "recovered-";

    private readonly StoragePaths _paths;
    private readonly object _gate = new();
    private ClipboardIndex _index = new();

    public HistoryStore(StoragePaths paths)
    {
        _paths = paths;
    }

    public StoragePaths Paths => _paths;

    /// <summary>
    /// Whether new payloads are written encrypted. Reads always handle both, so turning
    /// this off leaves existing encrypted clips readable and vice versa.
    /// </summary>
    public bool EncryptPayloads { get; set; } = true;

    /// <summary>Raised when a payload could not be encrypted and was stored in the clear.</summary>
    public event Action<Exception>? EncryptionFailed;

    public IReadOnlyList<ClipboardItem> Items
    {
        get
        {
            lock (_gate)
                return _index.Items.ToList();
        }
    }

    public void Initialize()
    {
        _paths.EnsureCreated();
        CleanupTemporaryFolders();
        _index = LoadOrRecoverIndex();
        ValidateAndPruneMissingPayloads();
        BackfillMissingMetadata();
        SaveIndexAtomic();
    }

    /// <summary>
    /// Writes the recovery sidecar for clips captured before it existed.
    /// </summary>
    /// <remarks>
    /// Without this the fix would only protect clips captured after the upgrade, leaving
    /// an existing history one corrupt index away from the un-deduplicable state this
    /// replaced. Runs once — after the backfill every folder has a sidecar — and writes
    /// only small files.
    /// </remarks>
    private void BackfillMissingMetadata()
    {
        foreach (var item in Items)
        {
            try
            {
                var dir = _paths.GetItemDirectory(item.Id);
                if (!Directory.Exists(dir) || File.Exists(Path.Combine(dir, PayloadFileNames.Metadata)))
                    continue;

                WriteMetadata(dir, new ClipboardItemMetadata
                {
                    ContentHash = item.ContentHash,
                    DominantKind = item.DominantKind,
                    CapturedUtc = item.CapturedUtc,
                    PreviewText = item.PreviewText,
                    CharacterCount = item.CharacterCount,
                    ImageWidth = item.ImageWidth,
                    ImageHeight = item.ImageHeight,
                    FileCount = item.FileCount,
                    IsPinned = item.IsPinned,
                });
            }
            catch
            {
                // best effort; the index still has everything while it stays readable
            }
        }
    }

    public ClipboardItem? FindByHash(string contentHash)
    {
        lock (_gate)
            return _index.Items.FirstOrDefault(i => string.Equals(i.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase));
    }

    public ClipboardItem? GetById(Guid id)
    {
        lock (_gate)
            return _index.Items.FirstOrDefault(i => i.Id == id);
    }

    /// <summary>
    /// Stores a new clip, or promotes the stored clip that already holds this content.
    /// </summary>
    /// <remarks>
    /// Payload encryption and disk writes deliberately run <b>outside</b> <c>_gate</c>.
    /// DPAPI costs roughly 50 ms per megabyte, so holding the lock across a large image
    /// blocked every reader for the duration — and the popup's first act on the hotkey is
    /// to read <see cref="Items"/>, which made a 50 MB capture freeze it for seconds.
    /// Raising <see cref="EncryptionFailed"/> under the lock was worse still: the handler
    /// marshals to the UI thread, which could already be waiting on the very same lock.
    ///
    /// The lock now covers only the in-memory index mutation and its save. That leaves a
    /// window where a concurrent capture of the same content can win the race, so the
    /// duplicate check runs again inside the lock and the losing write is discarded.
    /// </remarks>
    public (ClipboardItem Item, bool WasDuplicate) AddOrPromote(NewClipboardItemData data, int historyLimit)
    {
        if (historyLimit < 1) historyLimit = 1;

        // Fast path: this content is already stored, so nothing needs writing at all.
        ClipboardItem? existing;
        List<ClipboardItem> evicted;
        lock (_gate)
            existing = Promote_NoLock(data.ContentHash, historyLimit, out evicted);

        DeleteItemFolders(evicted);
        if (existing is not null)
            return (existing, true);

        var id = Guid.NewGuid();
        var tempDir = _paths.GetTempItemDirectory(id);
        var finalDir = _paths.GetItemDirectory(id);

        var now = DateTimeOffset.UtcNow;

        // Stored beside the payloads so a rebuild from folders does not have to guess.
        var metadata = new ClipboardItemMetadata
        {
            ContentHash = data.ContentHash,
            DominantKind = data.DominantKind,
            CapturedUtc = now,
            PreviewText = data.PreviewText,
            CharacterCount = data.CharacterCount,
            ImageWidth = data.ImageWidth,
            ImageHeight = data.ImageHeight,
            FileCount = data.FilePaths.Count,
        };

        List<ClipboardPayload> payloads;
        long totalSize;
        try
        {
            (payloads, totalSize) = WritePayloads(tempDir, finalDir, data, metadata);
        }
        catch
        {
            TryDeleteDirectory(tempDir);
            TryDeleteDirectory(finalDir);
            throw;
        }

        var thumbnail = payloads.FirstOrDefault(p => p.Format == ClipboardFormatKind.ThumbnailPng);

        var item = new ClipboardItem
        {
            Id = id,
            CapturedUtc = now,
            LastUsedUtc = now,
            DominantKind = data.DominantKind,
            ContentHash = data.ContentHash,
            TotalSizeBytes = totalSize,
            PreviewText = data.PreviewText,
            CharacterCount = data.CharacterCount,
            ImageWidth = data.ImageWidth,
            ImageHeight = data.ImageHeight,
            FileCount = data.FilePaths.Count,
            Payloads = payloads,
            FilePaths = data.FilePaths.ToList(),
            ThumbnailRelativePath = thumbnail?.RelativePath,
        };

        ClipboardItem? raced;
        lock (_gate)
        {
            // Another capture may have stored the same content while this one was writing.
            raced = Promote_NoLock(data.ContentHash, historyLimit, out evicted);
            if (raced is null)
            {
                _index.Items.Insert(0, item);
                SortPinnedFirst_NoLock();
                evicted = EvictOverflow_NoLock(historyLimit);
                SaveIndexAtomic_NoLock();
            }
        }

        DeleteItemFolders(evicted);

        if (raced is not null)
        {
            // Our payloads lost the race and are referenced by nothing.
            TryDeleteDirectory(finalDir);
            return (raced, true);
        }

        return (item, false);
    }

    /// <summary>
    /// Moves the clip holding this content to the front. Returns null when it is not stored.
    /// Callers must hold <c>_gate</c> and delete <paramref name="evicted"/> after releasing it.
    /// </summary>
    private ClipboardItem? Promote_NoLock(string contentHash, int historyLimit, out List<ClipboardItem> evicted)
    {
        var existing = _index.Items.FirstOrDefault(i =>
            string.Equals(i.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            evicted = [];
            return null;
        }

        existing.LastUsedUtc = DateTimeOffset.UtcNow;
        _index.Items.Remove(existing);
        _index.Items.Insert(0, existing);
        SortPinnedFirst_NoLock();
        evicted = EvictOverflow_NoLock(historyLimit);
        SaveIndexAtomic_NoLock();
        return existing;
    }

    /// <summary>
    /// Encrypts and writes every payload into a temporary folder, then renames it into
    /// place. Runs without <c>_gate</c> held — see <see cref="AddOrPromote"/>.
    /// </summary>
    private (List<ClipboardPayload> Payloads, long TotalSize) WritePayloads(
        string tempDir,
        string finalDir,
        NewClipboardItemData data,
        ClipboardItemMetadata metadata)
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
        Directory.CreateDirectory(tempDir);

        var payloads = new List<ClipboardPayload>();
        long totalSize = 0;

        foreach (var request in data.Payloads)
        {
            var fileName = request.RelativeFileName ?? PayloadFileNames.ForFormat(request.Format);
            if (fileName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(fileName))
                throw new InvalidOperationException("Unsafe payload file name.");
            if (fileName.Equals(PayloadFileNames.Metadata, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Payload may not claim the reserved metadata file name.");

            var fullPath = Path.Combine(tempDir, fileName);

            var encrypted = false;
            var toWrite = request.Bytes;
            if (EncryptPayloads)
            {
                try
                {
                    toWrite = PayloadProtector.Protect(request.Bytes);
                    encrypted = toWrite.Length != request.Bytes.Length
                                || PayloadProtector.IsProtected(toWrite);
                }
                catch (Exception ex)
                {
                    // Storing the clip in the clear beats losing it, but the user
                    // must not be told it is encrypted when it is not.
                    EncryptionFailed?.Invoke(ex);
                    toWrite = request.Bytes;
                    encrypted = false;
                }
            }

            File.WriteAllBytes(fullPath, toWrite);

            // SizeBytes tracks what is on disk, which is what disk-usage reports.
            totalSize += toWrite.LongLength;

            payloads.Add(new ClipboardPayload
            {
                Format = request.Format,
                RelativePath = fileName,
                SizeBytes = toWrite.LongLength,
                Encrypted = encrypted,
            });
        }

        // Into the temp folder too, so the sidecar lands atomically with what it describes.
        WriteMetadata(tempDir, metadata);

        if (Directory.Exists(finalDir))
            Directory.Delete(finalDir, recursive: true);
        Directory.Move(tempDir, finalDir);

        return (payloads, totalSize);
    }

    /// <summary>
    /// Writes the recovery sidecar. Encrypted like a payload — it carries preview text.
    /// </summary>
    private void WriteMetadata(string dir, ClipboardItemMetadata metadata)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, AppIdentity.JsonOptions));

        if (EncryptPayloads)
        {
            try
            {
                bytes = PayloadProtector.Protect(bytes);
            }
            catch (Exception ex)
            {
                // Same bargain as a payload: storing it readable beats losing it.
                EncryptionFailed?.Invoke(ex);
            }
        }

        File.WriteAllBytes(Path.Combine(dir, PayloadFileNames.Metadata), bytes);
    }

    private static ClipboardItemMetadata? TryReadMetadata(string dir)
    {
        var path = Path.Combine(dir, PayloadFileNames.Metadata);
        if (!File.Exists(path))
            return null;

        try
        {
            if (TryReadPayloadFile(path) is not { Length: > 0 } bytes)
                return null;

            return JsonSerializer.Deserialize<ClipboardItemMetadata>(
                Encoding.UTF8.GetString(bytes), AppIdentity.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void DeleteItemFolders(IEnumerable<ClipboardItem> items)
    {
        foreach (var item in items)
            TryDeleteItemFolder(item.Id);
    }

    /// <summary>
    /// Evicts oldest items until count &lt;= <paramref name="historyLimit"/>.
    /// Used when the limit is lowered from Settings (not only on the next capture).
    /// </summary>
    public int TrimToLimit(int historyLimit)
    {
        if (historyLimit < 1) historyLimit = 1;

        List<ClipboardItem> evicted;
        lock (_gate)
        {
            evicted = EvictOverflow_NoLock(historyLimit);
            if (evicted.Count > 0)
                SaveIndexAtomic_NoLock();
        }

        DeleteItemFolders(evicted);
        return evicted.Count;
    }

    /// <summary>
    /// Evicts the oldest unpinned items until the unpinned count fits the limit.
    /// </summary>
    /// <remarks>
    /// Pinned items are exempt rather than counted, so the limit governs the rolling
    /// history only. Pinning every slot therefore cannot wedge capture: new clips still
    /// arrive and simply push each other out. The tradeoff is that the visible list can
    /// exceed the configured limit by the number of pinned items, which is the whole
    /// point of pinning something.
    /// </remarks>
    private List<ClipboardItem> EvictOverflow_NoLock(int historyLimit)
    {
        var evicted = new List<ClipboardItem>();

        while (_index.Items.Count(i => !i.IsPinned) > historyLimit)
        {
            var lastUnpinned = -1;
            for (var i = _index.Items.Count - 1; i >= 0; i--)
            {
                if (!_index.Items[i].IsPinned)
                {
                    lastUnpinned = i;
                    break;
                }
            }

            if (lastUnpinned < 0)
                break;

            evicted.Add(_index.Items[lastUnpinned]);
            _index.Items.RemoveAt(lastUnpinned);
        }

        return evicted;
    }

    /// <summary>
    /// Pinned clips first, then everything by recency.
    /// </summary>
    /// <remarks>
    /// Ordering by LastUsedUtc rather than just preserving position matters on unpin: a
    /// stable sort alone would strand the clip at the top of the list it was promoted to,
    /// leaving the oldest clip sitting above newer ones. Sorting on recency sends it back
    /// to where it belongs, and makes the order deterministic regardless of how the list
    /// was reached.
    /// </remarks>
    private void SortPinnedFirst_NoLock()
    {
        _index.Items = _index.Items
            .OrderByDescending(i => i.IsPinned)
            .ThenByDescending(i => i.LastUsedUtc)
            .ToList();
    }

    /// <summary>Toggles the pin flag. Returns the new state, or null when the id is unknown.</summary>
    public bool? TogglePin(Guid id)
    {
        bool pinned;
        lock (_gate)
        {
            var item = _index.Items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return null;

            item.IsPinned = !item.IsPinned;
            pinned = item.IsPinned;
            SortPinnedFirst_NoLock();
            SaveIndexAtomic_NoLock();
        }

        // Outside the lock, and best effort: the index is the authority while it is
        // readable, so a sidecar that falls behind costs nothing until recovery runs.
        TryUpdateMetadataPin(id, pinned);
        return pinned;
    }

    private void TryUpdateMetadataPin(Guid id, bool pinned)
    {
        try
        {
            var dir = _paths.GetItemDirectory(id);
            if (TryReadMetadata(dir) is not { } metadata || metadata.IsPinned == pinned)
                return;

            metadata.IsPinned = pinned;
            WriteMetadata(dir, metadata);
        }
        catch
        {
            // best effort
        }
    }

    public bool DeleteItem(Guid id)
    {
        lock (_gate)
        {
            var item = _index.Items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return false;

            _index.Items.Remove(item);
            SaveIndexAtomic_NoLock();
        }

        // Outside the lock: removing a large item folder is disk work no reader should wait on.
        TryDeleteItemFolder(id);
        return true;
    }

    /// <summary>Removes every clip. Returns how many were removed, so the caller can log it.</summary>
    /// <remarks>
    /// The count is taken under the lock rather than read from <see cref="Items"/> first,
    /// which would miss a capture landing in between and report the wrong number.
    /// </remarks>
    public int ClearAll()
    {
        List<Guid> ids;
        lock (_gate)
        {
            ids = _index.Items.Select(i => i.Id).ToList();
            _index.Items.Clear();
            SaveIndexAtomic_NoLock();
        }

        foreach (var id in ids)
            TryDeleteItemFolder(id);

        CleanupTemporaryFolders();
        return ids.Count;
    }

    public void Touch(Guid id)
    {
        lock (_gate)
        {
            var item = _index.Items.FirstOrDefault(i => i.Id == id);
            if (item is null) return;
            item.LastUsedUtc = DateTimeOffset.UtcNow;
            _index.Items.Remove(item);
            _index.Items.Insert(0, item);
            SortPinnedFirst_NoLock();
            SaveIndexAtomic_NoLock();
        }
    }

    public string ResolvePayloadPath(ClipboardItem item, ClipboardPayload payload)
    {
        var itemDir = _paths.GetItemDirectory(item.Id);
        return PathSafety.ResolveSafeRelativePath(itemDir, payload.RelativePath);
    }

    public string? ResolveThumbnailPath(ClipboardItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ThumbnailRelativePath))
            return null;
        var itemDir = _paths.GetItemDirectory(item.Id);
        return PathSafety.ResolveSafeRelativePath(itemDir, item.ThumbnailRelativePath);
    }

    public long CalculateDiskUsageBytes()
    {
        if (!Directory.Exists(_paths.Items))
            return 0;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(_paths.Items, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch { /* ignore */ }
        }

        try
        {
            if (File.Exists(_paths.IndexFile))
                total += new FileInfo(_paths.IndexFile).Length;
        }
        catch { /* ignore */ }

        return total;
    }

    public byte[]? ReadPayloadBytes(ClipboardItem item, ClipboardFormatKind format)
    {
        var payload = item.Payloads.FirstOrDefault(p => p.Format == format);
        if (payload is null)
            return null;

        var path = ResolvePayloadPath(item, payload);
        if (!File.Exists(path))
            return null;

        var stored = File.ReadAllBytes(path);

        // Dispatch on the file's own header rather than the index flag, so a payload
        // stays readable even if the index disagrees with what is on disk.
        if (!PayloadProtector.IsProtected(stored))
            return stored;

        try
        {
            return PayloadProtector.Unprotect(stored);
        }
        catch (Exception ex)
        {
            // Encrypted under a different Windows account or a reset credential: the
            // bytes are unrecoverable, so report nothing rather than garbage.
            DecryptionFailed?.Invoke(ex);
            return null;
        }
    }

    /// <summary>Raised when a stored payload could not be decrypted for this user.</summary>
    public event Action<Exception>? DecryptionFailed;

    public string? ReadPayloadText(ClipboardItem item, ClipboardFormatKind format)
    {
        var bytes = ReadPayloadBytes(item, format);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public void CleanupTemporaryFolders()
    {
        if (!Directory.Exists(_paths.Items))
            return;

        foreach (var dir in Directory.EnumerateDirectories(_paths.Items, ".tmp-*"))
            TryDeleteDirectory(dir);
    }

    /// <summary>
    /// Loads the index, falling back to rebuilding from item folders.
    /// </summary>
    /// <remarks>
    /// Only a file that cannot be read or parsed triggers the rebuild. A single unsafe
    /// entry inside an otherwise valid index is that entry's problem and is dropped by
    /// <see cref="ValidateAndPruneMissingPayloads"/>, which validates every item
    /// individually on the very next line of <see cref="Initialize"/>. Treating one bad
    /// path as whole-file corruption discarded every pin, hash and timestamp in the file
    /// to remove a single row.
    /// </remarks>
    private ClipboardIndex LoadOrRecoverIndex()
    {
        if (!File.Exists(_paths.IndexFile))
            return ReconstructFromFolders();

        try
        {
            var json = File.ReadAllText(_paths.IndexFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<ClipboardIndex>(json, AppIdentity.JsonOptions)
                   ?? throw new InvalidDataException("Index deserialized to null.");
        }
        catch
        {
            BackupCorruptIndex();
            return ReconstructFromFolders();
        }
    }

    private void ValidateItemPaths(ClipboardItem item)
    {
        foreach (var payload in item.Payloads)
        {
            if (string.IsNullOrWhiteSpace(payload.RelativePath)
                || payload.RelativePath.Contains("..", StringComparison.Ordinal)
                || Path.IsPathRooted(payload.RelativePath))
            {
                throw new InvalidDataException("Unsafe payload path in index.");
            }

            var itemRoot = _paths.GetItemDirectory(item.Id);
            var full = Path.GetFullPath(Path.Combine(itemRoot, payload.RelativePath));
            if (!PathSafety.IsPathInsideRoot(itemRoot, full))
                throw new InvalidDataException("Path traversal in index.");
        }

        if (!string.IsNullOrWhiteSpace(item.ThumbnailRelativePath))
        {
            if (item.ThumbnailRelativePath.Contains("..", StringComparison.Ordinal)
                || Path.IsPathRooted(item.ThumbnailRelativePath))
            {
                throw new InvalidDataException("Unsafe thumbnail path in index.");
            }
        }
    }

    private void BackupCorruptIndex()
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var backup = Path.Combine(_paths.Root, $"index.corrupt.{stamp}.json");
            File.Move(_paths.IndexFile, backup, overwrite: true);
        }
        catch
        {
            try { File.Delete(_paths.IndexFile); } catch { /* best effort */ }
        }
    }

    private ClipboardIndex ReconstructFromFolders()
    {
        var index = new ClipboardIndex();
        if (!Directory.Exists(_paths.Items))
            return index;

        foreach (var dir in Directory.EnumerateDirectories(_paths.Items))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Guid.TryParse(name, out var id))
                continue;

            try
            {
                var item = ReconstructItem(id, dir);
                if (item is not null)
                    index.Items.Add(item);
            }
            catch
            {
                // skip unreadable folders
            }
        }

        index.Items = index.Items
            .OrderByDescending(i => i.LastUsedUtc)
            .ToList();

        return index;
    }

    /// <summary>
    /// Reads a payload file that may or may not be encrypted. Returns null when it is
    /// protected under credentials this user no longer has.
    /// </summary>
    private static byte[]? TryReadPayloadFile(string path)
    {
        try
        {
            var stored = File.ReadAllBytes(path);
            return PayloadProtector.IsProtected(stored)
                ? PayloadProtector.Unprotect(stored)
                : stored;
        }
        catch
        {
            return null;
        }
    }

    private static ClipboardItem? ReconstructItem(Guid id, string dir)
    {
        var payloads = new List<ClipboardPayload>();
        long total = 0;

        void AddIfExists(ClipboardFormatKind format, string fileName)
        {
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path)) return;
            var len = new FileInfo(path).Length;
            total += len;
            payloads.Add(new ClipboardPayload
            {
                Format = format,
                RelativePath = fileName,
                SizeBytes = len,
            });
        }

        AddIfExists(ClipboardFormatKind.UnicodeText, PayloadFileNames.Text);
        AddIfExists(ClipboardFormatKind.Html, PayloadFileNames.Html);
        AddIfExists(ClipboardFormatKind.Rtf, PayloadFileNames.Rtf);
        AddIfExists(ClipboardFormatKind.ImagePng, PayloadFileNames.Image);
        AddIfExists(ClipboardFormatKind.ThumbnailPng, PayloadFileNames.Thumbnail);
        AddIfExists(ClipboardFormatKind.FileDropList, PayloadFileNames.Files);

        // Original HQ image files: original.jpg, original.webp, etc.
        foreach (var file in Directory.EnumerateFiles(dir, "original.*"))
        {
            var name = Path.GetFileName(file);
            if (!PayloadFileNames.IsOriginalImageFileName(name))
                continue;
            if (payloads.Any(p => p.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            AddIfExists(ClipboardFormatKind.ImageOriginal, name);
        }

        if (payloads.Count == 0)
            return null;

        var kind = ClipboardItemKind.Unknown;
        var preview = string.Empty;
        var charCount = 0;
        int? w = null, h = null;
        var files = new List<string>();

        var textPath = Path.Combine(dir, PayloadFileNames.Text);
        if (File.Exists(textPath))
        {
            if (TryReadPayloadFile(textPath) is { } textBytes)
            {
                var text = Encoding.UTF8.GetString(textBytes);
                preview = TextPreview.Create(text);
                charCount = text.Length;
            }

            kind = File.Exists(Path.Combine(dir, PayloadFileNames.Html))
                || File.Exists(Path.Combine(dir, PayloadFileNames.Rtf))
                ? ClipboardItemKind.RichText
                : ClipboardItemKind.Text;
        }

        if (File.Exists(Path.Combine(dir, PayloadFileNames.Image))
            || payloads.Any(p => p.Format == ClipboardFormatKind.ImageOriginal))
        {
            kind = ClipboardItemKind.Image;
            if (string.IsNullOrEmpty(preview))
                preview = "Image";
        }

        var filesPath = Path.Combine(dir, PayloadFileNames.Files);
        if (File.Exists(filesPath))
        {
            try
            {
                if (TryReadPayloadFile(filesPath) is { } fileBytes)
                {
                    files = JsonSerializer.Deserialize<List<string>>(
                        Encoding.UTF8.GetString(fileBytes), AppIdentity.JsonOptions) ?? [];
                }
            }
            catch { /* ignore */ }

            // Path-only file drops stay Files; HQ image captures keep Image even with FileDropList.
            if (kind != ClipboardItemKind.Image)
            {
                kind = ClipboardItemKind.Files;
                preview = string.Join(", ", files.Take(2).Select(Path.GetFileName));
            }
            else if (string.IsNullOrEmpty(preview) || preview == "Image")
            {
                preview = string.Join(", ", files.Take(2).Select(Path.GetFileName));
                if (string.IsNullOrEmpty(preview))
                    preview = "Image";
            }
        }

        var thumb = payloads.FirstOrDefault(p => p.Format == ClipboardFormatKind.ThumbnailPng);
        var utc = Directory.GetCreationTimeUtc(dir);
        var capturedUtc = new DateTimeOffset(utc, TimeSpan.Zero);

        // Only reachable for folders written before the sidecar existed. A hash no capture
        // can ever match means the clip never deduplicates again, so it is a last resort
        // rather than the normal outcome.
        var contentHash = RecoveredHashPrefix + id.ToString("N");
        var pinned = false;

        if (TryReadMetadata(dir) is { } metadata)
        {
            if (!string.IsNullOrWhiteSpace(metadata.ContentHash))
                contentHash = metadata.ContentHash;
            if (metadata.DominantKind != ClipboardItemKind.Unknown)
                kind = metadata.DominantKind;
            if (metadata.CapturedUtc > DateTimeOffset.MinValue)
                capturedUtc = metadata.CapturedUtc;
            if (!string.IsNullOrEmpty(metadata.PreviewText))
                preview = metadata.PreviewText;
            if (metadata.CharacterCount > 0)
                charCount = metadata.CharacterCount;

            // Dimensions have no folder-level fallback at all; without the sidecar a
            // recovered image row shows "×" where its size should be.
            w = metadata.ImageWidth ?? w;
            h = metadata.ImageHeight ?? h;
            pinned = metadata.IsPinned;
        }

        return new ClipboardItem
        {
            Id = id,
            CapturedUtc = capturedUtc,
            LastUsedUtc = capturedUtc,
            DominantKind = kind,
            ContentHash = contentHash,
            TotalSizeBytes = total,
            PreviewText = preview,
            CharacterCount = charCount,
            ImageWidth = w,
            ImageHeight = h,
            FileCount = files.Count,
            Payloads = payloads,
            FilePaths = files,
            ThumbnailRelativePath = thumb?.RelativePath,
            IsPinned = pinned,
        };
    }

    private void ValidateAndPruneMissingPayloads()
    {
        var kept = new List<ClipboardItem>();
        foreach (var item in _index.Items)
        {
            try
            {
                ValidateItemPaths(item);
            }
            catch
            {
                continue;
            }

            var itemDir = _paths.GetItemDirectory(item.Id);
            if (!Directory.Exists(itemDir))
                continue;

            var requiredOk = true;
            var validPayloads = new List<ClipboardPayload>();
            foreach (var payload in item.Payloads)
            {
                try
                {
                    var path = PathSafety.ResolveSafeRelativePath(itemDir, payload.RelativePath);
                    if (File.Exists(path))
                        validPayloads.Add(payload);
                    else if (payload.Format is ClipboardFormatKind.UnicodeText
                             or ClipboardFormatKind.ImagePng
                             or ClipboardFormatKind.ImageOriginal
                             or ClipboardFormatKind.FileDropList)
                    {
                        requiredOk = false;
                        break;
                    }
                }
                catch
                {
                    requiredOk = false;
                    break;
                }
            }

            if (!requiredOk || validPayloads.Count == 0)
                continue;

            item.Payloads = validPayloads;
            kept.Add(item);
        }

        _index.Items = kept;

        // Remove orphan folders not referenced by index
        if (Directory.Exists(_paths.Items))
        {
            var known = kept.Select(i => i.Id).ToHashSet();
            foreach (var dir in Directory.EnumerateDirectories(_paths.Items))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Guid.TryParse(name, out var id) && !known.Contains(id))
                    TryDeleteDirectory(dir);
            }
        }
    }

    private void SaveIndexAtomic()
    {
        lock (_gate)
            SaveIndexAtomic_NoLock();
    }

    private void SaveIndexAtomic_NoLock()
    {
        _paths.EnsureCreated();
        var tmp = _paths.IndexFile + ".tmp";
        var json = JsonSerializer.Serialize(_index, AppIdentity.JsonOptions);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(json);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        PathSafety.AtomicReplaceFile(_paths.IndexFile, tmp);
    }

    private void TryDeleteItemFolder(Guid id)
    {
        TryDeleteDirectory(_paths.GetItemDirectory(id));
        TryDeleteDirectory(_paths.GetTempItemDirectory(id));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
