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
    /// True when there is exactly one path and it is an existing local image file.
    /// Mixed or multi-file drops stay as normal file lists.
    /// </summary>
    public static bool IsSingleExistingImageFile(IReadOnlyList<string> paths)
    {
        if (paths.Count != 1)
            return false;

        var path = paths[0];
        if (!IsImageFilePath(path) || !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            if (!OperatingSystem.IsWindows())
                return File.Exists(path);

            // A clipboard path may be a network share, device path, or alternate data
            // stream. Keep those as file-drop references without opening them on copy.
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path.AsSpan(2).Contains(':'))
                return false;

            var root = Path.GetPathRoot(path)!;
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
                return false;

            // Check from the volume outward so an intermediate link cannot send this
            // automatic read to a network share or trigger a cloud file download.
            var current = root;
            var attributes = FileAttributes.Directory;
            foreach (var component in path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    return false;
            }

            return (attributes & FileAttributes.Directory) == 0;
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
