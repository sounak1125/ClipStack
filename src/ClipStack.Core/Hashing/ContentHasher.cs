using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ClipStack.Core.Models;

namespace ClipStack.Core.Hashing;

public static class ContentHasher
{
    public static string ComputeHash(
        IReadOnlyList<(ClipboardFormatKind Format, ReadOnlyMemory<byte> Bytes)> payloads,
        IReadOnlyList<string>? filePaths = null)
    {
        using var sha = SHA256.Create();
        Span<byte> header = stackalloc byte[8];

        var ordered = payloads
            .OrderBy(p => (int)p.Format)
            .ThenBy(p => p.Bytes.Length)
            .ToList();

        foreach (var (format, bytes) in ordered)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, (int)format);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], bytes.Length);
            sha.TransformBlock(header.ToArray(), 0, 8, null, 0);

            if (!bytes.IsEmpty)
            {
                var array = bytes.ToArray();
                sha.TransformBlock(array, 0, array.Length, null, 0);
            }
        }

        if (filePaths is { Count: > 0 })
        {
            var normalized = NormalizeFilePaths(filePaths);
            foreach (var path in normalized)
            {
                var pathBytes = Encoding.UTF8.GetBytes(path);
                BinaryPrimitives.WriteInt32LittleEndian(header, unchecked((int)0x46494C45)); // 'FILE'
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], pathBytes.Length);
                sha.TransformBlock(header.ToArray(), 0, 8, null, 0);
                sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            }
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// The hash a clipboard carrying nothing but this text produces on capture.
    /// </summary>
    /// <remarks>
    /// A plain-text paste publishes only <see cref="ClipboardFormatKind.UnicodeText"/>, so
    /// the clip that comes straight back hashes to this rather than to the stored clip's
    /// hash — which for rich text also covers its HTML and RTF. Self-copy suppression has
    /// to know both, or ClipStack records its own paste as a new clip.
    /// </remarks>
    public static string ComputeTextOnlyHash(string text) =>
        ComputeHash([(ClipboardFormatKind.UnicodeText, (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(text))]);

    public static IReadOnlyList<string> NormalizeFilePaths(IEnumerable<string> paths)
    {
        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }
}
