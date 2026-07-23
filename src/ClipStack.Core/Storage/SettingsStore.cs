using System.Text;
using System.Text.Json;
using ClipStack.Core.Settings;
using ClipStack.Core.Utilities;

namespace ClipStack.Core.Storage;

public sealed class SettingsStore
{
    private readonly StoragePaths _paths;
    private readonly object _gate = new();
    private AppSettings _settings = new();

    public SettingsStore(StoragePaths paths)
    {
        _paths = paths;
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
                return _settings.Clone();
        }
    }

    public void Initialize(bool defaultStartWithWindows = false)
    {
        _paths.EnsureCreated();
        lock (_gate)
        {
            _settings = LoadOrDefault(defaultStartWithWindows);
            _settings.ValidateAndClamp();
            Save_NoLock(_settings);
        }
    }

    public AppSettings Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(_settings);
            _settings.ValidateAndClamp();
            Save_NoLock(_settings);
            return _settings.Clone();
        }
    }

    public void Replace(AppSettings settings)
    {
        lock (_gate)
        {
            _settings = settings.Clone();
            _settings.ValidateAndClamp();
            Save_NoLock(_settings);
        }
    }

    private AppSettings LoadOrDefault(bool defaultStartWithWindows)
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            var defaults = new AppSettings
            {
                StartWithWindows = defaultStartWithWindows,
            };
            defaults.ValidateAndClamp();
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_paths.SettingsFile, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, AppIdentity.JsonOptions);
            if (settings is null)
                throw new InvalidDataException("Settings null.");
            settings.ValidateAndClamp();
            return settings;
        }
        catch
        {
            try
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(_paths.SettingsFile, Path.Combine(_paths.Root, $"settings.corrupt.{stamp}.json"), overwrite: true);
            }
            catch { /* best effort */ }

            var defaults = new AppSettings { StartWithWindows = defaultStartWithWindows };
            defaults.ValidateAndClamp();
            return defaults;
        }
    }

    private void Save_NoLock(AppSettings settings)
    {
        var tmp = _paths.SettingsFile + ".tmp";
        var json = JsonSerializer.Serialize(settings, AppIdentity.JsonOptions);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            fs.Flush(true);
        }

        PathSafety.AtomicReplaceFile(_paths.SettingsFile, tmp);
    }
}
