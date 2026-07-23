using System.Collections.Specialized;
using System.IO;
using System.Text;
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

    public async Task<RestoreResult> RestoreAsync(ClipboardItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = BuildDataObject(item, out var missingAllFiles);
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

    private DataObject? BuildDataObject(ClipboardItem item, out bool missingAllFiles)
    {
        missingAllFiles = false;
        var data = new DataObject();
        var hasAny = false;

        if (item.DominantKind == ClipboardItemKind.Files || item.FilePaths.Count > 0)
        {
            var existing = item.FilePaths.Where(File.Exists).ToArray();
            if (existing.Length == 0 && item.FilePaths.Count > 0)
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

        var pngBytes = _history.ReadPayloadBytes(item, ClipboardFormatKind.ImagePng);
        if (pngBytes is { Length: > 0 })
        {
            using var ms = new MemoryStream(pngBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            data.SetImage(bitmap);
            hasAny = true;
        }

        return hasAny ? data : null;
    }
}

internal readonly record struct RestoreResult(bool Success, string? Message)
{
    public static RestoreResult Ok() => new(true, null);
    public static RestoreResult Fail(string message) => new(false, message);
}
