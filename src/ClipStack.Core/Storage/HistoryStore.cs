using System.Text;
using System.Text.Json;
using ClipStack.Core.Models;
using ClipStack.Core.Utilities;

namespace ClipStack.Core.Storage;

public sealed class HistoryStore
{
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
        SaveIndexAtomic();
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

    public (ClipboardItem Item, bool WasDuplicate) AddOrPromote(NewClipboardItemData data, int historyLimit)
    {
        if (historyLimit < 1) historyLimit = 1;

        lock (_gate)
        {
            var existing = _index.Items.FirstOrDefault(i =>
                string.Equals(i.ContentHash, data.ContentHash, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.LastUsedUtc = DateTimeOffset.UtcNow;
                _index.Items.Remove(existing);
                _index.Items.Insert(0, existing);
                SortPinnedFirst_NoLock();
                var promotedEvicted = EvictOverflow_NoLock(historyLimit);
                SaveIndexAtomic_NoLock();
                foreach (var e in promotedEvicted)
                    TryDeleteItemFolder(e.Id);
                return (existing, true);
            }

            var id = Guid.NewGuid();
            var tempDir = _paths.GetTempItemDirectory(id);
            var finalDir = _paths.GetItemDirectory(id);

            try
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

                    var fullPath = Path.Combine(tempDir, fileName);

                    // Encrypt at rest. This runs on the capture background thread, so the
                    // DPAPI cost on a large image cannot reach the UI thread.
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

                if (Directory.Exists(finalDir))
                    Directory.Delete(finalDir, recursive: true);
                Directory.Move(tempDir, finalDir);

                var thumbnail = payloads.FirstOrDefault(p => p.Format == ClipboardFormatKind.ThumbnailPng);

                var item = new ClipboardItem
                {
                    Id = id,
                    CapturedUtc = DateTimeOffset.UtcNow,
                    LastUsedUtc = DateTimeOffset.UtcNow,
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

                _index.Items.Insert(0, item);
                SortPinnedFirst_NoLock();

                var evicted = EvictOverflow_NoLock(historyLimit);
                SaveIndexAtomic_NoLock();

                foreach (var e in evicted)
                    TryDeleteItemFolder(e.Id);

                return (item, false);
            }
            catch
            {
                TryDeleteDirectory(tempDir);
                throw;
            }
        }
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

        foreach (var e in evicted)
            TryDeleteItemFolder(e.Id);

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

    /// <summary>Keeps pinned items above unpinned ones without disturbing relative order.</summary>
    private void SortPinnedFirst_NoLock()
    {
        _index.Items = _index.Items
            .OrderByDescending(i => i.IsPinned)
            .ToList();
    }

    /// <summary>Toggles the pin flag. Returns the new state, or null when the id is unknown.</summary>
    public bool? TogglePin(Guid id)
    {
        lock (_gate)
        {
            var item = _index.Items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return null;

            item.IsPinned = !item.IsPinned;
            SortPinnedFirst_NoLock();
            SaveIndexAtomic_NoLock();
            return item.IsPinned;
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
            TryDeleteItemFolder(id);
            return true;
        }
    }

    public void ClearAll()
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

    private ClipboardIndex LoadOrRecoverIndex()
    {
        if (!File.Exists(_paths.IndexFile))
            return ReconstructFromFolders();

        try
        {
            var json = File.ReadAllText(_paths.IndexFile, Encoding.UTF8);
            var index = JsonSerializer.Deserialize<ClipboardIndex>(json, AppIdentity.JsonOptions);
            if (index is null)
                throw new InvalidDataException("Index deserialized to null.");

            foreach (var item in index.Items)
                ValidateItemPaths(item);

            return index;
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

        return new ClipboardItem
        {
            Id = id,
            CapturedUtc = new DateTimeOffset(utc, TimeSpan.Zero),
            LastUsedUtc = new DateTimeOffset(utc, TimeSpan.Zero),
            DominantKind = kind,
            ContentHash = "recovered-" + id.ToString("N"),
            TotalSizeBytes = total,
            PreviewText = preview,
            CharacterCount = charCount,
            ImageWidth = w,
            ImageHeight = h,
            FileCount = files.Count,
            Payloads = payloads,
            FilePaths = files,
            ThumbnailRelativePath = thumb?.RelativePath,
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
