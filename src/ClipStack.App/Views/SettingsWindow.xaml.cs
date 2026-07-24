using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipStack.Core;
using MediaBrush = System.Windows.Media.Brush;
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
    private readonly Action<string> _applyTheme;
    private HotKeyConfiguration _draftHotKey;
    private HotKeyConfiguration _shortcutBeforeRecording;
    private bool _isRecordingShortcut;
    private string _themeAtOpen = "Dark";
    private bool _themeSaved;
    private bool _loadingTheme;

    public SettingsWindow(
        SettingsStore settingsStore,
        HistoryStore historyStore,
        StartupService startupService,
        UpdateService updateService,
        Func<HotKeyConfiguration, bool> tryRegisterHotKey,
        Action onSettingsChanged,
        Action onClearHistory,
        Action<string> applyTheme)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _historyStore = historyStore;
        _startupService = startupService;
        _updateService = updateService;
        _tryRegisterHotKey = tryRegisterHotKey;
        _onSettingsChanged = onSettingsChanged;
        _onClearHistory = onClearHistory;
        _applyTheme = applyTheme;
        _draftHotKey = settingsStore.Current.HotKey.Clone();
        _shortcutBeforeRecording = _draftHotKey.Clone();
        SettingsNavigation.SelectionChanged += OnSettingsNavigationChanged;
        SettingsNavigation.SelectedIndex = 0;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Closed += OnWindowClosed;
        LoadFromSettings();
        _updateService.StatusChanged += () => Dispatcher.Invoke(RefreshUpdateUi);
    }

    private void LoadFromSettings()
    {
        var s = _settingsStore.Current;
        StartWithWindows.IsChecked = _startupService.IsEnabled();
        StartWithWindows.IsEnabled = _startupService.CanManageStartup;
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
        _themeAtOpen = AppSettings.NormalizeTheme(s.Theme);
        SelectThemeCombo(_themeAtOpen);
        _draftHotKey = s.HotKey.Clone();
        _isRecordingShortcut = false;
        RefreshShortcutDisplay();
        ShortcutError.Text = string.Empty;
        DataFolderText.Text = AppIdentity.GetDataDirectory();
        DiskUsageText.Text = FormatBytes(_historyStore.CalculateDiskUsageBytes());
        SidebarVersionText.Text = _updateService.CurrentVersion;
        RefreshUpdateUi();
    }

    private void SelectThemeCombo(string theme)
    {
        _loadingTheme = true;
        try
        {
            foreach (ComboBoxItem item in ThemeCombo.Items)
            {
                if (string.Equals(item.Tag as string, theme, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeCombo.SelectedItem = item;
                    return;
                }
            }

            ThemeCombo.SelectedIndex = 0;
        }
        finally
        {
            _loadingTheme = false;
        }
    }

    private string GetSelectedTheme()
    {
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            return AppSettings.NormalizeTheme(tag);
        return "Dark";
    }

    private void OnThemeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingTheme || ThemeCombo is null)
            return;

        _applyTheme(GetSelectedTheme());
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!_themeSaved)
            _applyTheme(_themeAtOpen);
    }

    private void RefreshUpdateUi()
    {
        VersionText.Text = _updateService.CurrentVersion;
        FeedStatusText.Text = _updateService.FeedStatus;
        UpdateStatusText.Text = _updateService.Status;
    }

    private void OnShortcutRecorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ShortcutRecorderHost.IsKeyboardFocused)
            ShortcutRecorderHost.Focus();
        e.Handled = true;
    }

    private void OnShortcutRecorderGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        => BeginShortcutRecording();

    private void OnShortcutRecorderLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_isRecordingShortcut)
            return;

        // Keep whatever draft was captured; only Esc restores the prior value.
        EndShortcutRecording(restorePrevious: false);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingShortcut)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Escape)
            return;

        CancelShortcutRecording();
        e.Handled = true;
    }

    private void OnShortcutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingShortcut)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Let Tab move focus out of the recorder (LostFocus ends recording).
        if (key == Key.Tab)
            return;

        e.Handled = true;

        if (key == Key.Escape)
        {
            CancelShortcutRecording();
            return;
        }

        if (IsModifierKey(key))
        {
            ShowRecordingDraft(FormatModifierPreview(Keyboard.Modifiers));
            ShortcutError.Text = string.Empty;
            return;
        }

        var mods = Keyboard.Modifiers;
        var draft = new HotKeyConfiguration
        {
            Control = mods.HasFlag(ModifierKeys.Control),
            Alt = mods.HasFlag(ModifierKeys.Alt),
            Shift = mods.HasFlag(ModifierKeys.Shift),
            Win = mods.HasFlag(ModifierKeys.Windows),
            VirtualKey = KeyInterop.VirtualKeyFromKey(key),
        };

        if (!draft.IsValid)
        {
            ShowRecordingDraft("Press new shortcut…");
            ShortcutError.Text = "Need a modifier + key.";
            return;
        }

        _draftHotKey = draft;
        ShowRecordingDraft(draft.ToDisplayString(), muted: false);
        ShortcutHint.Text = "Press another shortcut, Apply to save, or Esc to cancel.";
        ShortcutError.Text = string.Empty;
    }

    private void OnShortcutPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_isRecordingShortcut)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifierKey(key))
            return;

        // Refresh modifier preview while waiting for a non-modifier key.
        if (ShortcutRecorderText.Text.EndsWith("…", StringComparison.Ordinal))
            ShowRecordingDraft(FormatModifierPreview(Keyboard.Modifiers));
    }

    private void BeginShortcutRecording()
    {
        if (_isRecordingShortcut)
            return;

        _isRecordingShortcut = true;
        _shortcutBeforeRecording = _draftHotKey.Clone();
        ShortcutError.Text = string.Empty;
        ShowRecordingDraft("Press new shortcut…");
        ShortcutHint.Text = "Recording… Esc cancels.";
        ShortcutRecorderHost.BorderBrush = (MediaBrush)FindResource("AccentBrush");
        ShortcutRecorderHost.Background = (MediaBrush)FindResource("HoverBrush");
    }

    private void CancelShortcutRecording()
    {
        if (!_isRecordingShortcut)
            return;

        _draftHotKey = _shortcutBeforeRecording.Clone();
        EndShortcutRecording(restorePrevious: false);
        // Move focus away so we don't immediately re-enter recording via GotFocus.
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void EndShortcutRecording(bool restorePrevious)
    {
        if (!_isRecordingShortcut)
            return;

        if (restorePrevious)
            _draftHotKey = _shortcutBeforeRecording.Clone();

        _isRecordingShortcut = false;
        RefreshShortcutDisplay();
    }

    private void RefreshShortcutDisplay()
    {
        ShortcutRecorderText.Text = _draftHotKey.ToDisplayString();
        ShortcutRecorderText.Foreground = (MediaBrush)FindResource("TextBrush");
        ShortcutRecorderHost.BorderBrush = (MediaBrush)FindResource("BorderBrush");
        ShortcutRecorderHost.Background = (MediaBrush)FindResource("SurfaceBrush");
        ShortcutHint.Text = "Click the field, then press a new shortcut.";
    }

    private void ShowRecordingDraft(string text, bool muted = true)
    {
        ShortcutRecorderText.Text = text;
        ShortcutRecorderText.Foreground = (MediaBrush)FindResource(muted ? "MutedBrush" : "TextBrush");
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string FormatModifierPreview(ModifierKeys mods)
    {
        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count == 0 ? "Press new shortcut…" : string.Join(" + ", parts) + " + …";
    }

    private void OnApplyShortcut(object sender, RoutedEventArgs e)
    {
        if (_isRecordingShortcut)
            EndShortcutRecording(restorePrevious: false);

        if (!_draftHotKey.IsValid)
        {
            ShortcutError.Text = "Invalid shortcut.";
            RefreshShortcutDisplay();
            return;
        }

        var previous = _settingsStore.Current.HotKey.Clone();
        if (!_tryRegisterHotKey(_draftHotKey))
        {
            _tryRegisterHotKey(previous);
            ShortcutError.Text = "Shortcut in use.";
            _draftHotKey = previous;
            RefreshShortcutDisplay();
            return;
        }

        _settingsStore.Update(s => s.HotKey = _draftHotKey.Clone());
        RefreshShortcutDisplay();
        ShortcutError.Text = string.Empty;
        _onSettingsChanged();
    }

    private void OnRestoreDefaultShortcut(object sender, RoutedEventArgs e)
    {
        if (_isRecordingShortcut)
            EndShortcutRecording(restorePrevious: false);

        _draftHotKey = HotKeyConfiguration.Default.Clone();
        RefreshShortcutDisplay();
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
        if (ConfirmDialog.Confirm(this, "Clear history?", confirmText: "Clear", danger: true))
            _onClearHistory();
        DiskUsageText.Text = FormatBytes(_historyStore.CalculateDiskUsageBytes());
    }

    private void OnHistoryLimitLostFocus(object sender, RoutedEventArgs e) =>
        ClampHistoryLimitTextBox();

    private void ClampHistoryLimitTextBox()
    {
        if (!int.TryParse(HistoryLimit.Text, out var limit))
            return;

        if (limit > AppSettings.MaxHistoryLimit)
        {
            HistoryLimit.Text = AppSettings.MaxHistoryLimit.ToString();
            return;
        }

        if (limit < AppSettings.MinHistoryLimit)
            HistoryLimit.Text = AppSettings.MinHistoryLimit.ToString();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ClampHistoryLimitTextBox();
        if (!int.TryParse(HistoryLimit.Text, out var limit))
            limit = AppSettings.DefaultHistoryLimit;
        if (limit > AppSettings.MaxHistoryLimit)
            limit = AppSettings.MaxHistoryLimit;
        if (limit < AppSettings.MinHistoryLimit)
            limit = AppSettings.MinHistoryLimit;

        if (!double.TryParse(MaxSizeMb.Text, out var mb))
            mb = AppSettings.DefaultMaxItemSizeBytes / (1024.0 * 1024.0);

        var wantStartup = StartWithWindows.IsChecked == true;
        if (_startupService.CanManageStartup && !_startupService.SetEnabled(wantStartup))
        {
            wantStartup = _startupService.IsEnabled();
            StartWithWindows.IsChecked = wantStartup;
            ConfirmDialog.Alert(this, "Couldn't update", "Start with Windows setting failed.");
        }
        else if (!_startupService.CanManageStartup)
        {
            wantStartup = _startupService.IsEnabled();
        }

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
            s.Theme = GetSelectedTheme();
        });

        _themeSaved = true;
        _applyTheme(GetSelectedTheme());
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
