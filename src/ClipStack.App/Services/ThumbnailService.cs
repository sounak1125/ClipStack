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

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource? ToFrozenBitmapSource(System.Windows.Media.Imaging.BitmapSource? source)
    {
        if (source is null) return null;
        if (source.CanFreeze && !source.IsFrozen)
            source.Freeze();
        return source;
    }
}
