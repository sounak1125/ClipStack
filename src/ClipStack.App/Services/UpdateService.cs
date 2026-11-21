using ClipStack.Core;
using ClipStack.Core.Models;
using ClipStack.Core.Storage;
using ClipStack.Core.Updates;
using ClipStack.Core.Utilities;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace ClipStack.Services;

public sealed class UpdateService
{
    private readonly SettingsStore _settings;
    private readonly FileLogger _logger;
    private readonly ReleaseConfiguration _release;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;
    private string _status = "Idle";
    private int _checkRunning;

    public event Action? StatusChanged;
    public event Action<string>? UpdateReady;

    public UpdateService(SettingsStore settings, FileLogger logger, ReleaseConfiguration release)
    {
        _settings = settings;
        _logger = logger;
        _release = release;
        _manager = CreateManager();
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

    public string FeedStatus => !_release.IsConfigured ? "No feed" : _manager is null ? "Feed unavailable" : "Feed configured";

    private UpdateManager? CreateManager()
    {
        if (!_release.IsConfigured)
            return null;

        if (!Uri.TryCreate(_release.FeedUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0)
        {
            _logger.Warn("UpdateFeed", "The update feed must be an HTTPS URL without embedded credentials.");
            Status = "Unavailable";
            return null;
        }

        try
        {
            IUpdateSource source = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(uri.AbsoluteUri, accessToken: null, prerelease: false)
                : new SimpleWebSource(uri.AbsoluteUri);

            var options = new UpdateOptions();
            if (!string.IsNullOrWhiteSpace(_release.Channel))
                options.ExplicitChannel = _release.Channel.Trim();

            return new UpdateManager(source, options);
        }
        catch (Exception ex)
        {
            _logger.Error("UpdateManagerCreate", ex);
            Status = "Unavailable";
            return null;
        }
    }

    public async Task RunAutomaticChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            await CheckAutomaticallyAsync(isLaunchCheck: true, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
                await CheckAutomaticallyIfDueAsync(cancellationToken).ConfigureAwait(false);
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
        await CheckAutomaticallyAsync(isLaunchCheck: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckAutomaticallyAsync(bool isLaunchCheck, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!settings.CheckForUpdatesAutomatically || !_release.AutomaticChecks || !_release.IsConfigured)
            return;

        // Don't burn the 24h cooldown while running unpackaged (dev/publish folder).
        if (_manager is null || !_manager.IsInstalled)
            return;

        var now = DateTimeOffset.UtcNow;
        if (!AutomaticUpdateSchedule.ShouldCheck(settings.LastAutomaticUpdateCheckUtc, now, isLaunchCheck))
            return;

        var completed = await CheckForUpdatesAsync(manual: false, cancellationToken).ConfigureAwait(false);
        if (completed)
            _settings.Update(s => s.LastAutomaticUpdateCheckUtc = now);
    }

    public async Task<bool> CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!_release.IsConfigured)
        {
            Status = "No feed";
            return false;
        }

        if (_manager is null)
        {
            Status = "Unavailable";
            return false;
        }

        // Velopack only supports update checks from a real install (Setup/portable pack).
        // Unpackaged debug/publish runs throw NotInstalledException — not a feed failure.
        if (!_manager.IsInstalled)
        {
            Status = "Not installed";
            return false;
        }

        if (Interlocked.Exchange(ref _checkRunning, 1) != 0)
        {
            if (manual)
                Status = "Busy";
            return false;
        }

        try
        {
            Status = "Checking…";
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            if (update is null)
            {
                Status = "Up to date";
                return true;
            }

            Status = $"Downloading {update.TargetFullRelease.Version}…";
            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(true);
            _pending = update;
            Status = $"{update.TargetFullRelease.Version} ready";
            UpdateReady?.Invoke(update.TargetFullRelease.Version.ToString());
            return true;
        }
        catch (NotInstalledException ex)
        {
            _logger.Warn("UpdateCheckNotInstalled", ex.Message);
            Status = "Not installed";
            return false;
        }
        catch (NotSupportedException ex)
        {
            _logger.Warn("UpdateCheckNotSupported", ex.Message);
            Status = "Unavailable";
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("UpdateCheck", ex);
            Status = "Check failed";
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _checkRunning, 0);
        }
    }

    /// <summary>
    /// Applies the downloaded update and restarts. Returns only if that did not happen.
    /// </summary>
    /// <remarks>
    /// On success this never returns: Velopack spawns Update.exe with <c>--waitPid</c> for
    /// this process and then calls <c>Environment.Exit(0)</c>. Callers must therefore do
    /// any cleanup <i>before</i> calling, and must be able to undo it when this returns
    /// false — at that point the app is still very much alive.
    /// </remarks>
    public bool ApplyUpdateAndRestart()
    {
        try
        {
            if (_manager is null || _pending is null)
                return false;

            _manager.ApplyUpdatesAndRestart(_pending);

            // Not reached in practice; the call above ends the process.
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("ApplyUpdate", ex);
            Status = "Apply failed";
            return false;
        }
    }

}
