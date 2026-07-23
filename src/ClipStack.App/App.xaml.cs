using System.Windows;
using System.Windows.Threading;
using ClipStack.Core.Utilities;

namespace ClipStack;

public partial class App : Application
{
    private AppController? _controller;
    private FileLogger? _logger;

    internal void Attach(AppController controller, FileLogger logger)
    {
        _controller = controller;
        _logger = logger;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _controller?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("DispatcherUnhandled", e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _logger?.Error("DomainUnhandled", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error("UnobservedTask", e.Exception);
        e.SetObserved();
    }
}
