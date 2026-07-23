using System.Windows;
using ClipStack.Core;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;
using ClipStack.Services;
using Velopack;

namespace ClipStack;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build().Run();
        }
        catch
        {
            // Velopack may throw in unpackaged/dev scenarios; continue.
        }

        var singleInstance = new SingleInstanceService();
        if (!singleInstance.TryAcquire())
        {
            singleInstance.Dispose();
            return;
        }

        FileLogger? logger = null;
        AppController? controller = null;
        try
        {
            var dataRoot = AppIdentity.GetDataDirectory();
            var paths = new StoragePaths(dataRoot);
            paths.EnsureCreated();
            logger = new FileLogger(paths.Logs);

            var installed = StartupService.IsInstalledBuild();
            var settings = new SettingsStore(paths);
            settings.Initialize(defaultStartWithWindows: installed);

            var history = new HistoryStore(paths);
            history.Initialize();

            var releaseStore = new ReleaseConfigStore(paths);
            var bundled = Path.Combine(AppContext.BaseDirectory, "release-config.json");
            var release = releaseStore.Load(bundled);

            var app = new App();
            controller = new AppController(logger, paths, settings, history, releaseStore, release, singleInstance);
            app.Attach(controller, logger);

            app.Startup += (_, _) =>
            {
                try
                {
                    controller.Start();
                }
                catch (Exception ex)
                {
                    logger.Error("AppStart", ex);
                    MessageBox.Show(
                        "ClipStack failed to start.\n\n" + ex.GetType().Name,
                        AppIdentity.ProductName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    app.Shutdown();
                }
            };

            app.Run();
        }
        catch (Exception ex)
        {
            try { logger?.Error("FatalInit", ex); } catch { }
            MessageBox.Show(
                "ClipStack could not start.\n\n" + ex.GetType().Name,
                AppIdentity.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            singleInstance.Dispose();
            logger?.Dispose();
        }
    }
}
