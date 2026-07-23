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

        _ = Application.Current.Dispatcher.InvokeAsync(async () => await RunCaptureLoopAsync());
    }

    private async Task RunCaptureLoopAsync()
    {
        try
        {
            do
            {
                lock (_gate) { _capturePending = false; }

                var token = _cts.Token;
                if (token.IsCancellationRequested || !_accepting)
                    break;

                var settings = _settings.Current;
                if (settings.PauseCapture)
                    continue;

                NewClipboardItemData? data;
                try
                {
                    data = await _reader.CaptureAsync(settings, token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("CaptureAsync", ex);
                    continue;
                }

                if (data is null)
                    continue;

                if (data.IsOversized())
                {
                    if (_oversizedCooldown.TryAcquire(TimeSpan.FromMinutes(2)))
                        NotifyUser?.Invoke("Clipboard item skipped — exceeds size limit.");
                    continue;
                }

                if (_suppression.ShouldIgnore(data.ContentHash))
                    continue;

                try
                {
                    _history.AddOrPromote(data, settings.HistoryLimit);
                    HistoryChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.Error("HistoryAdd", ex);
                }
            }
            while (TakePending());
        }
        finally
        {
            lock (_gate) { _captureRunning = false; }
        }
    }

    private bool TakePending()
    {
        lock (_gate)
        {
            if (!_capturePending)
                return false;
            _capturePending = false;
            return true;
        }
    }
}
