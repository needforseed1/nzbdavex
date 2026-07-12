using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Api.SabControllers.AddUrl;

public class AddUrlRequest() : AddFileRequest
{
    private const int MaxAutomaticRedirections = 10;
    public static async Task<AddUrlRequest> New(
        HttpContext context,
        ConfigManager configManager,
        IndexerHitTracker hitTracker,
        NewznabRateLimiter rateLimiter)
    {
        var nzbUrl = context.GetRequestParam("name");
        var nzbName = context.GetRequestParam("nzbname");
        var indexerConfig = configManager.GetIndexerConfig();
        var matchedIndexer = MatchIndexerByHost(nzbUrl, indexerConfig);

        var userAgent = IndexerConfig.PerIndexerRetrieveUserAgent(matchedIndexer) ?? configManager.GetUserAgent();
        var proxyUrl = StringUtil.EmptyToNull(matchedIndexer?.ProxyUrl) ?? indexerConfig.ProxyUrl;

        if (matchedIndexer is not null)
        {
            await rateLimiter
                .WaitAsync(matchedIndexer.Id, matchedIndexer.MaxRequestsPerMinute, context.RequestAborted)
                .ConfigureAwait(false);
            var hitCheck = await hitTracker
                .ReserveAsync(matchedIndexer.Id, IndexerApiHit.HitType.Download,
                    matchedIndexer.DownloadLimit, matchedIndexer.HitLimitResetTime, context.RequestAborted)
                .ConfigureAwait(false);
            if (hitCheck is { Allowed: false })
            {
                var reason = IndexerHitTracker.FormatSkipReason(hitCheck, IndexerApiHit.HitType.Download);
                Log.Warning("AddUrl rejected for {Indexer}: {Reason}", matchedIndexer.Name, reason);
                throw new BadHttpRequestException($"Indexer {matchedIndexer.Name} {reason}");
            }
        }

        var timeoutSeconds = matchedIndexer is null
            ? IndexerConfig.DefaultTimeoutSeconds
            : indexerConfig.GetEffectiveTimeoutSeconds(matchedIndexer);
        var nzbFile = await GetNzbFile(
            nzbUrl, nzbName, userAgent, proxyUrl,
            TimeSpan.FromSeconds(timeoutSeconds), context.RequestAborted).ConfigureAwait(false);
        return new AddUrlRequest()
        {
            FileName = nzbFile.FileName,
            ContentType = nzbFile.ContentType,
            NzbFileStream = nzbFile.FileStream,
            Category = context.GetRequestParam("cat") ?? configManager.GetManualUploadCategory(),
            Priority = MapPriorityOption(context.GetRequestParam("priority")),
            PostProcessing = MapPostProcessingOption(context.GetRequestParam("pp")),
            CancellationToken = context.RequestAborted
        };
    }

    private static IndexerConfig.ConnectionDetails? MatchIndexerByHost(string? url, IndexerConfig config)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri)) return null;
        var host = requestUri.Host;
        if (string.IsNullOrEmpty(host)) return null;

        foreach (var indexer in config.Indexers)
        {
            if (!indexer.Enabled) continue;
            if (!Uri.TryCreate(indexer.Url, UriKind.Absolute, out var indexerUri)) continue;
            if (string.Equals(indexerUri.Host, host, StringComparison.OrdinalIgnoreCase))
                return indexer;
        }
        return null;
    }

    private static async Task<NzbFileResponse> GetNzbFile(
        string? url,
        string? nzbName,
        string userAgent,
        string? proxyUrl,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            // validate url
            if (string.IsNullOrWhiteSpace(url))
                throw new Exception($"The url is invalid.");

            // fetch url
            var response = await GetAsync(url, userAgent, proxyUrl, timeout, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Received status code {response.StatusCode}.");

            // read the content type
            var contentType = response.Content.Headers.ContentType?.MediaType;

            // determine the filename
            var fileName = AddNzbExtension(nzbName)
                           ?? GetFilenameFromResponseHeader(response)
                           ?? GetFilenameFromUrl(url)
                           ?? throw new Exception("Nzb filename could not be determined.");

            // read the file contents
            var fileStream = await response.Content.ReadAsStreamAsync();

            // return response
            return new NzbFileResponse
            {
                FileName = fileName,
                ContentType = contentType,
                FileStream = fileStream
            };
        }
        catch (Exception ex)
        {
            throw new BadHttpRequestException($"Failed to fetch nzb-file url `{url}`: {ex.Message}");
        }
    }

    private static string? AddNzbExtension(string? nzbName)
    {
        return nzbName == null ? null
            : nzbName.ToLower().EndsWith("nzb") ? nzbName
            : $"{nzbName}.nzb";
    }

    private static async Task<HttpResponseMessage> GetAsync(
        string url,
        string userAgent,
        string? proxyUrl,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var httpClient = ProxyHttpClientPool.GetClient(proxyUrl);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var currentUri = new Uri(url);
        var response = await SendGetAsync(httpClient, url, userAgent, cts.Token).ConfigureAwait(false);
        var remainingRedirects = MaxAutomaticRedirections;
        while
        (
            (int)response.StatusCode is >= 300 and < 400
            && remainingRedirects > 0
            && response.Headers.Location is not null
            && EnvironmentUtil.IsVariableTrue("ALLOW_HTTPS_TO_HTTP_REDIRECTS")
        )
        {
            var redirect = response.Headers.Location;
            var redirectUri = redirect.IsAbsoluteUri ? redirect : new Uri(currentUri, redirect);
            HttpResponseMessage nextResponse;
            try
            {
                nextResponse = await SendGetAsync(
                    httpClient, redirectUri.ToString(), userAgent, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
            response = nextResponse;
            currentUri = redirectUri;
            remainingRedirects--;
        }

        return response;
    }

    private static Task<HttpResponseMessage> SendGetAsync(HttpClient client, string url, string userAgent, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return client.SendAsync(req, ct);
    }

    private static string? GetFilenameFromResponseHeader(HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var filename = contentDisposition?.FileName?.Trim('"');
        return StringUtil.EmptyToNull(filename);
    }

    private static string? GetFilenameFromUrl(string url)
    {
        try
        {
            var filename = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(filename)) return null;
            filename = Uri.UnescapeDataString(filename);
            filename = AddNzbExtension(filename);
            return filename;
        }
        catch
        {
            return null;
        }
    }

    private class NzbFileResponse
    {
        public required string FileName { get; init; }
        public required string? ContentType { get; init; }
        public required Stream FileStream { get; init; }
    }
}
