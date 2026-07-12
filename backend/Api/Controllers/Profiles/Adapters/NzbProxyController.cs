using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Api.Controllers.Profiles.Adapters;

[ApiController]
[Route("api/search/{token}/nzb/{playToken}.nzb")]
public class NzbProxyController(
    SearchProfileService searchService,
    NzbResolutionCache cache,
    NzbFetchCoalescer nzbFetchCoalescer,
    ConfigManager configManager,
    NewznabRateLimiter rateLimiter,
    IndexerHitTracker hitTracker
) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string token, string playToken)
    {
        var profile = searchService.GetProfile(token);
        if (profile is null) return NotFound();
        if (!searchService.IsAdapterEnabled(token, "json")
            && !searchService.IsAdapterEnabled(token, "newznab"))
        {
            return NotFound();
        }

        var entry = cache.Get(playToken);
        if (entry is null) return NotFound("Link expired. Re-search to refresh.");
        if (entry.ProfileToken != token) return NotFound();

        var candidate = entry.Primary;
        if (string.IsNullOrWhiteSpace(candidate.NzbUrl)) return NotFound();
        var indexerConfig = configManager.GetIndexerConfig();
        var indexer = indexerConfig.Indexers.FirstOrDefault(x =>
            x.Enabled && (string.Equals(x.Id, candidate.IndexerId, StringComparison.OrdinalIgnoreCase)
                          || (string.IsNullOrWhiteSpace(candidate.IndexerId)
                              && string.Equals(x.Name, candidate.IndexerName, StringComparison.OrdinalIgnoreCase))));
        if (indexer is null)
            return StatusCode(410, "The source indexer is no longer enabled or configured.");
        var fetchTimeout = TimeSpan.FromSeconds(indexerConfig.GetEffectiveTimeoutSeconds(indexer));

        byte[]? bytes;
        try
        {
            bytes = await nzbFetchCoalescer.GetOrFetchAsync(candidate.NzbUrl, async innerCt =>
            {
                await rateLimiter
                    .WaitAsync(indexer.Id, indexer.MaxRequestsPerMinute, innerCt)
                    .ConfigureAwait(false);
                var hitCheck = await hitTracker
                    .ReserveAsync(indexer.Id, IndexerApiHit.HitType.Download,
                        indexer.DownloadLimit, indexer.HitLimitResetTime, innerCt)
                    .ConfigureAwait(false);
                if (hitCheck is { Allowed: false })
                    throw new IndexerDownloadLimitException(hitCheck.ResetAt);

                using var req = new HttpRequestMessage(HttpMethod.Get, candidate.NzbUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", candidate.IndexerUserAgent);
                var client = ProxyHttpClientPool.GetClient(candidate.ProxyUrl);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                cts.CancelAfter(fetchTimeout);
                using var resp = await client
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (IndexerDownloadLimitException e)
        {
            Response.Headers.RetryAfter = Math.Max(1,
                (int)Math.Ceiling((e.ResetAt - DateTimeOffset.UtcNow).TotalSeconds)).ToString();
            return StatusCode(429,
                "The source indexer's configured NZB download limit has been reached.");
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception e)
        {
            Log.Debug(e, "NZB proxy fetch failed for {Url}", candidate.NzbUrl);
            return StatusCode(502, "Failed to fetch NZB from source indexer.");
        }

        if (bytes is null)
        {
            return StatusCode(502, "Source indexer failed to return the NZB.");
        }

        Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{SanitizeFileName(candidate.Title)}.nzb\"";
        return File(bytes, "application/x-nzb");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "untitled" : clean;
    }

    private sealed class IndexerDownloadLimitException(DateTimeOffset resetAt) : Exception
    {
        public DateTimeOffset ResetAt { get; } = resetAt;
    }
}
