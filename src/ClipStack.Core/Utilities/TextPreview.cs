using System.Text;

namespace ClipStack.Core.Utilities;

public static class TextPreview
{
    public static string Create(string? text, int maxChars = 240, int maxLines = 3)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Only the first maxChars survive, and normalisation never expands the string, so
        // scanning a bounded window is enough. Without this a single-line 50 MB clip
        // (minified JSON, base64, one long log line) walked every character and allocated
        // a StringBuilder the size of the whole clipboard to produce 240 characters.
        var scanLimit = ScanWindow(maxChars);
        var normalized = NormalizeWhitespace(text, scanLimit);

        if (normalized.Length <= maxChars)
            return TruncateLines(normalized, maxLines);

        return TruncateLines(normalized[..maxChars].TrimEnd() + "…", maxLines);
    }

    /// <summary>
    /// How much input to read for a given preview length. Runs of whitespace collapse to
    /// one character, so a generous multiple guarantees the window still yields a full
    /// preview even for heavily-spaced text.
    /// </summary>
    private static int ScanWindow(int maxChars)
    {
        if (maxChars <= 0)
            return 0;

        // Widened so a large maxChars cannot overflow into a negative window.
        var window = (long)maxChars * 8;
        return window > int.MaxValue ? int.MaxValue : (int)window;
    }

    public static string NormalizeWhitespace(string text) =>
        NormalizeWhitespace(text, int.MaxValue);

    public static string NormalizeWhitespace(string text, int scanLimit)
    {
        if (scanLimit <= 0 || text.Length == 0)
            return string.Empty;

        var capacity = (int)Math.Min(text.Length, (long)scanLimit);
        var sb = new StringBuilder(capacity);
        var lastWasWs = false;
        var lineCount = 0;
        var scanned = 0;

        foreach (var ch in text)
        {
            if (scanned++ >= scanLimit)
                break;

            if (ch is '\r')
                continue;

            if (ch is '\n')
            {
                if (lineCount >= 2)
                    break;
                sb.Append('\n');
                lineCount++;
                lastWasWs = true;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWs)
                {
                    sb.Append(' ');
                    lastWasWs = true;
                }
                continue;
            }

            sb.Append(ch);
            lastWasWs = false;
        }

        return sb.ToString().Trim();
    }

    private static string TruncateLines(string text, int maxLines)
    {
        var lines = text.Split('\n');
        if (lines.Length <= maxLines)
            return text;
        return string.Join('\n', lines.Take(maxLines));
    }

    public static long Utf8ByteCount(string text) => Encoding.UTF8.GetByteCount(text);
}
