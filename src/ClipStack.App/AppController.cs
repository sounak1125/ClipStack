using System.Windows;
using ClipStack.Core;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;
using ClipStack.Interop;
using ClipStack.Services;
using ClipStack.ViewModels;
using ClipStack.Views;
using Microsoft.Win32;

namespace ClipStack;

internal sealed class AppController : IDisposable
{
    private readonly FileLogger _logger;
    private readonly StoragePaths _paths;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly ReleaseConfigStore _releaseStore;
    private readonly NativeMessageWindow _native;
    private readonly SelfCopySuppression _suppression;
    private readonly ThumbnailService _thumbnails;
    private readonly ClipboardFormatReader _formatReader;
    private readonly ClipboardCaptureService _capture;
    private readonly ClipboardRestoreService _restore;
    private readonly ForegroundWindowService _foreground;
    private readonly AutoPasteService _autoPaste;
    private readonly PopupPositionService _position;
    private readonly StartupService _startup;
    private readonly UpdateService _updates;
    private readonly TrayIconService _tray;
    private readonly PopupViewModel _popupVm;
    private readonly ClipboardPopupWindow _popup;
    private readonly SingleInstanceService _singleInstance;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private SettingsWindow? _settingsWindow;
    private UpdateNotificationWindow? _updateNotification;
    private bool _shuttingDown;
    private bool _disposed;

    public AppController(
        FileLogger logger,
        StoragePaths paths,
        SettingsStore settings,
        HistoryStore history,
        ReleaseConfigStore releaseStore,
        ReleaseConfiguration release,
        SingleInstanceService singleInstance)
    {
        _logger = logger;
        _paths = paths;
        _settings = settings;
        _history = history;
        _releaseStore = releaseStore;
        _singleInstance = singleInstance;

        _suppression = new SelfCopySuppression();
        _thumbnails = new ThumbnailService(logger);
        _formatReader = new ClipboardFormatReader(logger, _thumbnails);
        _foreground = new ForegroundWindowService();
        _autoPaste = new AutoPasteService(logger);
        _position = new PopupPositionService();
        _startup = new StartupService(logger);
        _updates = new UpdateService(settings, releaseStore, logger, release);
        _native = new NativeMessageWindow(logger);
        _restore = new ClipboardRestoreService(history, _suppression, logger);
        _capture = new ClipboardCaptureService(_native, _formatReader, history, settings, _suppression, logger);
        _tray = new TrayIconService(settings, _startup, logger);
        _popupVm = new PopupViewModel(history, logger);
        _popup = new ClipboardPopupWindow { DataContext = _popupVm };
    }

    public void Start()
    {
        ApplyTheme(_settings.Current.Theme);

        _native.Create();
        _native.StartClipboardListener();

        var settings = _settings.Current;
        if (!_native.TryRegisterHotKey(settings.HotKey))
        {
            _tray.ShowBalloon("The default shortcut is unavailable. Choose another in Settings.");
            ShowSettings(forceHotKeyAttention: true);
        }

        WireEvents();
        _capture.Start();

        if (StartupService.IsInstalledBuild())
        {
            if (!_startup.IsEnabled() && settings.StartWithWindows)
                _startup.SetEnabled(true);
            _startup.RefreshInstalledPathIfNeeded();
        }

        _tray.RefreshMenuState();
        _ = _updates.RunAutomaticChecksAsync(_lifetimeCts.Token);
    }

