using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetWatchdogEntries;

[ApiController]
[Route("api/get-watchdog-entries")]
public partial class GetWatchdogEntriesController(
    WatchdogLog watchdogLog,
    ConfigManager configManager
) : BaseApiController
{
    [GeneratedRegex(@"\s*\(\d+%\)\s*$")] private static partial Regex PercentSuffixRegex();

    protected override async Task<IActionResult> HandleRequest()
    {
        var limitStr = HttpContext.Request.Query["limit"].ToString();
        var limit = int.TryParse(limitStr, out var n) ? Math.Clamp(n, 1, 500) : 200;

        // Resolve nicknames from current config rather than storing per-row, so
        // a rename retroactively updates older entries and we avoid a schema
        // migration. Case-insensitive on host.
        var nicknamesByHost = configManager.GetUsenetProviderConfig().Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Nickname))
            .GroupBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Nickname, StringComparer.OrdinalIgnoreCase);
        var providersById = configManager.GetUsenetProviderConfig().Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var recent = await watchdogLog.GetRecentAsync(limit, HttpContext.RequestAborted).ConfigureAwait(false);
        var dtos = recent.Select(a => new GetWatchdogEntriesResponse.EntryDto
        {
            ClickId = a.ClickId.ToString(),
            AttemptedAtUnix = a.AttemptedAt.ToUnixTimeSeconds(),
            ContentType = a.ContentType,
            RequestedTitle = a.RequestedTitle,
            CandidateTitle = a.CandidateTitle,
            IndexerName = a.IndexerName,
            Size = a.Size,
            RankIndex = a.RankIndex,
            Outcome = a.Result,
            FailReason = a.FailReason,
            DurationMs = a.DurationMs,
            PrepDurationMs = a.PrepDurationMs,
            HealthDurationMs = a.HealthDurationMs,
            PrepStats = BuildPrepStats(a.PrepStatsJson, providersById),
            HealthStats = BuildHealthStats(a.HealthStatsJson, providersById),
            IsWinner = a.IsWinner,
            ProviderHost = a.ProviderHost,
            ProviderNickname = ResolveNickname(a.ProviderHost, nicknamesByHost),
        }).ToList();

        return Ok(new GetWatchdogEntriesResponse
        {
            Status = true,
            Entries = dtos,
        });
    }

    private static GetWatchdogEntriesResponse.PrepStatsDto? BuildPrepStats(
        string? json,
        IReadOnlyDictionary<string, UsenetProviderConfig.ConnectionDetails> providersById)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        PrepUsageSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<PrepUsageSnapshot>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (snapshot is null) return null;

        return new GetWatchdogEntriesResponse.PrepStatsDto
        {
            FileCount = snapshot.FileCount,
            Connections = snapshot.Connections,
            QueueWaitMs = snapshot.QueueWaitMs,
            FirstSegmentsMs = snapshot.FirstSegmentsMs,
            Par2Ms = snapshot.Par2Ms,
            RarMs = snapshot.RarMs,
            ProcessorsMs = snapshot.ProcessorsMs,
            LazyRarMounted = snapshot.LazyRarMounted,
            FirstSegmentFallbacks = snapshot.FirstSegmentFallbacks,
            LastStage = snapshot.LastStage,
            Providers = snapshot.Providers.Select(stat =>
            {
                providersById.TryGetValue(stat.ProviderId, out var configured);
                return new GetWatchdogEntriesResponse.PrepProviderDto
                {
                    ProviderId = stat.ProviderId,
                    Host = configured?.Host ?? stat.ProviderId,
                    Nickname = configured?.Nickname,
                    Articles = stat.Articles,
                    Bytes = stat.Bytes,
                };
            }).OrderByDescending(x => x.Articles).ThenBy(x => x.Nickname ?? x.Host).ToList(),
        };
    }

    private static GetWatchdogEntriesResponse.HealthStatsDto? BuildHealthStats(
        string? json,
        IReadOnlyDictionary<string, UsenetProviderConfig.ConnectionDetails> providersById)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        HealthCheckUsageSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<HealthCheckUsageSnapshot>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (snapshot is null) return null;

        return new GetWatchdogEntriesResponse.HealthStatsDto
        {
            TotalArticles = snapshot.TotalArticles,
            FoundArticles = snapshot.FoundArticles,
            MissingArticles = snapshot.MissingArticles,
            Providers = snapshot.Providers.Select(stat =>
            {
                providersById.TryGetValue(stat.ProviderId, out var configured);
                return new GetWatchdogEntriesResponse.HealthProviderDto
                {
                    ProviderId = stat.ProviderId,
                    Host = configured?.Host ?? stat.Host,
                    Nickname = configured?.Nickname,
                    Preferred = stat.Preferred,
                    ProbeFound = stat.ProbeFound,
                    ProbeReceived = stat.ProbeReceived,
                    ProbeStatus = stat.ProbeStatus,
                    Batches = stat.Batches,
                    Attempted = stat.Attempted,
                    Received = stat.Received,
                    Found = stat.Found,
                    Missing = stat.Missing,
                    Failures = stat.Failures,
                    WorkMs = stat.WorkMs,
                    Rate = stat.Rate,
                };
            }).OrderByDescending(x => x.Found).ThenBy(x => x.Nickname ?? x.Host).ToList(),
        };
    }

    // ProviderHost can be a single host, or a comma-separated formatted string
    // like "news.eweka.nl (60%), news.frugal.com (40%)" — see QueueItemProcessor.FormatProviders.
    // Returns a joined display string where each host is replaced with its
    // nickname (or kept as-is when no nickname is configured). Returns null
    // when no part has a nickname, so the frontend can fall back to its own
    // formatProviderShort for the host string.
    private static string? ResolveNickname(string? providerHost, IReadOnlyDictionary<string, string?> nicknamesByHost)
    {
        if (string.IsNullOrWhiteSpace(providerHost) || nicknamesByHost.Count == 0) return null;
        var parts = providerHost.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var anyMatched = false;
        var rendered = parts.Select(part =>
        {
            var pctMatch = PercentSuffixRegex().Match(part);
            var host = pctMatch.Success ? part[..pctMatch.Index].Trim() : part;
            var suffix = pctMatch.Success ? pctMatch.Value : string.Empty;
            if (nicknamesByHost.TryGetValue(host, out var nick) && !string.IsNullOrWhiteSpace(nick))
            {
                anyMatched = true;
                return nick + suffix;
            }
            return part;
        });
        return anyMatched ? string.Join(", ", rendered) : null;
    }
}
