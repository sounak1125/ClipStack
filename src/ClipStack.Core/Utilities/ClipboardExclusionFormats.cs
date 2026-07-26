namespace ClipStack.Core.Utilities;

/// <summary>
/// Well-known clipboard formats that applications (password managers, browsers, banking
/// apps) place on the clipboard to ask history tools not to record a clip.
/// </summary>
/// <remarks>
/// Two families exist. <see cref="PresenceMarkers"/> carry no meaningful value — an app
/// setting the format at all is opting the clip out. <see cref="PolicyMarkers"/> are
/// DWORD-valued, where 0 means "exclude"; apps that want inclusion normally omit the
/// format entirely rather than writing 1.
///
/// "CanUploadToCloudClipboard" is deliberately absent: it governs cross-device sync
/// only, and Windows itself still keeps such clips in local history. Treating it as a
/// local-storage opt-out would drop clips the user expects to keep.
/// </remarks>
public static class ClipboardExclusionFormats
{
    /// <summary>Set by password managers; presence alone means "do not record".</summary>
    public const string ExcludeFromMonitorProcessing = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>Long-standing convention honoured by third-party clipboard tools.</summary>
    public const string ViewerIgnoreSpaced = "Clipboard Viewer Ignore";

    /// <summary>Compact spelling of the same convention.</summary>
    public const string ViewerIgnore = "ClipboardViewerIgnore";

    /// <summary>Windows 10+ DWORD marker; 0 means "keep out of clipboard history".</summary>
    public const string CanIncludeInClipboardHistory = "CanIncludeInClipboardHistory";

    public static readonly string[] PresenceMarkers =
    [
        ExcludeFromMonitorProcessing,
        ViewerIgnoreSpaced,
        ViewerIgnore,
    ];

    public static readonly string[] PolicyMarkers =
    [
        CanIncludeInClipboardHistory,
    ];

    /// <summary>
    /// Interprets a DWORD-style marker value that is known to be present on the clipboard.
    /// Returns <see langword="true"/> only when the value positively permits capture.
    /// </summary>
    /// <remarks>
    /// Fails closed: a marker that is present but whose value cannot be read is treated as
    /// exclusion. An app that sets one of these formats is expressing a clipboard-history
    /// policy, so the privacy-preserving reading wins whenever the value is ambiguous.
    /// </remarks>
    public static bool PolicyValueAllowsCapture(object? value)
    {
        switch (value)
        {
            case null:
                return false;
            case bool flag:
                return flag;
            case int i:
                return i != 0;
            case uint u:
                return u != 0;
            case long l:
                return l != 0;
            case short s:
                return s != 0;
            case byte b:
                return b != 0;
            case string text:
                var trimmed = text.Trim().Trim('\0').Trim();
                return int.TryParse(trimmed, out var parsed) && parsed != 0;
            case byte[] bytes:
                return BytesAllowCapture(bytes);
            case Stream stream:
                return StreamAllowsCapture(stream);
            default:
                return false;
        }
    }

    /// <summary>A DWORD is four bytes; any non-zero byte means the value is not 0.</summary>
    public static bool BytesAllowCapture(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 4)
            return false;

        foreach (var b in bytes)
        {
            if (b != 0)
                return true;
        }

        return false;
    }

    private static bool StreamAllowsCapture(Stream stream)
    {
        try
        {
            if (stream.CanSeek)
                stream.Position = 0;

            Span<byte> buffer = stackalloc byte[4];
            var read = 0;
            while (read < buffer.Length)
            {
                var chunk = stream.Read(buffer[read..]);
                if (chunk <= 0)
                    break;
                read += chunk;
            }

            return BytesAllowCapture(buffer[..read]);
        }
        catch
        {
            return false;
        }
    }
}
