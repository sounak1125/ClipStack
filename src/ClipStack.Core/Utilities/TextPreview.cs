using System.Text;

namespace ClipStack.Core.Utilities;

public static class TextPreview
{
    public static string Create(string? text, int maxChars = 240, int maxLines = 3)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var normalized = NormalizeWhitespace(text);
        if (normalized.Length <= maxChars)
            return TruncateLines(normalized, maxLines);

        return TruncateLines(normalized[..maxChars].TrimEnd() + "…", maxLines);
    }

    public static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasWs = false;
        var lineCount = 0;

        foreach (var ch in text)
        {
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
