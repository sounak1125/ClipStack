namespace ClipStack.Core.Models;

public sealed class ClipboardItem
{
    public Guid Id { get; set; }

    public DateTimeOffset CapturedUtc { get; set; }

    public DateTimeOffset LastUsedUtc { get; set; }

    public ClipboardItemKind DominantKind { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public long TotalSizeBytes { get; set; }

    public string PreviewText { get; set; } = string.Empty;

    public int CharacterCount { get; set; }

    public int? ImageWidth { get; set; }

    public int? ImageHeight { get; set; }

    public int FileCount { get; set; }

    public List<ClipboardPayload> Payloads { get; set; } = [];

    public List<string> FilePaths { get; set; } = [];

    public string? ThumbnailRelativePath { get; set; }

    /// <summary>
    /// Pinned clips sort above the rest and are never evicted, so the history limit
    /// applies to unpinned clips only.
    /// </summary>
    public bool IsPinned { get; set; }
}
