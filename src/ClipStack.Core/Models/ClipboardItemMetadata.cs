namespace ClipStack.Core.Models;

/// <summary>
/// The facts about a clip that its payload files cannot express, stored beside them.
/// </summary>
/// <remarks>
/// Rebuilding from folders is the fallback for an unreadable <c>index.json</c>. Without
/// this sidecar it had to guess, and the guess for the content hash was a per-folder
/// sentinel that no real capture could ever match — so every recovered clip was
/// permanently un-deduplicable and re-copying the same text added a row forever. Capture
/// time fell back to the folder's creation time, image dimensions were lost, and pins
/// were dropped outright.
///
/// Written once per capture and rewritten only when the pin state changes, so it costs
/// one small file per clip rather than a write on every paste. <c>LastUsedUtc</c> is
/// deliberately absent: it changes on every paste, and recovery ordering by capture time
/// is worth more than a write per paste to keep it exact.
///
/// It carries the clip's preview text, so it is encrypted exactly like a payload.
/// </remarks>
public sealed class ClipboardItemMetadata
{
    public int Version { get; set; } = 1;

    public string ContentHash { get; set; } = string.Empty;

    public ClipboardItemKind DominantKind { get; set; }

    public DateTimeOffset CapturedUtc { get; set; }

    public string PreviewText { get; set; } = string.Empty;

    public int CharacterCount { get; set; }

    public int? ImageWidth { get; set; }

    public int? ImageHeight { get; set; }

    public int FileCount { get; set; }

    public bool IsPinned { get; set; }
}
