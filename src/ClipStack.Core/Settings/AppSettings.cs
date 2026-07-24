namespace ClipStack.Core.Settings;

public sealed class AppSettings
{
    public const int MinHistoryLimit = 1;
    public const int MaxHistoryLimit = 50;
    public const int DefaultHistoryLimit = 50;
    public const long DefaultMaxItemSizeBytes = 200L * 1024 * 1024;

    public bool StartWithWindows { get; set; }

    public bool AutoPaste { get; set; } = true;

    public bool ShowTrayNotifications { get; set; } = true;

    public int HistoryLimit { get; set; } = DefaultHistoryLimit;

    /// <summary>0 means no explicit ClipStack size limit.</summary>
    public long MaxItemSizeBytes { get; set; } = DefaultMaxItemSizeBytes;

    public bool ClearHistoryOnExit { get; set; }

    public bool CaptureText { get; set; } = true;

    public bool CaptureRichText { get; set; } = true;

    public bool CaptureImages { get; set; } = true;

    public bool CaptureFiles { get; set; } = true;

    public bool PauseCapture { get; set; }

    public Models.HotKeyConfiguration HotKey { get; set; } = Models.HotKeyConfiguration.Default.Clone();

    public bool CheckForUpdatesAutomatically { get; set; } = true;

    public DateTimeOffset? LastAutomaticUpdateCheckUtc { get; set; }

    public string Theme { get; set; } = "Dark";

    public static readonly string[] AllowedThemes = ["Dark", "Light", "Dim", "Contrast"];

    public static bool IsAllowedTheme(string? theme) =>
        !string.IsNullOrWhiteSpace(theme)
        && AllowedThemes.Any(t => t.Equals(theme, StringComparison.OrdinalIgnoreCase));

    public static string NormalizeTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
            return "Dark";

        foreach (var allowed in AllowedThemes)
        {
            if (allowed.Equals(theme, StringComparison.OrdinalIgnoreCase))
                return allowed;
        }

        return "Dark";
    }

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        AutoPaste = AutoPaste,
        ShowTrayNotifications = ShowTrayNotifications,
        HistoryLimit = HistoryLimit,
        MaxItemSizeBytes = MaxItemSizeBytes,
        ClearHistoryOnExit = ClearHistoryOnExit,
        CaptureText = CaptureText,
        CaptureRichText = CaptureRichText,
        CaptureImages = CaptureImages,
        CaptureFiles = CaptureFiles,
        PauseCapture = PauseCapture,
        HotKey = HotKey.Clone(),
        CheckForUpdatesAutomatically = CheckForUpdatesAutomatically,
        LastAutomaticUpdateCheckUtc = LastAutomaticUpdateCheckUtc,
        Theme = Theme,
    };

    public void ValidateAndClamp()
    {
        if (HistoryLimit < MinHistoryLimit) HistoryLimit = MinHistoryLimit;
        if (HistoryLimit > MaxHistoryLimit) HistoryLimit = MaxHistoryLimit;
        if (MaxItemSizeBytes < 0) MaxItemSizeBytes = 0;
        HotKey ??= Models.HotKeyConfiguration.Default.Clone();
        if (!HotKey.IsValid)
            HotKey = Models.HotKeyConfiguration.Default.Clone();
        Theme = NormalizeTheme(Theme);
    }
}
