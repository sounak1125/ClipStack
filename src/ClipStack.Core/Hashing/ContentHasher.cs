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
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[8];

        var ordered = payloads
            .OrderBy(p => (int)p.Format)
            .ThenBy(p => p.Bytes.Length)
            .ToList();

        foreach (var (format, bytes) in ordered)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, (int)format);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], bytes.Length);
            sha.AppendData(header);
            sha.AppendData(bytes.Span);
        }

        if (filePaths is { Count: > 0 })
        {
            var normalized = NormalizeFilePaths(filePaths);
            foreach (var path in normalized)
            {
                var pathBytes = Encoding.UTF8.GetBytes(path);
                BinaryPrimitives.WriteInt32LittleEndian(header, unchecked((int)0x46494C45)); // 'FILE'
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], pathBytes.Length);
                sha.AppendData(header);
                sha.AppendData(pathBytes);
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// The hash a clipboard carrying nothing but this text produces on capture.
    /// </summary>
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
