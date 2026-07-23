using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ClipStack.Core;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;

namespace ClipStack.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private readonly SettingsStore _settings;
    private readonly StartupService _startup;
    private readonly FileLogger _logger;
    private ToolStripMenuItem? _pauseItem;
    private ToolStripMenuItem? _startupItem;
    private bool _disposed;

    public event Action? ShowHistoryRequested;
    public event Action? SettingsRequested;
    public event Action? ClearHistoryRequested;
    public event Action? CheckUpdatesRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PauseChanged;
    public event Action<bool>? StartupChanged;

    public TrayIconService(SettingsStore settings, StartupService startup, FileLogger logger)
    {
        _settings = settings;
        _startup = startup;
        _logger = logger;
        _appIcon = LoadApplicationIcon();

        _notifyIcon = new NotifyIcon
        {
            Text = AppIdentity.ProductName,
            Visible = true,
            Icon = _appIcon,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowHistoryRequested?.Invoke();
        _notifyIcon.ContextMenuStrip = BuildMenu();
        RefreshMenuState();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var show = new ToolStripMenuItem("Show Clipboard History");
        show.Click += (_, _) => ShowHistoryRequested?.Invoke();
        menu.Items.Add(show);

        _pauseItem = new ToolStripMenuItem("Pause Clipboard Capture");
        _pauseItem.Click += (_, _) =>
        {
            var settings = _settings.Current;
            var next = !settings.PauseCapture;
            _settings.Update(s => s.PauseCapture = next);
            PauseChanged?.Invoke(next);
            RefreshMenuState();
        };
        menu.Items.Add(_pauseItem);

        var clear = new ToolStripMenuItem("Clear History");
        clear.Click += (_, _) => ClearHistoryRequested?.Invoke();
        menu.Items.Add(clear);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);

        var updates = new ToolStripMenuItem("Check for Updates");
        updates.Click += (_, _) => CheckUpdatesRequested?.Invoke();
        menu.Items.Add(updates);

        _startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _startupItem.CheckedChanged += (_, _) =>
        {
            if (_startupItem is null) return;
            var ok = _startup.SetEnabled(_startupItem.Checked);
            if (!ok)
            {
                _startupItem.Checked = _startup.IsEnabled();
                ShowBalloon("Could not update startup setting.");
            }
            else
            {
                _settings.Update(s => s.StartWithWindows = _startupItem.Checked);
                StartupChanged?.Invoke(_startupItem.Checked);
            }
        };
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem($"Exit {AppIdentity.ProductName}");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        return menu;
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            {
                using var associated = Icon.ExtractAssociatedIcon(executable);
                if (associated is not null)
                    return (Icon)associated.Clone();
            }
        }
        catch
        {
            // Fall through to a cloned system icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void RefreshMenuState()
    {
        try
        {
            var settings = _settings.Current;
            if (_pauseItem is not null)
                _pauseItem.Text = settings.PauseCapture ? "Resume Clipboard Capture" : "Pause Clipboard Capture";

            if (_startupItem is not null)
            {
                var enabled = _startup.IsEnabled();
                if (_startupItem.Checked != enabled)
                    _startupItem.Checked = enabled;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("TrayRefresh", ex);
        }
    }

    public void ShowBalloon(string message, string? title = null)
    {
        try
        {
            if (!_settings.Current.ShowTrayNotifications)
                return;

            _notifyIcon.BalloonTipTitle = title ?? AppIdentity.ProductName;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch (Exception ex)
        {
            _logger.Error("ShowBalloon", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _appIcon.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Error("TrayDispose", ex);
        }
    }
}
