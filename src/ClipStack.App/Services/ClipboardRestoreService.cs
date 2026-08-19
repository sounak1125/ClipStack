using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipStack.Core.Hashing;
using ClipStack.Core.Models;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;

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
            for (var i = 0; i < RetryDelaysMs.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    _suppression.Arm(SuppressionHashes(item, payloads), TimeSpan.FromSeconds(2));
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

    /// <summary>
    /// Every hash the clip we just published can be captured back as.
    /// </summary>
    /// <remarks>
    /// A full restore puts back every stored format, so it re-hashes to the stored hash.
    /// A plain-text restore publishes text alone and hashes to something else entirely,
    /// so arming with the stored hash alone let the paste come straight back in as a new
    /// clip — one duplicate plain-text row per Shift+Enter on a styled clip.
    ///
    /// Keyed off what was actually published rather than off the plain-text flag, so a
    /// clip whose HTML or RTF payload has gone missing is covered by the same reasoning.
    /// </remarks>
    private static IReadOnlyCollection<string> SuppressionHashes(ClipboardItem item, RestorePayloads payloads)
    {
        var publishedTextOnly =
            !string.IsNullOrEmpty(payloads.Text)
            && string.IsNullOrEmpty(payloads.Html)
            && string.IsNullOrEmpty(payloads.Rtf)
            && payloads.Image is null
            && payloads.ExistingFiles.Length == 0;

        return publishedTextOnly
            ? [item.ContentHash, ContentHasher.ComputeTextOnlyHash(payloads.Text!)]
            : [item.ContentHash];
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

        BitmapSource? image = null;
        byte[]? png = null;

        try
        {
            var original = _history.ReadPayloadBytes(item, ClipboardFormatKind.ImageOriginal);
            if (original is { Length: > 0 })
            {
                image = ThumbnailService.LoadFrozenFromBytes(original);

                // Expose raw PNG only when the original genuinely is one.
                var originalPayload = item.Payloads.FirstOrDefault(p => p.Format == ClipboardFormatKind.ImageOriginal);
                if (image is not null
                    && originalPayload is not null
                    && originalPayload.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    png = original;
                }
            }

            if (image is null)
            {
                var pngBytes = _history.ReadPayloadBytes(item, ClipboardFormatKind.ImagePng);
                if (pngBytes is { Length: > 0 })
                {
                    image = ThumbnailService.LoadFrozenFromBytes(pngBytes);
                    if (image is not null)
                        png = pngBytes;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("RestoreImage", ex);
        }

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

        return data;
    }
}

internal readonly record struct RestoreResult(bool Success, string? Message)
{
    public static RestoreResult Ok() => new(true, null);
    public static RestoreResult Fail(string message) => new(false, message);
}
