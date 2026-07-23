using ClipStack.Core.Models;
using ClipStack.Core.Utilities;
using ClipStack.Interop;

namespace ClipStack.Services;

/// <summary>
/// Thin wrapper around native hotkey registration owned by <see cref="NativeMessageWindow"/>.
/// </summary>
internal sealed class HotKeyService
{
    private readonly NativeMessageWindow _native;
    private readonly FileLogger _logger;

    public HotKeyService(NativeMessageWindow native, FileLogger logger)
    {
        _native = native;
        _logger = logger;
    }

    public bool IsRegistered => _native.IsHotKeyRegistered;

    public HotKeyConfiguration? Current => _native.RegisteredHotKey;

    public bool TryRegister(HotKeyConfiguration configuration)
    {
        var ok = _native.TryRegisterHotKey(configuration);
        if (!ok)
            _logger.Warn("HotKeyRegister", "Registration failed");
        return ok;
    }

    public void Unregister() => _native.UnregisterHotKeyInternal();
}
