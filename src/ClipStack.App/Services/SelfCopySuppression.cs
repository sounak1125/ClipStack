using ClipStack.Core.Utilities;

namespace ClipStack.Services;

internal sealed class SelfCopySuppression
{
    private readonly object _gate = new();
    private string[] _hashes = [];
    private DateTimeOffset _expiresUtc;
    private int _remainingMatches;

    /// <summary>
    /// Arms suppression for every hash the clip just written to the clipboard can be
    /// captured back as.
    /// </summary>
    /// <remarks>
    /// More than one is needed because a plain-text paste publishes fewer formats than
    /// were captured, so the clip coming back does not hash to the stored clip's hash.
    /// Matching the stored hash alone let every Shift+Enter on a styled clip add a
    /// duplicate plain-text row to the history.
    /// </remarks>
    public void Arm(IReadOnlyCollection<string> contentHashes, TimeSpan duration)
    {
        lock (_gate)
        {
            _hashes = contentHashes.Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _expiresUtc = DateTimeOffset.UtcNow + duration;
            _remainingMatches = _hashes.Length == 0 ? 0 : 2;
        }
    }

    public bool ShouldIgnore(string contentHash)
    {
        lock (_gate)
        {
            if (_hashes.Length == 0)
                return false;

            if (DateTimeOffset.UtcNow > _expiresUtc)
            {
                Clear_NoLock();
                return false;
            }

            if (!_hashes.Contains(contentHash, StringComparer.OrdinalIgnoreCase))
                return false;

            _remainingMatches--;
            if (_remainingMatches <= 0)
                Clear_NoLock();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) Clear_NoLock();
    }

    private void Clear_NoLock()
    {
        _hashes = [];
        _remainingMatches = 0;
    }
}

internal sealed class NotificationCooldown
{
    private readonly object _gate = new();
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public bool TryAcquire(TimeSpan cooldown)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextAllowed)
                return false;
            _nextAllowed = now + cooldown;
            return true;
        }
    }
}
