using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core.Utilities;

namespace ClipStack.Services;

internal sealed class ThumbnailService
{
    public const int MaxWidth = 256;
    public const int MaxHeight = 160;
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly FileLogger _logger;

    public ThumbnailService(FileLogger logger)
    {
        _logger = logger;
    }

    public byte[]? CreateThumbnailPng(BitmapSource source)
    {
        try
        {
            if (!IsWithinDecodeBudget(source))
                return null;

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

    public byte[]? CreateThumbnailPngFromImageBytes(byte[] imageBytes, bool pngOnly = false)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            return CreateThumbnailPng(ReadFirstFrame(ms, pngOnly));
        }
        catch (Exception ex)
        {
            _logger.Error("CreateThumbnailFromBytes", ex);
            return null;
        }
    }

    public static bool TryGetSafeImageDimensions(byte[] imageBytes, out int width, out int height, bool pngOnly = false)
    {
        width = 0;
        height = 0;
        if (imageBytes.Length > ImageSizeGuard.MaxEncodedBytes || (pngOnly && !HasPngSignature(imageBytes)))
            return false;

        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            var frame = ReadFirstFrame(ms, pngOnly);
            if (!IsWithinDecodeBudget(frame))
                return false;

            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static byte[]? TryReadClipboardPng(IDataObject data, long configuredLimit, out bool oversized)
    {
        oversized = false;
        try
        {
            if (!data.GetDataPresent("PNG", autoConvert: false))
                return null;

            if (data.GetData("PNG", autoConvert: false) is not Stream stream)
                return null;

            return ImageSizeGuard.ReadEncodedImage(stream, configuredLimit, out oversized);
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? LoadFrozenFromBytes(byte[] imageBytes, bool pngOnly = false)
    {
        if (!TryGetSafeImageDimensions(imageBytes, out _, out _, pngOnly))
            return null;

        using var ms = new MemoryStream(imageBytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static bool HasPngSignature(ReadOnlySpan<byte> bytes) => bytes.StartsWith(PngSignature);

    private static BitmapFrame ReadFirstFrame(Stream stream, bool pngOnly)
    {
        const BitmapCreateOptions options = BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat;
        return pngOnly
            ? new PngBitmapDecoder(stream, options, BitmapCacheOption.OnDemand).Frames[0]
            : BitmapFrame.Create(stream, options, BitmapCacheOption.OnDemand);
    }

    public static bool IsWithinDecodeBudget(BitmapSource source) => ImageSizeGuard.IsWithinBudget(
        source.PixelWidth, source.PixelHeight, Math.Max(4, (source.Format.BitsPerPixel + 7) / 8),
        ImageSizeGuard.DefaultMaxUncompressedBytes);

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
