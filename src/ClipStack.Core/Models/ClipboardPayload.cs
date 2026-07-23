using System.Text.Json.Serialization;

namespace ClipStack.Core.Models;

public sealed class ClipboardPayload
{
    public ClipboardFormatKind Format { get; set; }

    /// <summary>Relative path under the item folder (validated on load).</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [JsonIgnore]
    public string? AbsolutePath { get; set; }
}
