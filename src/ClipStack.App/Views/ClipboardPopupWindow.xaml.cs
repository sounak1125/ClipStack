using System.Windows;
using System.Windows.Input;
using ClipStack.ViewModels;

namespace ClipStack.Views;

public partial class ClipboardPopupWindow : Window
{
    private bool _suppressDeactivateHide;
    private bool _openingSettings;

    /// <summary>Raised with true when Shift was held, meaning "invert the plain-text setting".</summary>
    public event Action<bool>? PasteRequested;
    public event Action? DeleteRequested;
    public event Action? TogglePinRequested;
    public event Action? SettingsRequested;
    public event Action? CloseRequested;

    public ClipboardPopupWindow()
    {
        InitializeComponent();
    }

    public PopupViewModel? ViewModel => DataContext as PopupViewModel;

    public void BeginSuppressDeactivate() => _suppressDeactivateHide = true;

    public void EndSuppressDeactivate() => _suppressDeactivateHide = false;

    public void ScrollSelectionIntoView()
    {
        // Defer until layout has applied SelectedItem binding and the list is measured.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Hidden popups retain ScrollViewer offset; force top before ScrollIntoView.
                if (FindDescendantScrollViewer(HistoryList) is { } scroll)
                    scroll.ScrollToHome();

                var selected = ViewModel?.SelectedItem;
                if (selected is null)
                {
                    if (!IsFilterFocused)
                        HistoryList.Focus();
                    return;
                }

                HistoryList.ScrollIntoView(selected);

                // Never pull focus out of the filter box while the user is typing.
                if (IsFilterFocused)
                    return;

                if (HistoryList.ItemContainerGenerator.ContainerFromItem(selected) is System.Windows.Controls.ListBoxItem container)
                    container.Focus();
                else
                    HistoryList.Focus();
            }
            catch
            {
                // Ignore layout races while the popup is closing.
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private bool IsFilterFocused => FilterBox.IsKeyboardFocusWithin;

    /// <summary>Shift inverts the configured plain-text paste behaviour.</summary>
    private static bool IsShiftHeld => (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

    private void ActivateFilter(PopupViewModel vm)
    {
        vm.IsFilterVisible = true;

        // The box is collapsed until this point, so it cannot take focus until layout runs.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                FilterBox.Focus();
                FilterBox.CaretIndex = FilterBox.Text.Length;
            }
            catch
            {
                // Popup may already be closing.
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ExitFilterToList(PopupViewModel vm)
    {
        // Keep an active filter applied — the list stays narrowed and the normal
        // 1-9 / Delete shortcuts operate on what is visible. An empty box just collapses.
        if (!vm.HasSearchText)
            vm.IsFilterVisible = false;

        var selected = vm.SelectedItem;
        if (selected is not null
            && HistoryList.ItemContainerGenerator.ContainerFromItem(selected) is System.Windows.Controls.ListBoxItem container)
        {
            container.Focus();
        }
        else
        {
            HistoryList.Focus();
        }
    }

    private static System.Windows.Controls.ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer sv)
                return sv;
            var nested = FindDescendantScrollViewer(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivateHide || _openingSettings)
            return;
        CloseRequested?.Invoke();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;

        // While the filter box has focus it owns every key except navigation, so digits,
        // Delete and letters edit the query instead of triggering list shortcuts.
        if (IsFilterFocused)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    ExitFilterToList(vm);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    PasteRequested?.Invoke(IsShiftHeld);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
            }

            return;
        }

        // "/" is the primary filter key; Ctrl+F is the layout-independent equivalent for
        // keyboards where "/" is not on the OemQuestion key.
        if (e.Key is Key.OemQuestion or Key.Divide
            || (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
        {
            ActivateFilter(vm);
            e.Handled = true;
            return;
        }

        // Ctrl+P rather than bare P, so P stays available for future type-to-filter.
        if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            TogglePinRequested?.Invoke();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                CloseRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.Enter:
                PasteRequested?.Invoke(IsShiftHeld);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                SelectByNumber(1); e.Handled = true; break;
            case Key.D2:
            case Key.NumPad2:
                SelectByNumber(2); e.Handled = true; break;
            case Key.D3:
            case Key.NumPad3:
                SelectByNumber(3); e.Handled = true; break;
            case Key.D4:
            case Key.NumPad4:
                SelectByNumber(4); e.Handled = true; break;
            case Key.D5:
            case Key.NumPad5:
                SelectByNumber(5); e.Handled = true; break;
            case Key.D6:
            case Key.NumPad6:
                SelectByNumber(6); e.Handled = true; break;
            case Key.D7:
            case Key.NumPad7:
                SelectByNumber(7); e.Handled = true; break;
            case Key.D8:
            case Key.NumPad8:
                SelectByNumber(8); e.Handled = true; break;
            case Key.D9:
            case Key.NumPad9:
                SelectByNumber(9); e.Handled = true; break;
            case Key.D0:
            case Key.NumPad0:
                SelectByNumber(10); e.Handled = true; break;
        }
    }

    private void MoveSelection(int delta)
    {
        var vm = ViewModel;
        if (vm is null || vm.Items.Count == 0) return;
        var index = vm.SelectedItem is null ? 0 : vm.Items.IndexOf(vm.SelectedItem);
        index = Math.Clamp(index + delta, 0, vm.Items.Count - 1);
        vm.SelectedItem = vm.Items[index];
        HistoryList.ScrollIntoView(vm.SelectedItem);
    }

    private void SelectByNumber(int number)
    {
        var vm = ViewModel;
        if (vm is null) return;
        var item = vm.Items.FirstOrDefault(i => i.ShortcutNumber == number);
        if (item is null) return;
        vm.SelectedItem = item;
        PasteRequested?.Invoke(IsShiftHeld);
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        // Prefer the clicked row's DataContext — SelectedItem binding can lag
        // behind PreviewMouseLeftButtonUp on a newly clicked row.
        if (e.OriginalSource is not DependencyObject d)
            return;

        var listBoxItem = FindAncestor<System.Windows.Controls.ListBoxItem>(d);
        if (listBoxItem?.DataContext is not ClipboardItemViewModel clicked)
            return;

        var vm = ViewModel;
        if (vm is null) return;

        vm.SelectedItem = clicked;
        PasteRequested?.Invoke(IsShiftHeld);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _openingSettings = true;
        try { SettingsRequested?.Invoke(); }
        finally { _openingSettings = false; }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
