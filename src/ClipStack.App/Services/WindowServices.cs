using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
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
    public IntPtr LastForeground { get; private set; }

    public void Capture() => LastForeground = NativeMethods.GetForegroundWindow();

    public async Task RestoreFocusAsync(CancellationToken cancellationToken)
    {
        var target = LastForeground;
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            return;

        if (NativeMethods.GetForegroundWindow() == target)
            return;

        if (NativeMethods.IsIconic(target))
            NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);

        NativeMethods.SetForegroundWindow(target);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (NativeMethods.GetForegroundWindow() == target)
                return;

            if (attempt is 2 or 6)
                ForceForeground(target);

            await Task.Delay(30, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Mouse activation of our popup often blocks a plain SetForegroundWindow.
    /// AttachThreadInput temporarily joins input queues so focus can return to the target.
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();

        var attached = targetThread != 0 && targetThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }
}

internal enum AutoPasteResult
{
    Sent,
    Unavailable,
    ClipboardChanged,
}

internal sealed class AutoPasteService
{
    private readonly FileLogger _logger;

    public AutoPasteService(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<AutoPasteResult> TryPasteAsync(
        IntPtr target,
        uint clipboardSequence,
        CancellationToken cancellationToken)
    {
        var restoredFormat = (uint)DataFormats.GetDataFormat(ClipboardRestoreService.RestoredFormat).Id;
        var elapsed = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (NativeMethods.GetClipboardSequenceNumber() != clipboardSequence)
                return AutoPasteResult.ClipboardChanged;

            if (target == IntPtr.Zero || NativeMethods.GetForegroundWindow() != target)
                return AutoPasteResult.Unavailable;

            // A held modifier (including Shift for plain-text paste) changes Ctrl+V.
            // An open clipboard can also make the target miss the paste. Wait for both
            // before sending one shortcut, since a second shortcut could paste twice.
            if (!PasteKeysAreDown() && NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                var isRestoredClip = NativeMethods.IsClipboardFormatAvailable(restoredFormat);
                if (!NativeMethods.CloseClipboard())
                    return AutoPasteResult.Unavailable;

                if (!isRestoredClip || NativeMethods.GetClipboardSequenceNumber() != clipboardSequence)
                    return AutoPasteResult.ClipboardChanged;

                return NativeMethods.GetForegroundWindow() == target && TrySendCtrlV()
                    ? AutoPasteResult.Sent
                    : AutoPasteResult.Unavailable;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(true);
        }
        while (elapsed.ElapsedMilliseconds < 1000);

        return AutoPasteResult.Unavailable;
    }

    private static bool PasteKeysAreDown() =>
        NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) < 0
        || NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) < 0
        || NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) < 0
        || NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) < 0
        || NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) < 0
        || NativeMethods.GetAsyncKeyState(NativeMethods.VK_V) < 0;

    private bool TrySendCtrlV()
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
