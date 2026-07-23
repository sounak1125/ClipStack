using System.Text;
using System.Text.Json;
using ClipStack.Core.Models;
using ClipStack.Core.Utilities;

namespace ClipStack.Core.Storage;

public sealed class ReleaseConfigStore
{
    private readonly StoragePaths _paths;

    public ReleaseConfigStore(StoragePaths paths)
    {
        _paths = paths;
    }

    public ReleaseConfiguration Load(string? bundledFallbackPath = null)
    {
        _paths.EnsureCreated();
        var bundled = TryLoadConfiguration(bundledFallbackPath);

        if (!File.Exists(_paths.ReleaseConfigFile) && bundled is not null)
        {
            try
            {
                Save(bundled);
            }
            catch { /* ignore */ }
        }

        var local = TryLoadConfiguration(_paths.ReleaseConfigFile);
        if (local is null)
            return bundled ?? new ReleaseConfiguration();

        // Migrate early installs that persisted the original empty feed.
        if (!local.IsConfigured && bundled is { IsConfigured: true })
        {
            try { Save(bundled); } catch { /* use bundled in memory */ }
            return bundled;
        }

        return local;
    }

    public void Save(ReleaseConfiguration config)
    {
        _paths.EnsureCreated();
        var tmp = _paths.ReleaseConfigFile + ".tmp";
        var json = JsonSerializer.Serialize(config, AppIdentity.JsonOptions);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        PathSafety.AtomicReplaceFile(_paths.ReleaseConfigFile, tmp);
    }

    private static ReleaseConfiguration? TryLoadConfiguration(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<ReleaseConfiguration>(json, AppIdentity.JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
