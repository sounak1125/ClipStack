using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipStack.Core.Models;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;
using ClipStack.Interop;

namespace ClipStack.Services;

/// <summary>
/// Decrypted payload content for one clip, read off the UI thread.
/// </summary>
/// <remarks>
/// This mirrors <see cref="ClipboardSnapshot"/> on the capture side. DPAPI costs roughly
/// 50 ms per megabyte, so decrypting a large image inline would stall the dispatcher for
/// seconds — the exact freeze the off-thread capture work removed. Bitmaps are frozen
/// here so they can cross back to the STA thread.
/// </remarks>
internal sealed class RestorePayloads
{
    public string? Text { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    public BitmapSource? Image { get; init; }

    /// <summary>Raw PNG bytes, offered alongside the bitmap for apps that prefer them.</summary>
    public byte[]? Png { get; init; }

    public string[] ExistingFiles { get; init; } = [];
    public bool MissingAllFiles { get; init; }

    public bool HasAny =>
        !string.IsNullOrEmpty(Text)
        || !string.IsNullOrEmpty(Html)
        || !string.IsNullOrEmpty(Rtf)
        || Image is not null
        || ExistingFiles.Length > 0;
}

internal sealed class ClipboardRestoreService
{
    internal const string RestoredFormat = "ClipStack.RestoredClip";
    private static readonly int[] RetryDelaysMs = [20, 40, 80, 120, 180, 250];
    // Encrypted payloads are slightly larger on disk because of DPAPI framing.
    private const long MaxStoredImageBytes = ImageSizeGuard.MaxEncodedBytes + 1024 * 1024;

    private readonly HistoryStore _history;
    private readonly FileLogger _logger;

    public ClipboardRestoreService(HistoryStore history, FileLogger logger)
    {
        _history = history;
        _logger = logger;
    }

    public async Task<RestoreResult> RestoreAsync(
        ClipboardItem item,
        bool plainTextOnly = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Phase 1: read and decrypt off the UI thread.
            var payloads = await Task
                .Run(() => LoadPayloads(item, plainTextOnly), cancellationToken)
                .ConfigureAwait(true);

            if (payloads.MissingAllFiles)
                return RestoreResult.Fail("Files are no longer available.");

            // Phase 2: assemble and publish on the STA thread. Only cheap work here.
            var data = BuildDataObject(payloads);
            if (data is null)
                return RestoreResult.Fail("Could not restore this item.");

            Exception? last = null;
            for (var attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    return RestoreResult.Ok(NativeMethods.GetClipboardSequenceNumber());
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                if (attempt < RetryDelaysMs.Length)
                    await Task.Delay(RetryDelaysMs[attempt], cancellationToken).ConfigureAwait(true);
            }

            if (last is not null)
                _logger.Error("ClipboardSetDataObject", last);

            return RestoreResult.Fail("Clipboard is busy. Try again.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("RestoreItem", ex);
            return RestoreResult.Fail("Restore failed.");
        }
    }

    private RestorePayloads LoadPayloads(ClipboardItem item, bool plainTextOnly)
    {
        var text = _history.ReadPayloadText(item, ClipboardFormatKind.UnicodeText)
                   ?? _history.ReadPayloadText(item, ClipboardFormatKind.Text);

        // Plain-text paste: for a clip that carries text, offer only the text formats so
        // the target application cannot pick up the source's HTML or RTF styling. Clips
        // with no text at all (images, file drops) still restore normally — there is no
        // useful "plain" form of those, and silently pasting nothing would be worse.
        if (plainTextOnly && !string.IsNullOrEmpty(text))
            return new RestorePayloads { Text = text };

        var hasImagePayload = item.Payloads.Any(p =>
            p.Format is ClipboardFormatKind.ImagePng or ClipboardFormatKind.ImageOriginal);

        var existingFiles = Array.Empty<string>();
        var missingAllFiles = false;

        // File-only items require at least one existing path.
        // Image items with stored pixels can still paste when source files are gone.
        if (item.DominantKind == ClipboardItemKind.Files || item.FilePaths.Count > 0)
        {
            existingFiles = item.FilePaths.Where(File.Exists).ToArray();
            if (existingFiles.Length == 0 && item.FilePaths.Count > 0 && !hasImagePayload)
                return new RestorePayloads { MissingAllFiles = true };
        }

        var (image, png) = LoadImage(item, ClipboardFormatKind.ImageOriginal);
        if (image is null)
            (image, png) = LoadImage(item, ClipboardFormatKind.ImagePng);

        return new RestorePayloads
        {
            Text = text,
            Html = _history.ReadPayloadText(item, ClipboardFormatKind.Html),
            Rtf = _history.ReadPayloadText(item, ClipboardFormatKind.Rtf),
            Image = image,
            Png = png,
            ExistingFiles = existingFiles,
            MissingAllFiles = missingAllFiles,
        };
    }

    private (BitmapSource? Image, byte[]? Png) LoadImage(ClipboardItem item, ClipboardFormatKind format)
    {
        try
        {
            var bytes = _history.ReadPayloadBytes(item, format, maximumStoredBytes: MaxStoredImageBytes);
            if (bytes is not { Length: > 0 })
                return (null, null);

            var image = ThumbnailService.LoadFrozenFromBytes(bytes, pngOnly: format == ClipboardFormatKind.ImagePng);
            return (image, image is not null && ThumbnailService.HasPngSignature(bytes) ? bytes : null);
        }
        catch (Exception ex)
        {
            _logger.Error("RestoreImage", ex);
            return (null, null);
        }
    }

    private static DataObject? BuildDataObject(RestorePayloads payloads)
    {
        if (!payloads.HasAny)
            return null;

        var data = new DataObject();

        if (payloads.ExistingFiles.Length > 0)
        {
            var collection = new StringCollection();
            collection.AddRange(payloads.ExistingFiles);
            data.SetFileDropList(collection);
        }

        if (!string.IsNullOrEmpty(payloads.Text))
        {
            data.SetText(payloads.Text, TextDataFormat.UnicodeText);
            try { data.SetText(payloads.Text, TextDataFormat.Text); } catch { /* ignore */ }
        }

        if (!string.IsNullOrEmpty(payloads.Html))
            data.SetData(DataFormats.Html, payloads.Html);

        if (!string.IsNullOrEmpty(payloads.Rtf))
            data.SetData(DataFormats.Rtf, payloads.Rtf);

        if (payloads.Image is not null)
        {
            data.SetImage(payloads.Image);
            if (payloads.Png is { Length: > 0 })
            {
                try { data.SetData("PNG", new MemoryStream(payloads.Png)); }
                catch { /* optional */ }
            }
        }

        // Let our listener skip this write before it reads any image bytes. The private
        // format identifies the write even if a restored clip has fewer formats now.
        data.SetData(RestoredFormat, new MemoryStream([1], writable: false), autoConvert: false);
        return data;
    }
}

internal readonly record struct RestoreResult(bool Success, string? Message, uint ClipboardSequence)
{
    public static RestoreResult Ok(uint clipboardSequence) => new(true, null, clipboardSequence);
    public static RestoreResult Fail(string message) => new(false, message, 0);
}
