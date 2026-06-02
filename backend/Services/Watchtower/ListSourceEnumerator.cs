using System.Text.Json;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class ListSourceEnumerator
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<WtContentRef>> EnumerateAsync(ListSource source, CancellationToken ct)
    {
        return source.Kind switch
        {
            ListSource.KindStremioCatalog => await FetchStremioCatalogAsync(source.Url, ct).ConfigureAwait(false),
            ListSource.KindUrlList => await FetchUrlListAsync(source.Url, ct).ConfigureAwait(false),
            _ => Array.Empty<WtContentRef>(),
        };
    }

    private static async Task<IReadOnlyList<WtContentRef>> FetchStremioCatalogAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<WtContentRef>();
        var json = await HttpGetStringAsync(url!, ct).ConfigureAwait(false);
        if (json is null) return Array.Empty<WtContentRef>();
        var refs = new List<WtContentRef>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("metas", out var metas) && metas.ValueKind == JsonValueKind.Array)
            {
                foreach (var meta in metas.EnumerateArray())
                {
                    var type = GetStr(meta, "type");
                    var id = GetStr(meta, "id");
                    if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id)) continue;
                    refs.Add(new WtContentRef { Type = NormalizeType(type!), ContentId = id!, Title = GetStr(meta, "name") });
                }
            }
        }
        catch (Exception e)
        {
            Log.Debug(e, "Watchtower: failed to parse stremio catalog {Url}", url);
        }
        return refs;
    }

    private static async Task<IReadOnlyList<WtContentRef>> FetchUrlListAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<WtContentRef>();
        var body = await HttpGetStringAsync(url!, ct).ConfigureAwait(false);
        if (body is null) return Array.Empty<WtContentRef>();

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            var fromJson = TryParseJsonList(trimmed);
            if (fromJson.Count > 0) return fromJson;
        }

        var refs = new List<WtContentRef>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var (type, id) = SplitTypeId(line);
            if (id.Length == 0) continue;
            refs.Add(new WtContentRef { Type = type, ContentId = id });
        }
        return refs;
    }

    private static List<WtContentRef> TryParseJsonList(string json)
    {
        var refs = new List<WtContentRef>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : (doc.RootElement.TryGetProperty("items", out var items) ? items : default);
            if (arr.ValueKind != JsonValueKind.Array) return refs;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var (type, id) = SplitTypeId(el.GetString() ?? "");
                    if (id.Length > 0) refs.Add(new WtContentRef { Type = type, ContentId = id });
                }
                else if (el.ValueKind == JsonValueKind.Object)
                {
                    var id = GetStr(el, "id") ?? GetStr(el, "imdb") ?? "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    refs.Add(new WtContentRef
                    {
                        Type = NormalizeType(GetStr(el, "type") ?? "movie"),
                        ContentId = id,
                        Title = GetStr(el, "name") ?? GetStr(el, "title"),
                    });
                }
            }
        }
        catch
        {
        }
        return refs;
    }

    private static (string Type, string Id) SplitTypeId(string line)
    {
        if (line.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && !line.Contains(':'))
            return ("movie", line);

        var firstColon = line.IndexOf(':');
        if (firstColon > 0)
        {
            var maybeType = line[..firstColon].ToLowerInvariant();
            if (maybeType is "movie" or "series" or "tv" or "show")
                return (NormalizeType(maybeType), line[(firstColon + 1)..]);
        }
        return ("movie", line);
    }

    private static string NormalizeType(string type)
    {
        type = type.Trim().ToLowerInvariant();
        return type is "series" or "tv" or "show" ? "series" : "movie";
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<string?> HttpGetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = ProxyHttpClientPool.GetClient(null);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "NzbDav-Watchtower");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FetchTimeout);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Watchtower: list fetch failed for {Url}", url);
            return null;
        }
    }
}
