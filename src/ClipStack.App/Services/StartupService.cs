using Microsoft.Win32;
using ClipStack.Core;
using ClipStack.Core.Utilities;

namespace ClipStack.Services;

public sealed class StartupService
{
    private readonly FileLogger _logger;

    public StartupService(FileLogger logger)
    {
        _logger = logger;
    }

    public bool CanManageStartup => IsInstalledBuild();

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            var value = key?.GetValue(AppIdentity.StartupRegistryValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            _logger.Error("StartupIsEnabled", ex);
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            // Never let a debug/publish build replace or remove the installed app's
            // startup registration with a path inside the source tree.
            if (!CanManageStartup)
            {
                _logger.Warn("StartupSetEnabled", "Ignored startup change from an unpackaged build.");
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key is null)
                return false;

            if (enabled)
            {
                var path = GetExecutablePathForStartup();
                if (string.IsNullOrWhiteSpace(path))
                    return false;
                key.SetValue(AppIdentity.StartupRegistryValueName, Quote(path));
            }
            else
            {
                key.DeleteValue(AppIdentity.StartupRegistryValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("StartupSetEnabled", ex);
            return false;
        }
    }

    public void RefreshInstalledPathIfNeeded()
    {
        try
        {
            if (IsInstalledBuild() && IsEnabled())
                SetEnabled(true);
        }
        catch (Exception ex)
        {
            _logger.Error("StartupRefreshPath", ex);
        }
    }

    public static bool IsInstalledBuild()
    {
        try
        {
            // Velopack places Update.exe next to the app when packaged/installed.
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "Update.exe"))
                   || File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));
        }
        catch
        {
            return false;
        }
    }

    private static string GetExecutablePathForStartup()
    {
        // Prefer the current process path (Velopack updates keep this correct after refresh).
        return Environment.ProcessPath
               ?? Path.Combine(AppContext.BaseDirectory, AppIdentity.ExecutableName);
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
