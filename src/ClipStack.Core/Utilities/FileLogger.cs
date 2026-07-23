using System.Text;

namespace ClipStack.Core.Utilities;

public sealed class FileLogger : IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly long _maxBytes;
    private StreamWriter? _writer;
    private string _currentPath = string.Empty;
    private bool _disposed;

    public FileLogger(string directory, long maxBytesPerFile = 1_000_000)
    {
        _directory = directory;
        _maxBytes = maxBytesPerFile;
        Directory.CreateDirectory(_directory);
        OpenCurrent();
    }

    public void Info(string operation, string message) => Write("INFO", operation, message, null);

    public void Warn(string operation, string message) => Write("WARN", operation, message, null);

    public void Error(string operation, Exception? ex, string? message = null)
    {
        var type = ex?.GetType().Name ?? "None";
        var sanitized = Sanitize(message ?? ex?.Message ?? string.Empty);
        Write("ERROR", operation, $"{type}: {sanitized}", type);
    }

    private void Write(string severity, string operation, string message, string? exceptionType)
    {
        if (_disposed) return;

        try
        {
            var line = $"{DateTimeOffset.UtcNow:O}\t{severity}\t{Sanitize(operation)}\t{Sanitize(message)}";
            if (!string.IsNullOrEmpty(exceptionType))
                line += $"\t{exceptionType}";

            lock (_gate)
            {
                RotateIfNeeded_NoLock();
                _writer?.WriteLine(line);
                _writer?.Flush();
            }

#if DEBUG
            System.Diagnostics.Debug.WriteLine(line);
#endif
        }
        catch
        {
            // logging must never crash the app
        }
    }

    private void OpenCurrent()
    {
        _currentPath = Path.Combine(_directory, "clipstack.log");
        _writer = new StreamWriter(new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    private void RotateIfNeeded_NoLock()
    {
        try
        {
            if (!File.Exists(_currentPath))
                return;

            var info = new FileInfo(_currentPath);
            if (info.Length < _maxBytes)
                return;

            _writer?.Dispose();
            _writer = null;

            var archive = Path.Combine(_directory, "clipstack.1.log");
            if (File.Exists(archive))
                File.Delete(archive);
            File.Move(_currentPath, archive);
            OpenCurrent();
        }
        catch
        {
            try { OpenCurrent(); } catch { /* ignore */ }
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Strip control chars; never intentionally include clipboard content.
        var sb = new StringBuilder(Math.Min(value.Length, 500));
        foreach (var ch in value.Take(500))
        {
            if (ch is '\t' or >= ' ')
                sb.Append(ch);
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
