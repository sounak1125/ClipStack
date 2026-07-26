using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipStack.Core.Models;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;

namespace ClipStack.Services;

internal sealed class ClipboardRestoreService
{
    private static readonly int[] RetryDelaysMs = [20, 40, 80, 120, 180, 250];

    private readonly HistoryStore _history;
    private readonly SelfCopySuppression _suppression;
    private readonly FileLogger _logger;

    public ClipboardRestoreService(HistoryStore history, SelfCopySuppression suppression, FileLogger logger)
    {
        _history = history;
        _suppression = suppression;
        _logger = logger;
    }

    public async Task<RestoreResult> RestoreAsync(
        ClipboardItem item,
        bool plainTextOnly = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = BuildDataObject(item, plainTextOnly, out var missingAllFiles);
            if (missingAllFiles)
                return RestoreResult.Fail("Files are no longer available.");

            if (data is null)
                return RestoreResult.Fail("Could not restore this item.");

            Exception? last = null;
            for (var i = 0; i < RetryDelaysMs.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    _suppression.Arm(item.ContentHash, TimeSpan.FromSeconds(2));
                    _history.Touch(item.Id);
                    return RestoreResult.Ok();
                }
                catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(RetryDelaysMs[i], cancellationToken).ConfigureAwait(true);
                }
            }

            if (last is not null)
                _logger.Error("ClipboardSetDataObject", last);

            return RestoreResult.Fail("Clipboard is busy. Try again.");
        }
        catch (Exception ex)
        {
            _logger.Error("RestoreItem", ex);
            return RestoreResult.Fail("Restore failed.");
        }
    }

    private DataObject? BuildDataObject(ClipboardItem item, bool plainTextOnly, out bool missingAllFiles)
    {
        missingAllFiles = false;
        var data = new DataObject();
        var hasAny = false;

        // Plain-text paste: for a clip that carries text, offer only the text formats so
        // the target application cannot pick up the source's HTML or RTF styling. Clips
        // with no text at all (images, file drops) still restore normally — there is no
        // useful "plain" form of those, and silently pasting nothing would be worse.
        if (plainTextOnly)
        {
            var plain = _history.ReadPayloadText(item, ClipboardFormatKind.UnicodeText)
                        ?? _history.ReadPayloadText(item, ClipboardFormatKind.Text);

            if (!string.IsNullOrEmpty(plain))
            {
                data.SetText(plain, TextDataFormat.UnicodeText);
                try { data.SetText(plain, TextDataFormat.Text); } catch { /* ignore */ }
                return data;
            }
        }

        var hasImagePayload = item.Payloads.Any(p =>
            p.Format is ClipboardFormatKind.ImagePng or ClipboardFormatKind.ImageOriginal);

        // File-only items require at least one existing path.
        // Image items with stored pixels can still paste when source files are gone.
        if (item.DominantKind == ClipboardItemKind.Files || item.FilePaths.Count > 0)
        {
            var existing = item.FilePaths.Where(File.Exists).ToArray();
            if (existing.Length == 0 && item.FilePaths.Count > 0 && !hasImagePayload)
            {
                missingAllFiles = true;
                return null;
            }

            if (existing.Length > 0)
            {
                var collection = new StringCollection();
                collection.AddRange(existing);
                data.SetFileDropList(collection);
                hasAny = true;
            }
        }

        var text = _history.ReadPayloadText(item, ClipboardFormatKind.UnicodeText)
                   ?? _history.ReadPayloadText(item, ClipboardFormatKind.Text);
        if (!string.IsNullOrEmpty(text))
        {
            data.SetText(text, TextDataFormat.UnicodeText);
            try { data.SetText(text, TextDataFormat.Text); } catch { /* ignore */ }
            hasAny = true;
        }

        var html = _history.ReadPayloadText(item, ClipboardFormatKind.Html);
        if (!string.IsNullOrEmpty(html))
        {
            data.SetData(DataFormats.Html, html);
            hasAny = true;
        }

        var rtf = _history.ReadPayloadText(item, ClipboardFormatKind.Rtf);
        if (!string.IsNullOrEmpty(rtf))
        {
            data.SetData(DataFormats.Rtf, rtf);
            hasAny = true;
        }

        if (TryAttachImage(data, item))
            hasAny = true;

        return hasAny ? data : null;
    }

    private bool TryAttachImage(DataObject data, ClipboardItem item)
    {
        try
        {
            var original = _history.ReadPayloadBytes(item, ClipboardFormatKind.ImageOriginal);
            if (original is { Length: > 0 })
            {
                var bitmap = ThumbnailService.LoadFrozenFromBytes(original);
                if (bitmap is not null)
                {
                    data.SetImage(bitmap);
                    // Also expose raw PNG when the original is already PNG for apps that prefer it.
                    var originalPayload = item.Payloads.FirstOrDefault(p => p.Format == ClipboardFormatKind.ImageOriginal);
                    if (originalPayload is not null
                        && originalPayload.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            data.SetData("PNG", new MemoryStream(original));
                        }
                        catch { /* optional */ }
                    }

                    return true;
                }
            }

            var pngBytes = _history.ReadPayloadBytes(item, ClipboardFormatKind.ImagePng);
            if (pngBytes is { Length: > 0 })
            {
                var bitmap = ThumbnailService.LoadFrozenFromBytes(pngBytes);
                if (bitmap is not null)
                {
                    data.SetImage(bitmap);
                    try
                    {
                        data.SetData("PNG", new MemoryStream(pngBytes));
                    }
                    catch { /* optional */ }
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("RestoreImage", ex);
        }

        return false;
    }
}

internal readonly record struct RestoreResult(bool Success, string? Message)
{
    public static RestoreResult Ok() => new(true, null);
    public static RestoreResult Fail(string message) => new(false, message);
}
