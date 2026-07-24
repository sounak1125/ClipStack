namespace ClipStack.Core.Updates;

public static class AutomaticUpdateSchedule
{
    public static readonly TimeSpan BackgroundCheckInterval = TimeSpan.FromHours(24);

    public static bool ShouldCheck(
        DateTimeOffset? lastCheckUtc,
        DateTimeOffset nowUtc,
        bool isLaunchCheck)
    {
        return isLaunchCheck
               || lastCheckUtc is null
               || nowUtc - lastCheckUtc.Value >= BackgroundCheckInterval;
    }
}
