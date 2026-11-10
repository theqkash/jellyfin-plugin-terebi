using System.Collections.Concurrent;
using Jellyfin.Plugin.Terebi.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Catalog;

/// <summary>
/// Everything the sources published, one JSON file per source, plus an in-memory index by "sourceId:key".
/// </summary>
public sealed class CatalogStore
{
    private readonly ConcurrentDictionary<string, List<RawStream>> _bySource = new(StringComparer.Ordinal);
    private readonly ILogger<CatalogStore> _logger;
    private readonly string _folder;
    private bool _loaded;

    public CatalogStore(TerebiPaths paths, ILogger<CatalogStore> logger)
    {
        _logger = logger;
        _folder = paths.Folder("catalog");
    }

    public IReadOnlyList<RawStream> ForSource(string sourceId)
    {
        EnsureLoaded();
        return _bySource.TryGetValue(sourceId, out var list) ? list : [];
    }

    public IEnumerable<RawStream> All()
    {
        EnsureLoaded();
        return _bySource.Values.SelectMany(v => v);
    }

    public RawStream? Find(string streamRef)
    {
        EnsureLoaded();
        var idx = streamRef.IndexOf(':', StringComparison.Ordinal);
        if (idx <= 0)
        {
            return null;
        }

        var sourceId = streamRef[..idx];
        var key = streamRef[(idx + 1)..];
        return _bySource.TryGetValue(sourceId, out var list) ? list.FirstOrDefault(s => s.Key == key) : null;
    }

    public async Task ReplaceSourceAsync(string sourceId, List<RawStream> streams, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        var previous = new Dictionary<string, RawStream>(StringComparer.Ordinal);
        if (_bySource.TryGetValue(sourceId, out var old))
        {
            foreach (var o in old)
            {
                previous.TryAdd(o.Key, o); // older catalogs may still carry a duplicated playlist entry
            }
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var s in streams)
        {
            s.SourceId = sourceId;
            s.LastSeenUtc = now;
            s.FirstSeenUtc = previous.TryGetValue(s.Key, out var p) ? p.FirstSeenUtc : now;
        }

        _bySource[sourceId] = streams;
        await JsonFile.SaveAsync(Path.Combine(_folder, sourceId + ".json"), streams, cancellationToken).ConfigureAwait(false);
    }

    public void RemoveSource(string sourceId)
    {
        _bySource.TryRemove(sourceId, out _);
        try
        {
            File.Delete(Path.Combine(_folder, sourceId + ".json"));
        }
        catch (IOException)
        {
            // ignore
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_bySource)
        {
            if (_loaded)
            {
                return;
            }

            Directory.CreateDirectory(_folder);
            foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                _bySource[id] = JsonFile.Load(file, () => new List<RawStream>(), _logger);
            }

            _logger.LogInformation("Terebi: catalog loaded, {Sources} sources, {Streams} streams", _bySource.Count, _bySource.Values.Sum(v => v.Count));
            _loaded = true;
        }
    }
}
