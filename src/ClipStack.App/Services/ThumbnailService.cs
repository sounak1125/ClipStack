using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core.Utilities;

namespace ClipStack.Services;

internal sealed class ThumbnailService
{
    public const int MaxWidth = 256;
    public const int MaxHeight = 160;

    private readonly FileLogger _logger;

    public ThumbnailService(FileLogger logger)
    {
        _logger = logger;
    }

    public byte[]? CreateThumbnailPng(BitmapSource source)
    {
        try
        {
            var scale = Math.Min(1.0, Math.Min(MaxWidth / (double)source.PixelWidth, MaxHeight / (double)source.PixelHeight));
            BitmapSource scaled = source;
            if (scale < 1.0)
            {
                scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.Error("CreateThumbnail", ex);
            return null;
        }
    }

    public static BitmapSource? LoadFrozenThumbnail(string path)
    {
        if (!File.Exists(path))
            return null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public byte[]? CreateThumbnailPngFromImageBytes(byte[] imageBytes)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.CanFreeze && !frame.IsFrozen)
                frame.Freeze();
            return CreateThumbnailPng(frame);
        }
        catch (Exception ex)
        {
            _logger.Error("CreateThumbnailFromBytes", ex);
            return null;
        }
    }

    public static bool TryGetImageDimensions(byte[] imageBytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static byte[]? TryReadClipboardPng(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent("PNG"))
                return null;

            if (data.GetData("PNG") is not Stream stream)
                return null;

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            return bytes.Length > 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? LoadFrozenFromBytes(byte[] imageBytes)
    {
        using var ms = new MemoryStream(imageBytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource? ToFrozenBitmapSource(BitmapSource? source)
    {
        if (source is null) return null;
        if (source.CanFreeze && !source.IsFrozen)
            source.Freeze();
        return source;
    }
}
