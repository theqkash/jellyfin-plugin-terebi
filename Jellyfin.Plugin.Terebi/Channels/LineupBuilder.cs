using System.Text.RegularExpressions;
using Jellyfin.Plugin.Terebi.Catalog;
using Jellyfin.Plugin.Terebi.Logos;
using Jellyfin.Plugin.Terebi.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Channels;

/// <summary>
/// Matches catalog streams onto logical channels:
///  1. explicit stream refs (by ref, or by remembered name+category when the provider recycled the id),
///  2. same EPG id,
///  3. same normalised name key,
///  4. otherwise a new channel, if the rule / source allows auto-adding.
/// Numbers come from the rule's block; user-pinned channels keep everything.
/// </summary>
public sealed class LineupBuilder
{
    private const string Sep = "\u001f";
    private readonly LineupStore _store;
    private readonly CatalogStore _catalog;
    private readonly LogoIndex _logoIndex;
    private readonly Epg.EpgService _epg;
    private readonly ILogger<LineupBuilder> _logger;

    public LineupBuilder(LineupStore store, CatalogStore catalog, LogoIndex logoIndex, Epg.EpgService epg, ILogger<LineupBuilder> logger)
    {
        _store = store;
        _catalog = catalog;
        _logoIndex = logoIndex;
        _epg = epg;
        _logger = logger;
    }

    /// <summary>
    /// Logo for an automatic channel: the stream whose source has the highest logo priority (ties: stream
    /// order), unless the iptv-org index outranks it and knows the channel.
    /// </summary>
    private string? ChooseLogo(LogicalChannel c, IReadOnlyList<RawStream> raws, IReadOnlyDictionary<string, SourceDefinition> sources)
    {
        var best = raws
            .Select((r, i) => (Raw: r, Priority: sources.TryGetValue(r.SourceId, out var s) ? s.LogoPriority : 0, Index: i))
            .Where(x => !string.IsNullOrWhiteSpace(x.Raw.Logo))
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Index)
            .FirstOrDefault();

        var cfg = Plugin.Instance?.Configuration;
        if (cfg?.EnableLogoIndex ?? true)
        {
            var indexPriority = cfg?.LogoIndexPriority ?? 5;
            if (best.Raw is null || indexPriority > best.Priority)
            {
                var label = raws.Select(r => sources.GetValueOrDefault(r.SourceId)).Select(src => src?.Country ?? src?.CategoryLabel).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                var epgIds = c.EpgIds.Concat(raws.Select(r => r.EpgId).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // Exact first: an icon the mapped XMLTV feed declares, then iptv-org by id; names after that.
                var fromXmltv = _epg.FindIcon(epgIds, string.Empty);
                if (fromXmltv is not null)
                {
                    c.LogoOrigin = "xmltv";
                    return fromXmltv;
                }

                var fromIndex = _logoIndex.Find(c.Name, LogoIndex.CountryOf(c, label), epgIds);
                if (fromIndex is not null)
                {
                    c.LogoOrigin = "index";
                    return fromIndex;
                }

                fromXmltv = _epg.FindIcon([], c.MatchKey, LogoIndex.CountryOf(c, label));
                if (fromXmltv is not null)
                {
                    c.LogoOrigin = "xmltv";
                    return fromXmltv;
                }
            }
        }

        c.LogoOrigin = best.Raw?.SourceId;
        return best.Raw?.Logo;
    }

    public sealed record RuleMatch(CategoryRule? Rule, bool Excluded, int Index = int.MaxValue);

    /// <summary>Terebi category for one raw stream: the source's fixed label if set, else a cleaned provider category.</summary>
    private static string CategoryOf(RawStream s, NameNormalizer.Parsed p, IReadOnlyDictionary<string, SourceDefinition> sources)
    {
        var src = sources.GetValueOrDefault(s.SourceId);
        if (!string.IsNullOrWhiteSpace(src?.CategoryLabel))
        {
            return src!.CategoryLabel!.Trim();
        }

        return NameNormalizer.CategoryLabel(s.Category, p.RegionPrefix ?? src?.Name);
    }

