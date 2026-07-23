using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClipStack.Core.Models;
using ClipStack.Core.Utilities;

namespace ClipStack.Interop;

internal sealed class NativeMessageWindow : IDisposable
{
    public const int HotKeyId = 1;

    private readonly FileLogger _logger;
    private HwndSource? _source;
    private bool _clipboardListening;
    private bool _hotKeyRegistered;
    private HotKeyConfiguration? _registeredHotKey;
    private bool _disposed;

    public event EventHandler? ClipboardUpdated;
    public event EventHandler? HotKeyPressed;

    public NativeMessageWindow(FileLogger logger)
    {
        _logger = logger;
    }

    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public bool IsHotKeyRegistered => _hotKeyRegistered;

    public HotKeyConfiguration? RegisteredHotKey => _registeredHotKey?.Clone();

    public void Create()
    {
        if (_source is not null)
            return;

        var parameters = new HwndSourceParameters("ClipStackMessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool StartClipboardListener()
    {
        if (_source is null || _clipboardListening)
            return _clipboardListening;

        if (NativeMethods.AddClipboardFormatListener(_source.Handle))
        {
            _clipboardListening = true;
            return true;
        }

        _logger.Error("AddClipboardFormatListener", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return false;
    }

    public bool TryRegisterHotKey(HotKeyConfiguration config)
    {
        if (_source is null)
            return false;

        if (!config.IsValid)
            return false;

        if (_hotKeyRegistered)
            UnregisterHotKeyInternal();

        var modifiers = NativeMethods.MOD_NOREPEAT;
        if (config.Control) modifiers |= NativeMethods.MOD_CONTROL;
        if (config.Alt) modifiers |= NativeMethods.MOD_ALT;
        if (config.Shift) modifiers |= NativeMethods.MOD_SHIFT;
        if (config.Win) modifiers |= NativeMethods.MOD_WIN;

        if (NativeMethods.RegisterHotKey(_source.Handle, HotKeyId, modifiers, (uint)config.VirtualKey))
        {
            _hotKeyRegistered = true;
            _registeredHotKey = config.Clone();
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        _logger.Error("RegisterHotKey", new System.ComponentModel.Win32Exception(error));
        return false;
    }

    public void UnregisterHotKeyInternal()
    {
        if (_source is null || !_hotKeyRegistered)
            return;

        NativeMethods.UnregisterHotKey(_source.Handle, HotKeyId);
        _hotKeyRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_CLIPBOARDUPDATE:
                ClipboardUpdated?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NativeMethods.WM_HOTKEY:
                if (wParam.ToInt32() == HotKeyId)
                {
                    HotKeyPressed?.Invoke(this, EventArgs.Empty);
                    handled = true;
                }
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_source is not null && _clipboardListening)
            {
                NativeMethods.RemoveClipboardFormatListener(_source.Handle);
                _clipboardListening = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("RemoveClipboardFormatListener", ex);
        }

        try { UnregisterHotKeyInternal(); }
        catch (Exception ex) { _logger.Error("UnregisterHotKey", ex); }

        try
        {
            _source?.RemoveHook(WndProc);
            _source?.Dispose();
            _source = null;
        }
        catch (Exception ex)
        {
            _logger.Error("DisposeNativeWindow", ex);
        }
    }
}
