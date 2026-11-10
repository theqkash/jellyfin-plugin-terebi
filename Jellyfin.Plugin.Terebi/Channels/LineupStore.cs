using Jellyfin.Plugin.Terebi.Sources;
using Jellyfin.Plugin.Terebi.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Channels;

/// <summary>
/// Holds sources.json and lineup.json in memory and on disk.
/// </summary>
public sealed class LineupStore
{
    private readonly ILogger<LineupStore> _logger;
    private readonly string _sourcesPath;
    private readonly string _lineupPath;
    private List<SourceDefinition>? _sources;
    private Lineup? _lineup;
    private readonly object _gate = new();

    public LineupStore(TerebiPaths paths, ILogger<LineupStore> logger)
    {
        _logger = logger;
        _sourcesPath = paths.File("sources.json");
        _lineupPath = paths.File("lineup.json");
        Aliases.LoadUser(paths.File("aliases.json"), logger);
        NameNormalizer.LoadRegionAliases(paths.File("region-aliases.json"), logger);
    }

    public List<SourceDefinition> Sources
    {
        get
        {
            lock (_gate)
            {
                return _sources ??= JsonFile.Load(_sourcesPath, () => new List<SourceDefinition>(), _logger);
            }
        }
    }

    public Lineup Lineup
    {
        get
        {
            lock (_gate)
            {
                return _lineup ??= JsonFile.Load(_lineupPath, () => new Lineup(), _logger);
            }
        }
    }

    public SourceDefinition? GetSource(string id) => Sources.FirstOrDefault(s => s.Id == id);

    public LogicalChannel? GetChannel(Guid id) => Lineup.Channels.FirstOrDefault(c => c.Id == id);

    public Task SaveSourcesAsync(CancellationToken cancellationToken = default) => JsonFile.SaveAsync(_sourcesPath, Sources, cancellationToken);

    public Task SaveLineupAsync(CancellationToken cancellationToken = default)
    {
        Lineup.UpdatedUtc = DateTimeOffset.UtcNow;
        return JsonFile.SaveAsync(_lineupPath, Lineup, cancellationToken);
    }
}
