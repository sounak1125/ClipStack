using System.Windows;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;
using ClipStack.Core.Utilities;
using ClipStack.Interop;

namespace ClipStack.Services;

internal sealed class ClipboardCaptureService
{
    private readonly NativeMessageWindow _native;
    private readonly ClipboardFormatReader _reader;
    private readonly HistoryStore _history;
    private readonly SettingsStore _settings;
    private readonly SelfCopySuppression _suppression;
    private readonly FileLogger _logger;
    private readonly NotificationCooldown _oversizedCooldown = new();
    private readonly object _gate = new();

    private bool _captureRunning;
    private bool _capturePending;
    private CancellationTokenSource _cts = new();
    private bool _accepting = true;

    public event Action? HistoryChanged;
    public event Action<string>? NotifyUser;

    public ClipboardCaptureService(
        NativeMessageWindow native,
        ClipboardFormatReader reader,
        HistoryStore history,
        SettingsStore settings,
        SelfCopySuppression suppression,
        FileLogger logger)
    {
        _native = native;
        _reader = reader;
        _history = history;
        _settings = settings;
        _suppression = suppression;
        _logger = logger;
    }

    public void Start()
    {
        _native.ClipboardUpdated += OnClipboardUpdated;
    }

    public void StopAccepting()
    {
        _accepting = false;
        try { _cts.Cancel(); } catch { }
    }

    public void DisposeSubscriptions()
    {
        _native.ClipboardUpdated -= OnClipboardUpdated;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private void OnClipboardUpdated(object? sender, EventArgs e)
    {
        if (!_accepting)
            return;

        lock (_gate)
        {
            if (_captureRunning)
            {
                _capturePending = true;
                return;
            }

            _captureRunning = true;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(RunCaptureLoopAsync);
    }

    private async Task RunCaptureLoopAsync()
    {
        try
        {
            while (true)
            {
                await CaptureOnceAsync().ConfigureAwait(true);

                // Clearing _captureRunning and testing _capturePending must happen under
                // one lock. Releasing first would let an update that arrives in between
                // set the pending flag on a loop that is already exiting, dropping the clip.
                lock (_gate)
                {
                    if (!_capturePending)
                    {
                        _captureRunning = false;
                        return;
                    }

                    _capturePending = false;
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                _logger.Error("CaptureLoop", ex);

            lock (_gate)
            {
                _captureRunning = false;
                _capturePending = false;
            }
        }
    }

    private async Task CaptureOnceAsync()
    {
        var token = _cts.Token;
        if (token.IsCancellationRequested || !_accepting)
            return;

        var settings = _settings.Current;
        if (settings.PauseCapture)
            return;

        // Phase 1: STA-bound clipboard marshalling, on the UI thread.
        ClipboardSnapshot? snapshot;
        try
        {
            snapshot = await _reader.ReadSnapshotAsync(settings, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("ReadSnapshot", ex);
            return;
        }

        if (snapshot is null)
            return;

        // Phase 2: decode, resize, encode, hash and write to disk — all off the UI thread.
        // A large image or a 200 MB file capture must never freeze the popup or the hotkey.
        NewClipboardItemData? data;
        try
        {
            data = await Task.Run(() => _reader.BuildItemData(snapshot, settings), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("BuildItemData", ex);
            return;
        }

        if (data is null)
            return;

        if (data.IsOversized())
        {
            if (_oversizedCooldown.TryAcquire(TimeSpan.FromMinutes(2)))
                NotifyUser?.Invoke("Clipboard item skipped — exceeds size limit.");
            return;
        }

        if (_suppression.ShouldIgnore(data.ContentHash))
            return;

        try
        {
            await Task.Run(() => _history.AddOrPromote(data, settings.HistoryLimit), token).ConfigureAwait(true);
            HistoryChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("HistoryAdd", ex);
        }
    }
}
