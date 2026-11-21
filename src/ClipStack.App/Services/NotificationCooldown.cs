namespace ClipStack.Services;

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
