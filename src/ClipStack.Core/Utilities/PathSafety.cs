namespace ClipStack.Core.Utilities;

public static class PathSafety
{
    public static bool IsPathInsideRoot(string rootDirectory, string candidatePath)
    {
        try
        {
            var root = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(candidatePath);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string ResolveSafeRelativePath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Unsafe relative path.");
        }

        var combined = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        if (!IsPathInsideRoot(rootDirectory, combined))
            throw new InvalidOperationException("Path traversal detected.");

        return combined;
    }

    public static async Task AtomicReplaceFileAsync(string destinationPath, string temporaryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationPath + ".bak", ignoreMetadataErrors: true);
            try { File.Delete(destinationPath + ".bak"); } catch { /* best effort */ }
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public static void AtomicReplaceFile(string destinationPath, string temporaryPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationPath + ".bak", ignoreMetadataErrors: true);
            try { File.Delete(destinationPath + ".bak"); } catch { /* best effort */ }
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }
}