    private void WireEvents()
    {
        _native.HotKeyPressed += (_, _) => Application.Current.Dispatcher.Invoke(ShowPopup);
        _singleInstance.ShowHistoryRequested += () => Application.Current.Dispatcher.Invoke(ShowPopup);
        _capture.HistoryChanged += () => Application.Current.Dispatcher.Invoke(OnHistoryChanged);
        _capture.NotifyUser += msg => Application.Current.Dispatcher.Invoke(() => _tray.ShowBalloon(msg));

        _tray.ShowHistoryRequested += () => Application.Current.Dispatcher.Invoke(ShowPopup);
        _tray.SettingsRequested += () => Application.Current.Dispatcher.Invoke(() => ShowSettings());
        _tray.ClearHistoryRequested += () => Application.Current.Dispatcher.Invoke(ClearHistory);
        _tray.CheckUpdatesRequested += () => Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await _updates.CheckForUpdatesAsync(manual: true);
            _tray.ShowBalloon(_updates.Status);
        });
        _tray.ExitRequested += () => Application.Current.Dispatcher.Invoke(Shutdown);
        _tray.PauseChanged += _ => Application.Current.Dispatcher.Invoke(OnSettingsChanged);
        _tray.StartupChanged += _ => { };

        _popup.PasteRequested += () => _ = PasteSelectedAsync();
        _popup.DeleteRequested += DeleteSelected;
        _popup.SettingsRequested += () => ShowSettings();
        _popup.ClearRequested += ClearHistory;
        _popup.CloseRequested += HidePopup;

        _updates.UpdateReady += version =>
            Application.Current.Dispatcher.Invoke(() => ShowUpdateNotification(version));

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
            Application.Current.Dispatcher.Invoke(() => ApplyTheme(_settings.Current.Theme));
    }

    private void OnHistoryChanged()
    {
        if (_popup.IsVisible)
            RefreshPopup();
    }

    private void OnSettingsChanged()
    {
        var settings = _settings.Current;
        _history.TrimToLimit(settings.HistoryLimit);
        _tray.RefreshMenuState();
        if (_popup.IsVisible)
            RefreshPopup();
        if (settings.CheckForUpdatesAutomatically)
            _ = _updates.CheckAutomaticallyIfDueAsync(_lifetimeCts.Token);
    }

    public void ShowPopup()
    {
        if (_shuttingDown) return;
        if (_popup.IsVisible)
        {
            _popup.Activate();
            _popup.Focus();
            return;
        }

        _foreground.Capture();
        RefreshPopup();

        _popup.Width = 408;
        _popup.Height = 360;
        _popup.Topmost = true;
        _popup.Opacity = 0;
        _popup.Show();
        _popup.UpdateLayout();
        _position.PositionNearCursor(_popup);
        _popup.Opacity = 1;
        _popup.Activate();
        _popup.Focus();
    }

    private void RefreshPopup()
    {
        var s = _settings.Current;
        _popupVm.Refresh(s.HistoryLimit, s.PauseCapture);
    }

    private void HidePopup()
    {
        if (!_popup.IsVisible) return;
        _popup.Hide();
        _popup.Topmost = false;
        _popupVm.ClearThumbnails();
    }

    private async Task PasteSelectedAsync()
    {
        var selected = _popupVm.SelectedItem;
        if (selected is null) return;

        var settings = _settings.Current;
        var result = await _restore.RestoreAsync(selected.Item).ConfigureAwait(true);
        if (!result.Success)
        {
            _tray.ShowBalloon(result.Message ?? "Restore failed.");
            return;
        }

        HidePopup();

        if (!settings.AutoPaste)
        {
            _tray.ShowBalloon("Copied to clipboard.");
            return;
        }

        var focusOk = await _foreground.TryRestoreAsync(_lifetimeCts.Token).ConfigureAwait(true);
        var pasteOk = focusOk && _autoPaste.TrySendCtrlV();
        if (!pasteOk)
            _tray.ShowBalloon("Copied to clipboard — press Ctrl+V to paste.");
    }

    private void DeleteSelected()
    {
        var selected = _popupVm.SelectedItem;
        if (selected is null) return;
        _history.DeleteItem(selected.Id);
        RefreshPopup();
    }

    private void ClearHistory()
    {
        _history.ClearAll();
        RefreshPopup();
        _tray.ShowBalloon("History cleared.");
    }

    private void ShowUpdateNotification(string version)
    {
        if (_updateNotification is { IsVisible: true })
        {
            _updateNotification.Activate();
            return;
        }

        _updateNotification = new UpdateNotificationWindow(version);
        _updateNotification.RestartRequested += () =>
        {
            _updateNotification?.Close();
            _updates.ApplyUpdateAndRestart();
        };
        _updateNotification.DismissRequested += () => _updateNotification?.Close();
        _updateNotification.Closed += (_, _) => _updateNotification = null;
        _updateNotification.Show();
    }

    private void ShowSettings(bool forceHotKeyAttention = false)
    {
        _popup.BeginSuppressDeactivate();
        HidePopup();

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            _popup.EndSuppressDeactivate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settings,
            _history,
            _startup,
            _updates,
            TryChangeHotKey,
            OnSettingsChanged,
            ClearHistory,
            ApplyTheme);

        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _popup.EndSuppressDeactivate();
            _tray.RefreshMenuState();
        };

        _settingsWindow.Show();
        _settingsWindow.Activate();

        if (forceHotKeyAttention)
        {
            // Settings already open for hotkey conflict.
        }
    }

    private bool TryChangeHotKey(HotKeyConfiguration config)
    {
        var previous = _native.RegisteredHotKey ?? _settings.Current.HotKey.Clone();
        if (_native.TryRegisterHotKey(config))
            return true;

        _native.TryRegisterHotKey(previous);
        return false;
    }

    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        try { _lifetimeCts.Cancel(); } catch { }

        _capture.StopAccepting();
        HidePopup();
        try { _updateNotification?.Close(); } catch { }
        try { _settingsWindow?.Close(); } catch { }

        _native.UnregisterHotKeyInternal();
        _capture.DisposeSubscriptions();
        _native.Dispose();
        _tray.Dispose();

        try
        {
            if (_settings.Current.ClearHistoryOnExit)
                _history.ClearAll();
        }
        catch (Exception ex)
        {
            _logger.Error("ClearOnExit", ex);
        }

        _singleInstance.Dispose();
        _logger.Dispose();

        Application.Current.Shutdown();
    }

    private void ApplyTheme(string theme)
    {
        var normalized = AppSettings.NormalizeTheme(theme);
        var source = normalized switch
        {
            "Light" => "Themes/Light.xaml",
            "Dim" => "Themes/Dim.xaml",
            "Contrast" => "Themes/Contrast.xaml",
            _ => "Themes/Dark.xaml",
        };

        var dict = new ResourceDictionary
        {
            Source = new Uri(source, UriKind.Relative),
        };

        var app = Application.Current;
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(dict);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
        if (!_shuttingDown)
            Shutdown();
    }
}
