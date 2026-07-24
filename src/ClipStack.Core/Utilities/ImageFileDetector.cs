namespace ClipStack.Core.Utilities;

/// <summary>
/// Classifies file paths for high-quality image capture from Explorer FileDrop.
/// </summary>
public static class ImageFileDetector
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff",
    };

    public static bool IsImageFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when there is exactly one path and it is an existing image file.
    /// Mixed or multi-file drops stay as normal file lists.
    /// </summary>
    public static bool IsSingleExistingImageFile(IReadOnlyList<string> paths)
    {
        if (paths.Count != 1)
            return false;

        var path = paths[0];
        if (!IsImageFilePath(path))
            return false;

        try
        {
            return File.Exists(path) && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static string SafeOriginalFileName(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            ext = ".bin";

        if (ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            ext = ".jpg";

        return "original" + ext.ToLowerInvariant();
    }
}
