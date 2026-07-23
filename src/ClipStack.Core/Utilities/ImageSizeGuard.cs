namespace ClipStack.Core.Utilities;

/// <summary>
/// Estimates uncompressed bitmap memory and rejects unsafe dimensions.
/// </summary>
public static class ImageSizeGuard
{
    public const int MaxDimension = 16384;
    public const long DefaultMaxUncompressedBytes = 512L * 1024 * 1024;

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
    {
        if (!TryEstimateUncompressedBytes(width, height, 4, out var bytes))
            return false;
        return maxUncompressedBytes <= 0 || bytes <= maxUncompressedBytes;
    }
}
