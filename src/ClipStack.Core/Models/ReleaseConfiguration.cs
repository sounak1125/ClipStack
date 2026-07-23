namespace ClipStack.Core.Models;

public sealed class ReleaseConfiguration
{
    public string FeedUrl { get; set; } = string.Empty;

    public string Channel { get; set; } = "stable";

    public bool AutomaticChecks { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(FeedUrl);
}
