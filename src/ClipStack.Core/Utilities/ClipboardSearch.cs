using ClipStack.Core.Models;

namespace ClipStack.Core.Utilities;

/// <summary>
/// Filter predicate for the popup's search box.
/// </summary>
/// <remarks>
/// Matching runs against data already held in the in-memory index — the stored preview
/// text and, for file clips, the captured paths. It deliberately does not read payload
/// files from disk: the filter re-runs on every keystroke, and touching up to
/// <see cref="Settings.AppSettings.MaxHistoryLimit"/> payload files per keystroke would
/// stall the UI thread for exactly the workload the popup is meant to feel instant on.
/// </remarks>
public static class ClipboardSearch
{
    /// <summary>Whitespace-separated terms must all match somewhere in the item (AND).</summary>
    public static string[] ParseTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static bool Matches(ClipboardItem item, string? query) =>
        Matches(item, ParseTerms(query));

    public static bool Matches(ClipboardItem item, string[] terms)
    {
        if (terms.Length == 0)
            return true;

        foreach (var term in terms)
        {
            if (!MatchesTerm(item, term))
                return false;
        }

        return true;
    }

    private static bool MatchesTerm(ClipboardItem item, string term)
    {
        // Both properties are settable and JSON-deserialized, so an index written by a
        // future/edited build can legitimately carry nulls here.
        if (item.PreviewText?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        foreach (var path in item.FilePaths ?? [])
        {
            if (path?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return KindLabel(item.DominantKind).Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lets "image" or "files" narrow the list to a clip kind.</summary>
    public static string KindLabel(ClipboardItemKind kind) => kind switch
    {
        ClipboardItemKind.Text => "text",
        ClipboardItemKind.RichText => "rich text",
        ClipboardItemKind.Image => "image",
        ClipboardItemKind.Files => "files",
        _ => "unknown",
    };
}
