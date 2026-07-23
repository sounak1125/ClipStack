using System.Windows;
using System.Windows.Interop;
using ClipStack.Core.Models;
using ClipStack.Core.Utilities;
using ClipStack.Interop;

namespace ClipStack.Services;

internal sealed class PopupPositionService
{
    private const int CursorGap = 12;
    private const int WorkAreaMargin = 8;

    public void PositionNearCursor(Window window)
    {
        if (!NativeMethods.GetCursorPos(out var pt))
            return;

        var monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var scaleX = 1.0;
        var scaleY = 1.0;
        try
        {
            if (NativeMethods.GetDpiForMonitor(
                    monitor,
                    NativeMethods.MDT_EFFECTIVE_DPI,
                    out var dpiX,
                    out var dpiY) == 0)
            {
                scaleX = dpiX / 96.0;
                scaleY = dpiY / 96.0;
            }
        }
        catch
        {
            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            if (dpi > 0)
                scaleX = scaleY = dpi / 96.0;
        }

        var width = (int)Math.Ceiling(window.ActualWidth * scaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * scaleY);
        if (width <= 0 || height <= 0)
            return;

        // Prefer above and just to the right of the pointer. Flip on either
        // axis when the popup would leave the current monitor's work area.
        var x = pt.X + CursorGap;
        if (x + width > info.rcWork.Right - WorkAreaMargin)
            x = pt.X - width - CursorGap;

        var y = pt.Y - height - CursorGap;
        if (y < info.rcWork.Top + WorkAreaMargin)
            y = pt.Y + CursorGap;

        x = Math.Clamp(
            x,
            info.rcWork.Left + WorkAreaMargin,
            Math.Max(info.rcWork.Left + WorkAreaMargin, info.rcWork.Right - width - WorkAreaMargin));
        y = Math.Clamp(
            y,
            info.rcWork.Top + WorkAreaMargin,
            Math.Max(info.rcWork.Top + WorkAreaMargin, info.rcWork.Bottom - height - WorkAreaMargin));

        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}

internal sealed class ForegroundWindowService
{
    private IntPtr _lastForeground;

    public void Capture() => _lastForeground = NativeMethods.GetForegroundWindow();

    public IntPtr LastForeground => _lastForeground;

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_lastForeground == IntPtr.Zero)
            return false;

        try
        {
            if (NativeMethods.IsIconic(_lastForeground))
                NativeMethods.ShowWindow(_lastForeground, NativeMethods.SW_RESTORE);

            NativeMethods.SetForegroundWindow(_lastForeground);

            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (NativeMethods.GetForegroundWindow() == _lastForeground)
                    return true;

                if (attempt == 3)
                    NativeMethods.SetForegroundWindow(_lastForeground);

                await Task.Delay(25, cancellationToken).ConfigureAwait(true);
            }

            return NativeMethods.GetForegroundWindow() == _lastForeground;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class AutoPasteService
{
    private readonly FileLogger _logger;

    public AutoPasteService(FileLogger logger)
    {
        _logger = logger;
    }

    public bool TrySendCtrlV()
    {
        try
        {
            var inputs = new NativeMethods.INPUT[4];
            inputs[0] = Key(NativeMethods.VK_CONTROL, keyUp: false);
            inputs[1] = Key(NativeMethods.VK_V, keyUp: false);
            inputs[2] = Key(NativeMethods.VK_V, keyUp: true);
            inputs[3] = Key(NativeMethods.VK_CONTROL, keyUp: true);

            var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent != inputs.Length)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _logger.Warn("SendInput", $"Inserted {sent}/{inputs.Length} events; Win32 error {error}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("SendInput", ex);
            return false;
        }
    }

    private static NativeMethods.INPUT Key(ushort vk, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
