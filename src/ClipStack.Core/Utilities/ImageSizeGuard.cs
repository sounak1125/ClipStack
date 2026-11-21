namespace ClipStack.Core.Utilities;

/// <summary>
/// Estimates uncompressed bitmap memory and rejects unsafe dimensions.
/// </summary>
public static class ImageSizeGuard
{
    public const int MaxDimension = 16384;
    public const long DefaultMaxUncompressedBytes = 512L * 1024 * 1024;
    public const int MaxEncodedBytes = 512 * 1024 * 1024;

    public static byte[]? ReadEncodedImage(Stream source, long configuredLimit, out bool oversized)
    {
        var limit = (int)(configuredLimit > 0 ? Math.Min(configuredLimit, MaxEncodedBytes) : MaxEncodedBytes);
        oversized = false;

        if (source.CanSeek)
        {
            var length = source.Length - source.Position;
            if (length > limit)
            {
                oversized = true;
                return null;
            }

            if (length <= 0)
                return null;

            var bytes = new byte[(int)length];
            source.ReadExactly(bytes);
            return bytes;
        }

        using var copy = new MemoryStream();
        Span<byte> buffer = stackalloc byte[4096];
        while (true)
        {
            var count = source.Read(buffer[..(int)Math.Min(buffer.Length, limit - copy.Length + 1)]);
            if (count == 0)
                return copy.Length == 0 ? null : copy.ToArray();

            if (copy.Length + count > limit)
            {
                oversized = true;
                return null;
            }

            copy.Write(buffer[..count]);
        }
    }

    public static bool TryEstimateUncompressedBytes(int width, int height, int bytesPerPixel, out long bytes)
    {
        bytes = 0;
        if (width <= 0 || height <= 0 || bytesPerPixel <= 0)
            return false;
        if (width > MaxDimension || height > MaxDimension)
            return false;

        try
        {
            checked
            {
                bytes = (long)width * height * bytesPerPixel;
            }
            return bytes > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static bool IsWithinBudget(int width, int height, long maxUncompressedBytes = DefaultMaxUncompressedBytes)
        => IsWithinBudget(width, height, 4, maxUncompressedBytes);

    public static bool IsWithinBudget(int width, int height, int bytesPerPixel, long maxUncompressedBytes)
    {
        if (!TryEstimateUncompressedBytes(width, height, bytesPerPixel, out var bytes))
            return false;
        return maxUncompressedBytes <= 0 || bytes <= maxUncompressedBytes;
    }
}
