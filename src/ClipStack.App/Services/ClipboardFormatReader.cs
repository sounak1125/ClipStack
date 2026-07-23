using System.Collections.Specialized;
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

    public async Task<NewClipboardItemData?> CaptureAsync(AppSettings settings, CancellationToken cancellationToken)
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
                await Task.Delay(RetryDelaysMs[i], cancellationToken).ConfigureAwait(true);
            }
        }

        if (data is null)
        {
            if (last is not null)
                _logger.Error("ClipboardGetDataObject", last);
            return null;
        }

        try
        {
            return CaptureFromDataObject(data, settings);
        }
        catch (OutOfMemoryException ex)
        {
            _logger.Error("ClipboardCaptureOom", ex);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error("ClipboardCapture", ex);
            return null;
        }
    }

    private NewClipboardItemData? CaptureFromDataObject(IDataObject data, AppSettings settings)
    {
        var payloads = new List<(ClipboardFormatKind Format, byte[] Bytes)>();
        string? text = null;
        string? html = null;
        string? rtf = null;
        BitmapSource? image = null;
        StringCollection? files = null;

        try
        {
            if (settings.CaptureFiles && data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] arr)
                {
                    files = new StringCollection();
                    files.AddRange(arr);
                }
            }
        }
        catch (Exception ex) { _logger.Error("ReadFileDrop", ex); }

        try
        {
            if (settings.CaptureImages && data.GetDataPresent(DataFormats.Bitmap))
            {
                if (data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                    image = ThumbnailService.ToFrozenBitmapSource(bmp);
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

        // Prefer files if present as dominant, else image, else rich/text
        if (files is { Count: > 0 })
        {
            var paths = ContentHasher.NormalizeFilePaths(files.Cast<string>());
            if (paths.Count == 0)
                return null;

            var listJson = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(paths, AppIdentity.JsonOptions));
            payloads.Add((ClipboardFormatKind.FileDropList, listJson));

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
                Payloads = payloads.Select(p => new PayloadWriteRequest { Format = p.Format, Bytes = p.Bytes }).ToList(),
            };
        }

        if (image is not null)
        {
            if (!ImageSizeGuard.IsWithinBudget(image.PixelWidth, image.PixelHeight))
            {
                _logger.Warn("ImageTooLarge", "Rejected image dimensions");
                return null;
            }

            var png = ThumbnailService.EncodePng(image);
            payloads.Add((ClipboardFormatKind.ImagePng, png));

            var thumb = _thumbnails.CreateThumbnailPng(image);
            if (thumb is not null)
                payloads.Add((ClipboardFormatKind.ThumbnailPng, thumb));

            // Release full image reference ASAP
            image = null;

            if (!string.IsNullOrEmpty(text) && settings.CaptureText)
                payloads.Add((ClipboardFormatKind.UnicodeText, Encoding.UTF8.GetBytes(text)));

            var hash = ContentHasher.ComputeHash(payloads.Select(p => (p.Format, (ReadOnlyMemory<byte>)p.Bytes)).ToList());
            if (!IsWithinSizeLimit(settings, payloads.Sum(p => (long)p.Bytes.Length)))
                return NewClipboardItemDataExtensions.OversizedSentinel();

            var width = 0;
            var height = 0;
            using (var ms = new MemoryStream(png))
            {
                var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                width = decoder.Frames[0].PixelWidth;
                height = decoder.Frames[0].PixelHeight;
            }

            return new NewClipboardItemData
            {
                DominantKind = ClipboardItemKind.Image,
                ContentHash = hash,
                PreviewText = string.IsNullOrWhiteSpace(text) ? "Image" : TextPreview.Create(text),
                CharacterCount = text?.Length ?? 0,
                ImageWidth = width,
                ImageHeight = height,
                Payloads = payloads.Select(p => new PayloadWriteRequest { Format = p.Format, Bytes = p.Bytes }).ToList(),
            };
        }

        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(html) && string.IsNullOrEmpty(rtf))
        {
            _logger.Info("UnsupportedClipboard", "No supported formats");
            return null;
        }

        if (!string.IsNullOrEmpty(text))
            payloads.Add((ClipboardFormatKind.UnicodeText, Encoding.UTF8.GetBytes(text)));
        if (!string.IsNullOrEmpty(html))
            payloads.Add((ClipboardFormatKind.Html, Encoding.UTF8.GetBytes(html)));
        if (!string.IsNullOrEmpty(rtf))
            payloads.Add((ClipboardFormatKind.Rtf, Encoding.UTF8.GetBytes(rtf)));

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
            // Store plain text only
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
            Payloads = payloads.Select(p => new PayloadWriteRequest { Format = p.Format, Bytes = p.Bytes }).ToList(),
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
