using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;

namespace NzbWebDAV.Services;

public class ExternalIdResolver
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string KitsuBase = "https://kitsu.io/api/edge";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public sealed record IdMapping(bool IsMovie, int? TvdbId, string? ImdbId, int Season);

    public async Task<IdMapping?> ResolveAsync(string provider, long externalId, CancellationToken ct)
    {
        var key = $"{provider}:{externalId}";
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return entry.Mapping;

        IdMapping? mapping = null;
        try
        {
            var sourceId = provider switch
            {
                "kitsu" => externalId,
                "mal" => await LookupSourceIdByExternalAsync("myanimelist/anime", externalId, ct).ConfigureAwait(false),
                "anilist" => await LookupSourceIdByExternalAsync("anilist/anime", externalId, ct).ConfigureAwait(false),
                _ => (long?)null,
            };
            if (sourceId is { } s) mapping = await FetchMappingAsync(s, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning("ExternalIdResolver {Provider}:{Id} lookup failed: {Message}", provider, externalId, ex.Message);
        }

        _cache[key] = new CacheEntry(mapping, DateTimeOffset.UtcNow.Add(mapping is null ? NegativeTtl : CacheTtl));
        return mapping;
    }

    private static async Task<long?> LookupSourceIdByExternalAsync(string externalSite, long externalId, CancellationToken ct)
    {
        var url = $"{KitsuBase}/mappings"
                  + $"?filter%5BexternalSite%5D={Uri.EscapeDataString(externalSite)}"
                  + $"&filter%5BexternalId%5D={externalId}"
                  + "&include=item&fields%5Banime%5D=id&page%5Blimit%5D=1";
        using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("included", out var included)) return null;
        if (included.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in included.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var t) && t.GetString() == "anime"
                && item.TryGetProperty("id", out var idEl) && long.TryParse(idEl.GetString(), out var id))
                return id;
        }
        return null;
    }

    private static async Task<IdMapping?> FetchMappingAsync(long sourceId, CancellationToken ct)
    {
        var url = $"{KitsuBase}/anime/{sourceId}"
                  + "?include=mappings"
                  + "&fields%5Banime%5D=subtype,mappings"
                  + "&fields%5Bmappings%5D=externalSite,externalId";
        using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        bool isMovie = false;
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("attributes", out var attrs)
            && attrs.TryGetProperty("subtype", out var subtype))
        {
            isMovie = string.Equals(subtype.GetString(), "movie", StringComparison.OrdinalIgnoreCase);
        }

        int? tvdbId = null;
        string? imdbId = null;
        int season = 1;
        if (doc.RootElement.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in included.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var t) || t.GetString() != "mappings") continue;
                if (!item.TryGetProperty("attributes", out var mAttrs)) continue;
                var site = mAttrs.TryGetProperty("externalSite", out var s) ? s.GetString() : null;
                var ext = mAttrs.TryGetProperty("externalId", out var e) ? e.GetString() : null;
                if (site is null || ext is null) continue;

                if (site == "imdb" && imdbId is null)
                {
                    imdbId = ext.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? ext : $"tt{ext}";
                }
                else if (site.Equals("thetvdb", StringComparison.OrdinalIgnoreCase))
                {
                    // ext is "<seriesId>" or "<seriesId>/<season>" — canonical season source
                    var slashIdx = ext.IndexOf('/');
                    if (slashIdx > 0)
                    {
                        if (int.TryParse(ext[..slashIdx], out var id)) tvdbId ??= id;
                        if (int.TryParse(ext[(slashIdx + 1)..], out var seasonNum)) season = seasonNum;
                    }
                    else if (int.TryParse(ext, out var id)) tvdbId ??= id;
                }
                else if (site.Equals("thetvdb/series", StringComparison.OrdinalIgnoreCase))
                {
                    // fallback id source; never carries season
                    if (int.TryParse(ext, out var id)) tvdbId ??= id;
                }
                // ignore "thetvdb/season" — its ext is the upstream row id, not a season number
            }
        }

        if (tvdbId is null && imdbId is null) return null;
        return new IdMapping(isMovie, tvdbId, imdbId, season);
    }

    private sealed record CacheEntry(IdMapping? Mapping, DateTimeOffset ExpiresAt);
}
