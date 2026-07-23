using System.Security.Principal;
using System.Threading;
using ClipStack.Core;
using ClipStack.Core.Utilities;

namespace ClipStack.Services;

internal sealed class SingleInstanceService : IDisposable
{
    private readonly FileLogger? _logger;
    private Mutex? _mutex;
    private EventWaitHandle? _signal;
    private bool _ownsMutex;
    private CancellationTokenSource? _listenCts;
    private bool _disposed;

    public event Action? ShowHistoryRequested;

    public SingleInstanceService(FileLogger? logger = null)
    {
        _logger = logger;
    }

    public bool TryAcquire()
    {
        var suffix = GetUserSuffix();
        var mutexName = AppIdentity.MutexNamePrefix + "." + suffix;
        var eventName = AppIdentity.SignalEventNamePrefix + "." + suffix;

        _mutex = new Mutex(initiallyOwned: true, mutexName, out _ownsMutex);
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        if (!_ownsMutex)
        {
            try { _signal.Set(); } catch { /* ignore */ }
            return false;
        }

        _listenCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_listenCts.Token));
        return true;
    }

    private void ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_signal is null)
                    return;

                if (_signal.WaitOne(500))
                {
                    ShowHistoryRequested?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error("SingleInstanceListen", ex);
                break;
            }
        }
    }

    private static string GetUserSuffix()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value.Replace('-', '_') ?? Environment.UserName;
        }
        catch
        {
            return Environment.UserName;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listenCts?.Cancel(); } catch { }
        try { _listenCts?.Dispose(); } catch { }
        try
        {
            if (_ownsMutex)
                _mutex?.ReleaseMutex();
        }
        catch { }
        try { _mutex?.Dispose(); } catch { }
        try { _signal?.Dispose(); } catch { }
    }
}
