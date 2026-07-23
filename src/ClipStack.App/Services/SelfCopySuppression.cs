using ClipStack.Core.Utilities;

namespace ClipStack.Services;

internal sealed class SelfCopySuppression
{
    private readonly object _gate = new();
    private string? _hash;
    private DateTimeOffset _expiresUtc;
    private int _remainingMatches = 2;

    public void Arm(string contentHash, TimeSpan duration)
    {
        lock (_gate)
        {
            _hash = contentHash;
            _expiresUtc = DateTimeOffset.UtcNow + duration;
            _remainingMatches = 2;
        }
    }

    public bool ShouldIgnore(string contentHash)
    {
        lock (_gate)
        {
            if (_hash is null)
                return false;

            if (DateTimeOffset.UtcNow > _expiresUtc)
            {
                Clear_NoLock();
                return false;
            }

            if (!string.Equals(_hash, contentHash, StringComparison.OrdinalIgnoreCase))
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
        _hash = null;
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
