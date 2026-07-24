using ClipStack.Core;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;
using Velopack;
using Velopack.Exceptions;
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

    public string FeedStatus => _release.IsConfigured ? "Feed configured" : "No feed";

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
            Status = "Unavailable";
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

        // Don't burn the 24h cooldown while running unpackaged (dev/publish folder).
        if (_manager is null || !_manager.IsInstalled)
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
            Status = "No feed";
            return;
        }

        if (_manager is null)
        {
            Status = "Unavailable";
            return;
        }

        // Velopack only supports update checks from a real install (Setup/portable pack).
        // Unpackaged debug/publish runs throw NotInstalledException — not a feed failure.
        if (!_manager.IsInstalled)
        {
            Status = "Not installed";
            return;
        }

        if (Interlocked.Exchange(ref _checkRunning, 1) != 0)
        {
            if (manual)
                Status = "Busy";
            return;
        }

        try
        {
            Status = "Checking…";
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            if (update is null)
            {
                Status = "Up to date";
                return;
            }

            Status = $"Downloading {update.TargetFullRelease.Version}…";
            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(true);
            _pending = update;
            Status = $"{update.TargetFullRelease.Version} ready";
            UpdateReady?.Invoke(update.TargetFullRelease.Version.ToString());
        }
        catch (NotInstalledException ex)
        {
            _logger.Warn("UpdateCheckNotInstalled", ex.Message);
            Status = "Not installed";
        }
        catch (NotSupportedException ex)
        {
            _logger.Warn("UpdateCheckNotSupported", ex.Message);
            Status = "Unavailable";
        }
        catch (Exception ex)
        {
            _logger.Error("UpdateCheck", ex);
            Status = "Check failed";
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
            Status = "Apply failed";
        }
    }

    private static bool IsGitHubRepositoryUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }
}
