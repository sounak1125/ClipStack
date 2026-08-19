using System.Windows;

namespace ClipStack.Views;

public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    private ConfirmDialog()
    {
        InitializeComponent();
    }

    public static bool Confirm(
        Window? owner,
        string title,
        string? message = null,
        string confirmText = "OK",
        string cancelText = "Cancel",
        bool danger = false)
    {
        var dialog = Create(owner, title, message, confirmText, cancelText, danger, showCancel: true);
        dialog.ShowDialog();
        return dialog.Confirmed;
    }

    public static void Alert(Window? owner, string title, string? message = null, string okText = "OK")
    {
        var dialog = Create(owner, title, message, okText, cancelText: null, danger: false, showCancel: false);
        dialog.ShowDialog();
    }

    private static ConfirmDialog Create(
        Window? owner,
        string title,
        string? message,
        string confirmText,
        string? cancelText,
        bool danger,
        bool showCancel)
    {
        var dialog = new ConfirmDialog
        {
            Owner = owner,
            Title = title,
        };

        if (owner is null)
        {
            // Raised from the tray, with no window to parent to. CenterOwner degenerates
            // to the screen corner without an owner, and ShowInTaskbar is off, so an
            // unowned dialog would be easy to miss entirely — and this one guards a
            // destructive action.
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        dialog.TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(message))
        {
            dialog.MessageText.Text = message;
            dialog.MessageText.Visibility = Visibility.Visible;
        }

        dialog.ConfirmButton.Content = confirmText;
        dialog.ConfirmButton.Style = (Style)dialog.FindResource(danger ? "DangerButton" : "PrimaryButton");

        if (showCancel)
        {
            dialog.CancelButton.Content = cancelText ?? "Cancel";
        }
        else
        {
            dialog.CancelButton.Visibility = Visibility.Collapsed;
            dialog.ConfirmButton.IsCancel = true;
        }

        return dialog;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }
}
