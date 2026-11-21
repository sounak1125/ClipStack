using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipStack.Core;
using ClipStack.Core.Hashing;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;

namespace ClipStack.Services;

/// <summary>
/// Raw clipboard content, marshalled off the clipboard on the STA/UI thread.
/// </summary>
/// <remarks>
/// Every member is a string, a byte array, or a <b>frozen</b> <see cref="BitmapSource"/>,
/// so the snapshot can be handed to a background thread. This is the seam that keeps
/// decoding, hashing, encoding, and disk I/O off the UI thread.
/// </remarks>
internal sealed class ClipboardSnapshot
{
    public string? Text { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }

    /// <summary>Raw bytes of the clipboard's own "PNG" format, when the source offered one.</summary>
    public byte[]? ClipboardPng { get; init; }
    public bool ImagePayloadTooLarge { get; init; }

    /// <summary>Frozen bitmap fallback, used when no "PNG" format was offered.</summary>
    public BitmapSource? Image { get; init; }

    public IReadOnlyList<string> FilePaths { get; init; } = [];

    public bool IsEmpty =>
        string.IsNullOrEmpty(Text)
        && string.IsNullOrEmpty(Html)
        && string.IsNullOrEmpty(Rtf)
        && ClipboardPng is not { Length: > 0 }
        && !ImagePayloadTooLarge
        && Image is null
        && FilePaths.Count == 0;
}

internal sealed class ClipboardFormatReader
{
    private static readonly int[] RetryDelaysMs = [20, 40, 80, 120, 180, 250];

    private readonly FileLogger _logger;
    private readonly ThumbnailService _thumbnails;

    public ClipboardFormatReader(FileLogger logger, ThumbnailService thumbnails)
    {
        _logger = logger;
        _thumbnails = thumbnails;
    }

    /// <summary>
    /// Phase 1 — must run on the STA/UI thread. Marshals clipboard formats into plain
    /// data and returns <see langword="null"/> when there is nothing to store or the
    /// source application opted out.
    /// </summary>
    public async Task<ClipboardSnapshot?> ReadSnapshotAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        IDataObject? data = null;
        Exception? last = null;

        for (var i = 0; i < RetryDelaysMs.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                data = Clipboard.GetDataObject();
                if (data is not null)
                    break;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            // Another process commonly holds the clipboard open for a few milliseconds
            // after a copy; back off on a null result too, not just on an exception.
            if (i < RetryDelaysMs.Length - 1)
                await Task.Delay(RetryDelaysMs[i], cancellationToken).ConfigureAwait(true);
        }

        if (data is null)
        {
            if (last is not null)
                _logger.Error("ClipboardGetDataObject", last);
            return null;
        }

        // Opt-out check runs before a single byte of content is touched.
        if (ClipboardExclusionDetector.IsPresent(data, ClipboardRestoreService.RestoredFormat, _logger))
            return null;

        if (ClipboardExclusionDetector.IsExcluded(data, _logger, out var marker))
        {
            _logger.Info("ClipboardExcluded", $"Skipped by format: {marker}");
            return null;
        }

