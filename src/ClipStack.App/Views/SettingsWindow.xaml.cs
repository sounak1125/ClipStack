using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ClipStack.Core;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Services;

namespace ClipStack.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly HistoryStore _historyStore;
    private readonly StartupService _startupService;
    private readonly UpdateService _updateService;
    private readonly Func<HotKeyConfiguration, bool> _tryRegisterHotKey;
    private readonly Action _onSettingsChanged;
    private readonly Action _onClearHistory;
    private HotKeyConfiguration _draftHotKey;

    public SettingsWindow(
        SettingsStore settingsStore,
        HistoryStore historyStore,
        StartupService startupService,
        UpdateService updateService,
        Func<HotKeyConfiguration, bool> tryRegisterHotKey,
        Action onSettingsChanged,
        Action onClearHistory)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _historyStore = historyStore;
        _startupService = startupService;
        _updateService = updateService;
        _tryRegisterHotKey = tryRegisterHotKey;
        _onSettingsChanged = onSettingsChanged;
        _onClearHistory = onClearHistory;
        _draftHotKey = settingsStore.Current.HotKey.Clone();
        SettingsNavigation.SelectionChanged += OnSettingsNavigationChanged;
        SettingsNavigation.SelectedIndex = 0;
        LoadFromSettings();
        _updateService.StatusChanged += () => Dispatcher.Invoke(RefreshUpdateUi);
    }

    private void LoadFromSettings()
    {
        var s = _settingsStore.Current;
        StartWithWindows.IsChecked = _startupService.IsEnabled();
        AutoPaste.IsChecked = s.AutoPaste;
        ShowNotifications.IsChecked = s.ShowTrayNotifications;
        ClearOnExit.IsChecked = s.ClearHistoryOnExit;
        HistoryLimit.Text = s.HistoryLimit.ToString();
        MaxSizeMb.Text = s.MaxItemSizeBytes <= 0 ? "0" : (s.MaxItemSizeBytes / (1024.0 * 1024.0)).ToString("0.##");
        CaptureText.IsChecked = s.CaptureText;
        CaptureRichText.IsChecked = s.CaptureRichText;
        CaptureImages.IsChecked = s.CaptureImages;
        CaptureFiles.IsChecked = s.CaptureFiles;
        CaptureEnabled.IsChecked = !s.PauseCapture;
        AutoUpdates.IsChecked = s.CheckForUpdatesAutomatically;
        _draftHotKey = s.HotKey.Clone();
        CurrentShortcut.Text = $"Current: {_draftHotKey.ToDisplayString()}";
        ShortcutRecorder.Text = _draftHotKey.ToDisplayString();
        ShortcutError.Text = string.Empty;
        DataFolderText.Text = AppIdentity.GetDataDirectory();
        DiskUsageText.Text = $"History disk usage: {FormatBytes(_historyStore.CalculateDiskUsageBytes())}";
        SidebarVersionText.Text = $"ClipStack {_updateService.CurrentVersion}";
        RefreshUpdateUi();
    }

    private void RefreshUpdateUi()
    {
        VersionText.Text = $"Current version: {_updateService.CurrentVersion}";
        FeedStatusText.Text = _updateService.FeedStatus;
        UpdateStatusText.Text = $"Status: {_updateService.Status}";
    }

    private void OnShortcutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        var draft = new HotKeyConfiguration
        {
            Control = mods.HasFlag(ModifierKeys.Control),
            Alt = mods.HasFlag(ModifierKeys.Alt),
            Shift = mods.HasFlag(ModifierKeys.Shift),
            Win = mods.HasFlag(ModifierKeys.Windows),
            VirtualKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key),
        };

        if (!draft.IsValid)
        {
            ShortcutError.Text = "Shortcut must include at least one modifier and a key.";
            return;
        }

        _draftHotKey = draft;
        ShortcutRecorder.Text = draft.ToDisplayString();
        ShortcutError.Text = string.Empty;
    }

    private void OnApplyShortcut(object sender, RoutedEventArgs e)
    {
        if (!_draftHotKey.IsValid)
        {
            ShortcutError.Text = "Invalid shortcut.";
            return;
        }

        var previous = _settingsStore.Current.HotKey.Clone();
        if (!_tryRegisterHotKey(_draftHotKey))
        {
            _tryRegisterHotKey(previous);
            ShortcutError.Text = "That shortcut is already in use. Previous shortcut restored.";
            _draftHotKey = previous;
            ShortcutRecorder.Text = previous.ToDisplayString();
            CurrentShortcut.Text = $"Current: {previous.ToDisplayString()}";
            return;
        }

        _settingsStore.Update(s => s.HotKey = _draftHotKey.Clone());
        CurrentShortcut.Text = $"Current: {_draftHotKey.ToDisplayString()}";
        ShortcutError.Text = string.Empty;
        _onSettingsChanged();
    }

    private void OnRestoreDefaultShortcut(object sender, RoutedEventArgs e)
    {
        _draftHotKey = HotKeyConfiguration.Default.Clone();
        ShortcutRecorder.Text = _draftHotKey.ToDisplayString();
        OnApplyShortcut(sender, e);
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        await _updateService.CheckForUpdatesAsync(manual: true);
        RefreshUpdateUi();
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        var dir = AppIdentity.GetDataDirectory();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = Quote(dir),
            UseShellExecute = true,
        });
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "Clear all clipboard history?", AppIdentity.ProductName,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            _onClearHistory();
        DiskUsageText.Text = $"History disk usage: {FormatBytes(_historyStore.CalculateDiskUsageBytes())}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HistoryLimit.Text, out var limit))
            limit = AppSettings.DefaultHistoryLimit;
        if (!double.TryParse(MaxSizeMb.Text, out var mb))
            mb = AppSettings.DefaultMaxItemSizeBytes / (1024.0 * 1024.0);

        var wantStartup = StartWithWindows.IsChecked == true;
        _startupService.SetEnabled(wantStartup);

        _settingsStore.Update(s =>
        {
            s.StartWithWindows = wantStartup;
            s.AutoPaste = AutoPaste.IsChecked == true;
            s.ShowTrayNotifications = ShowNotifications.IsChecked == true;
            s.ClearHistoryOnExit = ClearOnExit.IsChecked == true;
            s.HistoryLimit = limit;
            s.MaxItemSizeBytes = mb <= 0 ? 0 : (long)(mb * 1024 * 1024);
            s.CaptureText = CaptureText.IsChecked == true;
            s.CaptureRichText = CaptureRichText.IsChecked == true;
            s.CaptureImages = CaptureImages.IsChecked == true;
            s.CaptureFiles = CaptureFiles.IsChecked == true;
            s.PauseCapture = CaptureEnabled.IsChecked != true;
            s.CheckForUpdatesAutomatically = AutoUpdates.IsChecked == true;
        });

        _onSettingsChanged();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSettingsNavigationChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GeneralPage is null)
            return;

        var pages = new[]
        {
            GeneralPage,
            CapturePage,
            ShortcutPage,
            UpdatesPage,
            StoragePage,
            PrivacyPage,
        };

        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = i == SettingsNavigation.SelectedIndex
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (i == SettingsNavigation.SelectedIndex)
                pages[i].ScrollToTop();
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
