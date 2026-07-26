using System.Text.Json.Serialization;

namespace ClipStack.Core.Models;

public sealed class ClipboardPayload
{
    /// <summary>
    /// Whether the file on disk is DPAPI-protected. Defaults to false so payloads written
    /// before encryption existed deserialize correctly. Reads dispatch on the file's own
    /// header rather than this flag; it exists for reporting and diagnostics.
    /// </summary>
    public bool Encrypted { get; set; }

    public ClipboardFormatKind Format { get; set; }

    /// <summary>Relative path under the item folder (validated on load).</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [JsonIgnore]
    public string? AbsolutePath { get; set; }
}