    public async Task<string> RebuildAsync(CancellationToken cancellationToken)
    {
        var lineup = _store.Lineup;
        var sources = _store.Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var rules = lineup.Rules.Where(r => r.Enabled).ToList();
        var compiled = rules.ToDictionary(r => r.Id, r => (Cat: SourceRefresher.Compile(r.CategoryPattern), Name: SourceRefresher.Compile(r.NamePattern)), StringComparer.Ordinal);

        var all = _catalog.All().Where(s => sources.TryGetValue(s.SourceId, out var src) && src.Enabled && !s.IsDash).ToList();
        var byRef = new Dictionary<string, RawStream>(StringComparer.Ordinal);
        foreach (var s in all)
        {
            byRef[s.Ref] = s;
        }

        var parsed = new Dictionary<string, NameNormalizer.Parsed>(StringComparer.Ordinal);
        foreach (var s in all)
        {
            parsed.TryAdd(s.Ref, NameNormalizer.Parse(s.Name));
        }
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        int rematched = 0, added = 0, newChannels = 0;

        // 1. Existing refs: keep what still exists; re-find recycled ids by remembered name+category.
        var byNameCat = new Dictionary<string, RawStream>(StringComparer.Ordinal);
        foreach (var s in all)
        {
            byNameCat.TryAdd(s.SourceId + Sep + s.Category + Sep + s.Name, s);
        }

        foreach (var channel in lineup.Channels)
        {
            foreach (var sr in channel.Streams)
            {
                if (byRef.TryGetValue(sr.Ref, out var raw))
                {
                    claimed.Add(sr.Ref);
                    sr.MatchedName = raw.Name;
                    sr.MatchedCategory = raw.Category;
                    continue;
                }

                if (sr.MatchedName is not null && byNameCat.TryGetValue(sr.SourceId + Sep + sr.MatchedCategory + Sep + sr.MatchedName, out var again) && !claimed.Contains(again.Ref))
                {
                    sr.Ref = again.Ref;
                    claimed.Add(again.Ref);
                    rematched++;
                }
            }
        }

        // 2./3. Unclaimed streams: attach to a channel with the same EPG id or name key, else create.
        var channelsByKey = new Dictionary<string, LogicalChannel>(StringComparer.Ordinal);
        var channelsByEpg = new Dictionary<string, LogicalChannel>(StringComparer.OrdinalIgnoreCase);
        // Identity = the iptv-org channel a name resolves to; joins "NIPPON TV", "JOAX-DTV" and "日テレ".
        var channelsByIdentity = new Dictionary<string, LogicalChannel>(StringComparer.OrdinalIgnoreCase);
        // Same name, different country ("XX: MUSIC", "YY: MUSIC" both read "MUSIC") must stay separate channels.
        var channelsByKeyCountry = new Dictionary<string, LogicalChannel>(StringComparer.Ordinal);
        var channelCountry = new Dictionary<LogicalChannel, string?>(ReferenceEqualityComparer.Instance);
        string? SourceCountry(string sourceId) => sources.TryGetValue(sourceId, out var src) && !string.IsNullOrWhiteSpace(src.Country) ? src.Country!.Trim().ToUpperInvariant() : null;
        static string? SameNation(string? c) => c switch { "GB" => "UK", "HK" or "MO" or "TW" => "CN", _ => c };
        static bool Compatible(string? a, string? b) => a is null || b is null || SameNation(a) == SameNation(b);
        foreach (var c in lineup.Channels)
        {
            var cCountry = LogoIndex.CountryOf(c) ?? c.Streams.Select(sr => SourceCountry(sr.SourceId)).FirstOrDefault(x => x is not null);
            channelCountry[c] = cCountry;
            if (c.MatchKey.Length > 0)
            {
                channelsByKey.TryAdd(c.MatchKey, c);
                if (cCountry is not null)
                {
                    channelsByKeyCountry.TryAdd(c.MatchKey + Sep + SameNation(cCountry), c);
                }
            }

            foreach (var e in c.EpgIds)
            {
                channelsByEpg.TryAdd(e, c);
            }

            var identity = _logoIndex.IdentityOf(c.Name, cCountry, c.EpgIds);
            if (identity is not null)
            {
                channelsByIdentity.TryAdd(identity, c);
            }
        }

        foreach (var s in all.Where(s => !claimed.Contains(s.Ref)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = parsed[s.Ref];
            var match = MatchRule(rules, compiled, s, p.DisplayName);
            if (match.Excluded)
            {
                continue;
            }

            LogicalChannel? target = null;
            var streamCountry = p.RegionPrefix is { Length: >= 2 } rp && rp[..2].All(char.IsLetter) ? rp[..2].ToUpperInvariant() : SourceCountry(s.SourceId);
            var streamIdentity = _logoIndex.IdentityOf(p.DisplayName, streamCountry, s.EpgId is null ? null : [s.EpgId]);
            if (s.EpgId is not null && channelsByEpg.TryGetValue(s.EpgId, out var byEpg) && Compatible(channelCountry.GetValueOrDefault(byEpg), streamCountry))
            {
                target = byEpg;
            }
            else if (streamCountry is not null && channelsByKeyCountry.TryGetValue(p.Key + Sep + SameNation(streamCountry), out var byKeyCountry))
            {
                target = byKeyCountry;
            }
            else if (channelsByKey.TryGetValue(p.Key, out var byKey) && Compatible(channelCountry.GetValueOrDefault(byKey), streamCountry))
            {
                target = byKey;
            }
            else if (streamIdentity is not null && channelsByIdentity.TryGetValue(streamIdentity, out var byIdentity))
            {
                target = byIdentity;
            }

            if (target is null)
            {
                var allowAuto = match.Rule?.AutoAdd ?? sources[s.SourceId].AutoAddChannels;
                if (!allowAuto || p.Key.Length == 0)
                {
                    continue;
                }

                var follow = (match.Rule?.FollowProviderName ?? false) || (Plugin.Instance?.Configuration.FollowProviderNamesByDefault ?? false);
                target = new LogicalChannel
                {
                    Name = p.DisplayName,
                    MatchKey = p.Key,
                    Category = match.Rule?.Category ?? CategoryOf(s, p, sources),
                    Logo = s.Logo,
                    Region = p.RegionPrefix,
                    NameMode = follow ? NameMode.FollowPrimary : NameMode.Fixed,
                    Auto = true,
                };
                if (s.EpgId is not null)
                {
                    target.EpgIds.Add(s.EpgId);
                    channelsByEpg.TryAdd(s.EpgId, target);
                }

                lineup.Channels.Add(target);
                channelCountry[target] = streamCountry;
                channelsByKey.TryAdd(p.Key, target);
                if (streamCountry is not null)
                {
                    channelsByKeyCountry.TryAdd(p.Key + Sep + SameNation(streamCountry), target);
                }
                if (streamIdentity is not null)
                {
                    channelsByIdentity.TryAdd(streamIdentity, target);
                }

                newChannels++;
            }
            else if (s.EpgId is not null && !target.EpgIds.Contains(s.EpgId, StringComparer.OrdinalIgnoreCase))
            {
                target.EpgIds.Add(s.EpgId);
                channelsByEpg.TryAdd(s.EpgId, target);
            }

            target.Streams.Add(new StreamRef { Ref = s.Ref, MatchedName = s.Name, MatchedCategory = s.Category, Variant = p.Variant });
            target.Logo ??= s.Logo;
            claimed.Add(s.Ref);
            added++;
        }

        // Order streams inside each channel: explicit priority, then source priority, then quality.
        foreach (var c in lineup.Channels)
        {
            var ordered = c.Streams
                .OrderBy(sr => sr.Priority)
                .ThenBy(sr => sources.TryGetValue(sr.SourceId, out var src) ? src.Priority : 1000)
                .ThenByDescending(sr => NameNormalizer.QualityRank(sr.Variant ?? "default"))
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Priority = i;
            }

            c.Streams = ordered;

            if (c.Auto && !c.Pinned)
            {
                var raws = c.Streams.Select(sr => byRef.GetValueOrDefault(sr.Ref)).Where(r => r is not null).Cast<RawStream>().ToList();
                var primaryRaw = raws.FirstOrDefault();
                if (primaryRaw is not null)
                {
                    var rule = raws.Select(r => MatchRule(rules, compiled, r, parsed[r.Ref].DisplayName)).Where(m => m.Rule is not null && !m.Excluded).OrderBy(m => m.Index).Select(m => m.Rule).FirstOrDefault();
                    if (rule is not null)
                    {
                        c.Category = rule.Category;
                        if (rule.FollowProviderName && c.NameMode == NameMode.Fixed && c.Auto)
                        {
                            c.NameMode = NameMode.FollowPrimary;
                        }
                    }
                    else
                    {
                        // Majority category across the channel's streams, so a "VIP" duplicate bundle does not win
                        // over the thematic category; ties go to the higher-priority stream.
                        c.Category = raws
                            .Select((r, i) => (Category: CategoryOf(r, parsed[r.Ref], sources), Index: i))
                            .GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Count())
                            .ThenBy(g => g.Min(x => x.Index))
                            .First().Key;
                    }

                    // Aliases added later change what a name resolves to: keep the match key current for auto channels.
                    var currentKey = NameNormalizer.Parse(c.Name).Key;
                    if (currentKey.Length > 0 && !string.Equals(c.MatchKey, currentKey, StringComparison.Ordinal))
                    {
                        c.MatchKey = currentKey;
                    }

                    if ((Plugin.Instance?.Configuration.AutoMapEpgByName ?? true) && !c.EpgIds.Any(_epg.HasPrograms))
                    {
                        // No provider id with guide data: take the XMLTV channel with the same name, if unambiguous.
                        // Names the iptv-org database knows for the channel (native script included) count too.
                        var country = LogoIndex.CountryOf(c) ?? c.Streams.Select(sr => sources.GetValueOrDefault(sr.SourceId)?.Country).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.ToUpperInvariant();
                        var candidates = new List<string> { c.MatchKey };
                        candidates.AddRange(_logoIndex.NamesOf(c.Name, country, c.EpgIds).Select(n => NameNormalizer.Parse(n).Key));
                        foreach (var key in candidates.Distinct(StringComparer.Ordinal))
                        {
                            var suggested = _epg.SuggestIdByName(key, country);
                            if (suggested is not null)
                            {
                                if (!c.EpgIds.Contains(suggested, StringComparer.OrdinalIgnoreCase))
                                {
                                    c.EpgIds.Add(suggested);
                                }

                                break;
                            }
                        }
                    }

                    c.Logo = ChooseLogo(c, raws, sources) ?? c.Logo;
                }
            }

            if (c.Auto && !c.Pinned && c.NameMode == NameMode.Fixed && c.Streams.Count > 1)
            {
                // Several names for one channel (call sign "JOAX-DTV" from a free list, "NIPPON TV" from a provider):
                // prefer the stream whose own key is the canonical one, i.e. the readable name the alias points at.
                var names = c.Streams.Where(sr => parsed.ContainsKey(sr.Ref)).Select(sr => parsed[sr.Ref]).ToList();
                var preferred = names.FirstOrDefault(p => string.Equals(p.RawKey, c.MatchKey, StringComparison.Ordinal)) ?? names.FirstOrDefault();
                if (preferred is not null && names.Any(p => string.Equals(p.DisplayName, c.Name, StringComparison.Ordinal)) && !string.Equals(c.Name, preferred.DisplayName, StringComparison.Ordinal))
                {
                    c.Name = preferred.DisplayName;
                }
            }

            if (c.NameMode == NameMode.FollowPrimary && !c.Pinned)
            {
                var primary = c.Streams.FirstOrDefault(sr => sr.Enabled && byRef.ContainsKey(sr.Ref));
                if (primary is not null)
                {
                    var display = parsed.TryGetValue(primary.Ref, out var pp) ? pp.DisplayName : byRef[primary.Ref].Name;
                    if (!string.Equals(c.Name, display, StringComparison.Ordinal))
                    {
                        c.Name = display;
                    }
                }
            }
        }

