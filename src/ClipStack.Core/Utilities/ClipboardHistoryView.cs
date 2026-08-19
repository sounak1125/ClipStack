using ClipStack.Core.Models;

namespace ClipStack.Core.Utilities;

/// <summary>
/// Selects the clips a history limit leaves visible.
/// </summary>
/// <remarks>
/// This is the read side of the eviction rule in <c>HistoryStore.EvictOverflow_NoLock</c>:
/// the limit counts unpinned clips only, so a stored history legitimately holds more than
/// the limit by however many clips are pinned. Truncating that list to the limit instead
/// drops the newest unpinned clips — pinned clips sort first, so pinning N clips hid the
/// N oldest unpinned ones, and pinning a full limit's worth hid every unpinned clip
/// including the one just copied.
///
/// It lives here rather than in the popup so the two halves of one rule sit in one
/// assembly and stay testable together.
/// </remarks>
public static class ClipboardHistoryView
{
    public static List<ClipboardItem> ApplyLimit(IReadOnlyList<ClipboardItem> items, int historyLimit)
    {
        if (historyLimit < 1) historyLimit = 1;

        var visible = new List<ClipboardItem>(items.Count);
        var unpinned = 0;

        foreach (var item in items)
        {
            if (item.IsPinned)
            {
                visible.Add(item);
                continue;
            }

            if (unpinned >= historyLimit)
                continue;

            unpinned++;
            visible.Add(item);
        }

        return visible;
    }
}
