using System.Windows;

namespace ClipStack.Views;

public partial class UpdateNotificationWindow : Window
{
    public event Action? RestartRequested;
    public event Action? DismissRequested;

    public UpdateNotificationWindow(string version)
    {
        InitializeComponent();
        VersionTitle.Text = $"ClipStack {version} is available";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 18;
        Top = workArea.Bottom - ActualHeight - 18;
    }

    private void OnRestart(object sender, RoutedEventArgs e) => RestartRequested?.Invoke();

    private void OnDismiss(object sender, RoutedEventArgs e) => DismissRequested?.Invoke();
}