        // Channels whose every stream vanished from every source are removed (unless the user pinned them).
        var dropped = lineup.Channels.RemoveAll(c => c.Auto && !c.Pinned && !c.Streams.Any(sr => byRef.ContainsKey(sr.Ref)));

        AssignNumbers(lineup, rules, compiled, byRef);
        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        var summary = $"channels={lineup.Channels.Count} newChannels={newChannels} streamsAttached={added} rematched={rematched} dropped={dropped}";
        _logger.LogDebug("Terebi: {Summary}", summary);
        return summary;
    }

    /// <summary>Applies the rules to a raw stream. First enabled rule whose patterns match wins.</summary>
    public static RuleMatch MatchRule(IReadOnlyList<CategoryRule> rules, IReadOnlyDictionary<string, (Regex? Cat, Regex? Name)> compiled, RawStream s, string? displayName = null)
    {
        displayName ??= NameNormalizer.Parse(s.Name).DisplayName;
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (r.SourceIds.Count > 0 && !r.SourceIds.Contains(s.SourceId, StringComparer.Ordinal))
            {
                continue;
            }

            var (cat, name) = compiled[r.Id];
            if (cat is null && name is null)
            {
                continue;
            }

            // The name pattern is tried on the raw provider name and on the cleaned display name, so
            // "^NEWS$" matches "XX-TAG| NEWS RAW" as well.
            // A playlist without group-title has an empty category; "." style catch-all rules should still apply.
            var category = s.Category.Length == 0 ? " " : s.Category;
            if ((cat is null || cat.IsMatch(category)) && (name is null || name.IsMatch(s.Name) || name.IsMatch(displayName)))
            {
                return new RuleMatch(r, r.Exclude, i);
            }
        }

        return new RuleMatch(null, false);
    }

    /// <summary>
    /// Gives every channel without a number a slot: in its rule's block when a rule matched, otherwise in an
    /// automatic block allocated per Terebi category (first come, then stable across rebuilds). Existing numbers
    /// are never changed here; pinned channels are never touched.
    /// </summary>
    private static void AssignNumbers(Lineup lineup, IReadOnlyList<CategoryRule> rules, IReadOnlyDictionary<string, (Regex? Cat, Regex? Name)> compiled, IReadOnlyDictionary<string, RawStream> byRef)
    {
        var cfg = Plugin.Instance?.Configuration;
        var blockSize = Math.Max(10, cfg?.AutoBlockSize ?? 100);
        var used = new HashSet<int>(lineup.Channels.Where(c => c.Number > 0).Select(c => c.Number));
        var nextBlock = Math.Max(1, cfg?.AutoBlockStart ?? 1000);
        if (lineup.CategoryBlocks.Count > 0)
        {
            nextBlock = Math.Max(nextBlock, lineup.CategoryBlocks.Values.Max() + blockSize);
        }

        foreach (var group in lineup.Channels.Where(c => c.Number <= 0 && !c.Pinned).GroupBy(c => c.Category, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var c in group.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var rule = c.Streams.Select(sr => byRef.GetValueOrDefault(sr.Ref)).Where(r => r is not null).Select(r => MatchRule(rules, compiled, r!)).Where(m => m.Rule is not null && !m.Excluded).OrderBy(m => m.Index).Select(m => m.Rule).FirstOrDefault();
                int start;
                if (rule?.NumberStart > 0)
                {
                    start = rule.NumberStart;
                }
                else
                {
                    if (!lineup.CategoryBlocks.TryGetValue(group.Key, out start))
                    {
                        var needed = Math.Max(blockSize, (int)Math.Ceiling((group.Count() + 5) / (double)blockSize) * blockSize);
                        while (used.Any(u => u >= nextBlock && u < nextBlock + needed))
                        {
                            nextBlock += blockSize;
                        }

                        start = nextBlock;
                        lineup.CategoryBlocks[group.Key] = start;
                        nextBlock += needed;
                    }
                }

                var n = start;
                while (used.Contains(n))
                {
                    n++;
                }

                c.Number = n;
                used.Add(n);
                if (rule is not null && c.Auto && string.IsNullOrEmpty(c.Category))
                {
                    c.Category = rule.Category;
                }
            }
        }
    }
}
