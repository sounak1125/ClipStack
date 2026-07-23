namespace ClipStack.Core.Models;

public sealed class ClipboardIndex
{
    public int Version { get; set; } = 1;

    public List<ClipboardItem> Items { get; set; } = [];
}
