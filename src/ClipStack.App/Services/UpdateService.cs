using ClipStack.Core;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;
using Velopack;
using Velopack.Sources;

namespace ClipStack.Services;

public sealed class UpdateService
{
    private readonly SettingsStore _settings;
    private readonly ReleaseConfigStore _releaseStore;
    private readonly FileLogger _logger;
    private ReleaseConfiguration _release;
    private UpdateManager? _manager;
    private UpdateInfo? _pending;
    private string _status = "Idle";
    private int _checkRunning;

    public event Action? StatusChanged;
    public event Action<string>? UpdateReady;

    public UpdateService(SettingsStore settings, ReleaseConfigStore releaseStore, FileLogger logger, ReleaseConfiguration release)
    {
        _settings = settings;
        _releaseStore = releaseStore;
        _logger = logger;
        _release = release;
        RebuildManager();
    }

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            StatusChanged?.Invoke();
        }
    }

    public string CurrentVersion
    {
        get
        {
            try
            {
                return _manager?.CurrentVersion?.ToString()
                       ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
                       ?? AppIdentity.DefaultVersion;
            }
            catch
            {
                return AppIdentity.DefaultVersion;
            }
        }
    }

    public bool IsFeedConfigured => _release.IsConfigured;

    public string FeedStatus => _release.IsConfigured ? "Update feed configured." : "Update feed not configured.";

    public void ReloadConfiguration()
    {
        _release = _releaseStore.Load();
        RebuildManager();
        StatusChanged?.Invoke();
    }

    private void RebuildManager()
    {
        _manager = null;
        if (!_release.IsConfigured)
            return;

        try
        {
            var feedUrl = _release.FeedUrl.Trim();
            IUpdateSource source = IsGitHubRepositoryUrl(feedUrl)
                ? new GithubSource(feedUrl, accessToken: null, prerelease: false)
                : new SimpleWebSource(feedUrl);

            var options = new UpdateOptions();
            if (!string.IsNullOrWhiteSpace(_release.Channel))
                options.ExplicitChannel = _release.Channel.Trim();

            _manager = new UpdateManager(source, options);
        }
        catch (Exception ex)
        {
            _logger.Error("UpdateManagerCreate", ex);
            Status = "Updater unavailable.";
        }
    }

    public async Task RunAutomaticChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckAutomaticallyIfDueAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("AutomaticUpdateCheck", ex);
        }
    }

    public async Task CheckAutomaticallyIfDueAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (!settings.CheckForUpdatesAutomatically || !_release.AutomaticChecks || !_release.IsConfigured)
            return;

        if (settings.LastAutomaticUpdateCheckUtc is { } last
            && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
        {
            return;
        }

        await CheckForUpdatesAsync(manual: false, cancellationToken).ConfigureAwait(false);
        _settings.Update(s => s.LastAutomaticUpdateCheckUtc = DateTimeOffset.UtcNow);
    }

    public async Task CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!_release.IsConfigured)
        {
            Status = "Update feed not configured.";
            return;
        }

        if (_manager is null)
        {
            Status = "Updater unavailable (development / unpackaged).";
            return;
        }

        if (Interlocked.Exchange(ref _checkRunning, 1) != 0)
        {
            if (manual)
                Status = "An update check is already running.";
            return;
        }

        try
        {
            Status = "Checking for updates…";
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            if (update is null)
            {
                Status = "You're up to date.";
                return;
            }

            Status = $"Update {update.TargetFullRelease.Version} available — downloading…";
            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(true);
            _pending = update;
            Status = $"Update {update.TargetFullRelease.Version} ready.";
            UpdateReady?.Invoke(update.TargetFullRelease.Version.ToString());
        }
        catch (NotSupportedException ex)
        {
            _logger.Error("UpdateCheckNotInstalled", ex);
            Status = "Updater unavailable (development / unpackaged).";
        }
        catch (Exception ex)
        {
            _logger.Error("UpdateCheck", ex);
            Status = manual ? "Update check failed." : "Update check failed (ignored).";
        }
        finally
        {
            Interlocked.Exchange(ref _checkRunning, 0);
        }
    }

    public void ApplyUpdateAndRestart()
    {
        try
        {
            if (_manager is null || _pending is null)
                return;
            _manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception ex)
        {
            _logger.Error("ApplyUpdate", ex);
            Status = "Could not apply update.";
        }
    }

    private static bool IsGitHubRepositoryUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }
}