        try
        {
            var snapshot = ReadSnapshotCore(data, settings);
            return snapshot is null || snapshot.IsEmpty ? null : snapshot;
        }
        catch (OutOfMemoryException ex)
        {
            _logger.Error("ClipboardReadOom", ex);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error("ClipboardRead", ex);
            return null;
        }
    }

    private ClipboardSnapshot? ReadSnapshotCore(IDataObject data, AppSettings settings)
    {
        string? text = null;
        string? html = null;
        string? rtf = null;
        byte[]? clipboardPng = null;
        var imagePayloadTooLarge = false;
        BitmapSource? image = null;
        IReadOnlyList<string> files = [];

        // Read FileDrop when capturing files OR images (HQ Explorer image path).
        try
        {
            if ((settings.CaptureFiles || settings.CaptureImages) && data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] arr)
                    files = arr;
            }
        }
        catch (Exception ex) { _logger.Error("ReadFileDrop", ex); }

        try
        {
            if (settings.CaptureImages)
            {
                clipboardPng = ThumbnailService.TryReadClipboardPng(data, settings.MaxItemSizeBytes, out imagePayloadTooLarge);
                if (clipboardPng is null && !imagePayloadTooLarge && data.GetDataPresent(DataFormats.Bitmap)
                    && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                {
                    // Freezing here is what makes the bitmap safe to touch off-thread.
                    if (ThumbnailService.IsWithinDecodeBudget(bmp))
                        image = ThumbnailService.ToFrozenBitmapSource(bmp);
                    else
                        imagePayloadTooLarge = true;
                }
            }
        }
        catch (Exception ex) { _logger.Error("ReadBitmap", ex); }

        try
        {
            if ((settings.CaptureText || settings.CaptureRichText) && data.GetDataPresent(DataFormats.UnicodeText))
                text = data.GetData(DataFormats.UnicodeText) as string;
            else if ((settings.CaptureText || settings.CaptureRichText) && data.GetDataPresent(DataFormats.Text))
                text = data.GetData(DataFormats.Text) as string;
        }
        catch (Exception ex) { _logger.Error("ReadText", ex); }

        if (settings.CaptureRichText)
        {
            try
            {
                if (data.GetDataPresent(DataFormats.Html))
                    html = data.GetData(DataFormats.Html) as string;
            }
            catch (Exception ex) { _logger.Error("ReadHtml", ex); }

            try
            {
                if (data.GetDataPresent(DataFormats.Rtf))
                    rtf = data.GetData(DataFormats.Rtf) as string;
            }
            catch (Exception ex) { _logger.Error("ReadRtf", ex); }
        }

        if (image is { IsFrozen: false })
        {
            // Could not be frozen, so it cannot cross threads — drop it rather than
            // risk an InvalidOperationException on the background thread.
            _logger.Warn("ReadBitmap", "Clipboard bitmap could not be frozen; ignoring image payload.");
            image = null;
        }

        return new ClipboardSnapshot
        {
            Text = text,
            Html = html,
            Rtf = rtf,
            ClipboardPng = clipboardPng,
            ImagePayloadTooLarge = imagePayloadTooLarge,
            Image = image,
            FilePaths = files,
        };
    }

    /// <summary>
    /// Phase 2 — safe to run on a background thread. Decodes, resizes, encodes, and
    /// hashes; returns the item ready for <see cref="HistoryStore.AddOrPromote"/>.
    /// </summary>
    public NewClipboardItemData? BuildItemData(ClipboardSnapshot snapshot, AppSettings settings)
    {
        try
        {
            return BuildItemDataCore(snapshot, settings);
        }
        catch (OutOfMemoryException ex)
        {
            _logger.Error("ClipboardBuildOom", ex);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error("ClipboardBuild", ex);
            return null;
        }
    }

    private NewClipboardItemData? BuildItemDataCore(ClipboardSnapshot snapshot, AppSettings settings)
    {
        var payloads = new List<(ClipboardFormatKind Format, byte[] Bytes, string? RelativeFileName)>();
        var text = snapshot.Text;

        if (snapshot.FilePaths.Count > 0)
        {
            var paths = ContentHasher.NormalizeFilePaths(snapshot.FilePaths);
            if (paths.Count == 0)
                return null;

            // Single Explorer image file → store original high-quality bytes as Image.
            if (settings.CaptureImages && ImageFileDetector.IsSingleExistingImageFile(paths))
            {
                var hq = TryCaptureOriginalImageFile(paths[0], paths, settings, text);
                if (hq is not null)
                    return hq;
                // Fall through to path-only Files if original read fails and CaptureFiles is on.
            }

            if (!settings.CaptureFiles)
                return null;

            var listJson = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(paths, AppIdentity.JsonOptions));
            payloads.Add((ClipboardFormatKind.FileDropList, listJson, null));

            var hash = ContentHasher.ComputeHash(
                payloads.Select(p => (p.Format, (ReadOnlyMemory<byte>)p.Bytes)).ToList(),
                paths);

            if (!IsWithinSizeLimit(settings, payloads.Sum(p => (long)p.Bytes.Length)))
                return NewClipboardItemDataExtensions.OversizedSentinel();

            var names = paths.Take(2).Select(Path.GetFileName);
            return new NewClipboardItemData
            {
                DominantKind = ClipboardItemKind.Files,
                ContentHash = hash,
                PreviewText = string.Join(", ", names),
                CharacterCount = 0,
                FilePaths = paths.ToList(),
                Payloads = payloads.Select(p => new PayloadWriteRequest
                {
                    Format = p.Format,
                    Bytes = p.Bytes,
                    RelativeFileName = p.RelativeFileName,
                }).ToList(),
            };
        }

        if (snapshot.ImagePayloadTooLarge)
            return NewClipboardItemDataExtensions.OversizedSentinel();

        if (snapshot.ClipboardPng is { Length: > 0 })
            return BuildImageItemFromPngBytes(snapshot.ClipboardPng, settings, text);

        if (snapshot.Image is not null)
        {
            var image = snapshot.Image;
            if (!ThumbnailService.IsWithinDecodeBudget(image))
            {
                _logger.Warn("ImageTooLarge", "Rejected image dimensions");
                return null;
            }

            var png = ThumbnailService.EncodePng(image);
            return BuildImageItemFromPngBytes(png, settings, text, image);
        }

        var html = snapshot.Html;
        var rtf = snapshot.Rtf;

        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(html) && string.IsNullOrEmpty(rtf))
        {
            _logger.Info("UnsupportedClipboard", "No supported formats");
            return null;
        }

        if (!string.IsNullOrEmpty(text))
            payloads.Add((ClipboardFormatKind.UnicodeText, Encoding.UTF8.GetBytes(text), null));
        if (!string.IsNullOrEmpty(html))
            payloads.Add((ClipboardFormatKind.Html, Encoding.UTF8.GetBytes(html), null));
        if (!string.IsNullOrEmpty(rtf))
            payloads.Add((ClipboardFormatKind.Rtf, Encoding.UTF8.GetBytes(rtf), null));

        if (payloads.Count == 0)
            return null;

        var kind = (!string.IsNullOrEmpty(html) || !string.IsNullOrEmpty(rtf))
            ? ClipboardItemKind.RichText
            : ClipboardItemKind.Text;

        if (kind == ClipboardItemKind.Text && !settings.CaptureText)
            return null;
        if (kind == ClipboardItemKind.RichText && !settings.CaptureRichText && !settings.CaptureText)
            return null;
        if (kind == ClipboardItemKind.RichText && !settings.CaptureRichText && settings.CaptureText)
        {
            payloads.RemoveAll(p => p.Format is ClipboardFormatKind.Html or ClipboardFormatKind.Rtf);
            kind = ClipboardItemKind.Text;
        }

        var textHash = ContentHasher.ComputeHash(payloads.Select(p => (p.Format, (ReadOnlyMemory<byte>)p.Bytes)).ToList());
        var total = payloads.Sum(p => (long)p.Bytes.Length);
        if (!IsWithinSizeLimit(settings, total))
            return NewClipboardItemDataExtensions.OversizedSentinel();

        return new NewClipboardItemData
        {
            DominantKind = kind,
            ContentHash = textHash,
            PreviewText = TextPreview.Create(text ?? string.Empty),
            CharacterCount = text?.Length ?? 0,
            Payloads = payloads.Select(p => new PayloadWriteRequest
            {
                Format = p.Format,
                Bytes = p.Bytes,
                RelativeFileName = p.RelativeFileName,
            }).ToList(),
        };
    }

    private NewClipboardItemData? TryCaptureOriginalImageFile(
        string path,
        IReadOnlyList<string> paths,
        AppSettings settings,
        string? text)
    {
        try
        {
            byte[]? originalBytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan))
            {
                originalBytes = ImageSizeGuard.ReadEncodedImage(file, settings.MaxItemSizeBytes, out var oversized);
                if (oversized)
                    return NewClipboardItemDataExtensions.OversizedSentinel();
            }
            if (originalBytes is null)
                return null;

            if (!ThumbnailService.TryGetSafeImageDimensions(originalBytes, out var width, out var height))
            {
                _logger.Warn("OriginalImageDecode", "Rejected unsupported or over-limit image dimensions");
                return null;
            }

            var payloads = new List<PayloadWriteRequest>
            {
                new()
                {
                    Format = ClipboardFormatKind.ImageOriginal,
                    Bytes = originalBytes,
                    RelativeFileName = ImageFileDetector.SafeOriginalFileName(path),
                },
            };

            var listJson = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(paths, AppIdentity.JsonOptions));
            payloads.Add(new PayloadWriteRequest
            {
                Format = ClipboardFormatKind.FileDropList,
                Bytes = listJson,
            });

            var thumb = _thumbnails.CreateThumbnailPngFromImageBytes(originalBytes);
            if (thumb is not null)
            {
                payloads.Add(new PayloadWriteRequest
                {
                    Format = ClipboardFormatKind.ThumbnailPng,
                    Bytes = thumb,
                });
            }

            if (!string.IsNullOrEmpty(text) && settings.CaptureText)
            {
                payloads.Add(new PayloadWriteRequest
                {
                    Format = ClipboardFormatKind.UnicodeText,
                    Bytes = Encoding.UTF8.GetBytes(text),
                });
            }

            var total = payloads.Sum(p => (long)p.Bytes.LongLength);
            if (!IsWithinSizeLimit(settings, total))
                return NewClipboardItemDataExtensions.OversizedSentinel();

            var hashPayloads = payloads
                .Select(p => (p.Format, (ReadOnlyMemory<byte>)p.Bytes))
                .ToList();
            var hash = ContentHasher.ComputeHash(hashPayloads, paths);

            return new NewClipboardItemData
            {
                DominantKind = ClipboardItemKind.Image,
                ContentHash = hash,
                PreviewText = Path.GetFileName(path),
                CharacterCount = text?.Length ?? 0,
                ImageWidth = width,
                ImageHeight = height,
                FilePaths = paths.ToList(),
                Payloads = payloads,
            };
        }
        catch (Exception ex)
        {
            _logger.Error("CaptureOriginalImageFile", ex);
            return null;
        }
    }

    private NewClipboardItemData? BuildImageItemFromPngBytes(
        byte[] png,
        AppSettings settings,
        string? text,
        BitmapSource? sourceForThumb = null)
    {
        if (png.Length > ImageSizeGuard.MaxEncodedBytes || !IsWithinSizeLimit(settings, png.Length))
            return NewClipboardItemDataExtensions.OversizedSentinel();

        var width = sourceForThumb?.PixelWidth ?? 0;
        var height = sourceForThumb?.PixelHeight ?? 0;
        if (sourceForThumb is null && !ThumbnailService.TryGetSafeImageDimensions(png, out width, out height, pngOnly: true))
        {
            _logger.Warn("ClipboardImageDecode", "Rejected unsupported or over-limit PNG dimensions");
            return null;
        }

        var payloads = new List<(ClipboardFormatKind Format, byte[] Bytes, string? RelativeFileName)>
        {
            (ClipboardFormatKind.ImagePng, png, null),
        };

        byte[]? thumb;
        if (sourceForThumb is not null)
            thumb = _thumbnails.CreateThumbnailPng(sourceForThumb);
        else
            thumb = _thumbnails.CreateThumbnailPngFromImageBytes(png, pngOnly: true);

        if (thumb is not null)
            payloads.Add((ClipboardFormatKind.ThumbnailPng, thumb, null));

        if (!string.IsNullOrEmpty(text) && settings.CaptureText)
            payloads.Add((ClipboardFormatKind.UnicodeText, Encoding.UTF8.GetBytes(text), null));

        if (!IsWithinSizeLimit(settings, payloads.Sum(p => (long)p.Bytes.Length)))
            return NewClipboardItemDataExtensions.OversizedSentinel();

        var hash = ContentHasher.ComputeHash(payloads.Select(p => (p.Format, (ReadOnlyMemory<byte>)p.Bytes)).ToList());

        return new NewClipboardItemData
        {
            DominantKind = ClipboardItemKind.Image,
            ContentHash = hash,
            PreviewText = string.IsNullOrWhiteSpace(text) ? "Image" : TextPreview.Create(text),
            CharacterCount = text?.Length ?? 0,
            ImageWidth = width,
            ImageHeight = height,
            Payloads = payloads.Select(p => new PayloadWriteRequest
            {
                Format = p.Format,
                Bytes = p.Bytes,
                RelativeFileName = p.RelativeFileName,
            }).ToList(),
        };
    }

    private static bool IsWithinSizeLimit(AppSettings settings, long totalBytes)
    {
        if (settings.MaxItemSizeBytes <= 0)
            return true;
        return totalBytes <= settings.MaxItemSizeBytes;
    }
}

internal static class NewClipboardItemDataExtensions
{
    public const string OversizedHash = "__oversized__";

    public static NewClipboardItemData OversizedSentinel() => new()
    {
        DominantKind = ClipboardItemKind.Unknown,
        ContentHash = OversizedHash,
        PreviewText = string.Empty,
        Payloads = Array.Empty<PayloadWriteRequest>(),
    };

    public static bool IsOversized(this NewClipboardItemData data) =>
        data.ContentHash == OversizedHash;
}
