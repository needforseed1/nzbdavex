using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using static NzbWebDAV.Api.Controllers.GetPlaybackSessions.GetPlaybackSessionsResponse;

namespace NzbWebDAV.Api.Controllers.GetPlaybackSessions;

/// <summary>
/// Turns raw ReadSession rows into what the playback page shows. Kept free of
/// database and HTTP concerns so the grouping and issue rules can be tested
/// directly.
/// </summary>
public static class PlaybackHistory
{
    /// <summary>
    /// A pause longer than this is treated as a new play rather than a gap in the
    /// same one. Players routinely leave 15 s–minutes between range requests.
    /// </summary>
    public static readonly TimeSpan DefaultPlayGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Tiny successful reads are observable, but their caller's intent is not.
    /// They commonly come from header or metadata inspection and are separated
    /// from viewing by how little they took: real viewing pulls tens of megabytes
    /// within seconds. Duration is not useful here because a probe can hold a
    /// file open while transferring very little.
    /// </summary>
    internal const long ProbeMaxBytesServed = 8_000_000;

    // rclone hides the process reading its shared filesystem mount. These
    // thresholds only classify patterns with strong evidence and leave
    // ambiguous reads unclassified inside the mount-activity view.
    // Classification never deletes a row.
    private const double BackgroundTailProbeMaxFileFraction = 0.03;
    private const double BackgroundTailProbeMinReachedFraction = 0.95;
    private const int BackgroundTailProbeMinSessions = 3;
    private static readonly TimeSpan BackgroundTailProbeMaxActiveTime = TimeSpan.FromMinutes(2);
    private const double BackgroundBulkReadMinFileFraction = 0.75;
    private const double BackgroundBulkReadMinSourceUtilization = 0.80;
    private static readonly TimeSpan BackgroundBulkReadMinActiveTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackgroundBulkReadMaxStartSkew = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BackgroundBulkReadMinOverlap = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackgroundBulkReadContinuationGap = TimeSpan.FromMinutes(30);
    private const int ImportInspectionMinRelatedFiles = 3;
    private static readonly TimeSpan ImportInspectionWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ImportInspectionMaxStartSkew = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ImportInspectionSymlinkMaxGap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ImportInspectionSingleMaxActiveTime = TimeSpan.FromMinutes(2);
    private const double ImportInspectionSingleMaxFileFraction = 0.03;
    private const double ImportInspectionSingleMinReachedFraction = 0.95;
    private const int ImportInspectionSingleMaxRequests = 8;
    private static readonly TimeSpan AnalysisProbeMaxActiveTime = TimeSpan.FromSeconds(5);
    private const double AnalysisProbeMinReachedFraction = 0.95;
    private const int AnalysisProbeMinRequests = 10;
    private static readonly string[] RecognizedPlaybackUserAgentMarkers =
    [
        "infuse",
        "vlc",
        "kodi",
        "xbmc",
        "stremio",
        "mpv",
        "exoplayer",
        "applecoremedia",
        "avplayer",
    ];

    private static readonly JsonSerializerOptions ProviderStatsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static class Issue
    {
        /// <summary>
        /// Usenet stopped delivery long enough to pose a plausible playback
        /// risk. Deliberately *not* raised for downstream waits: those are the
        /// client refusing more data because its buffer is full, which is what
        /// healthy playback looks like, not buffering.
        /// </summary>
        public const string Stalled = "stalled";

        /// <summary>Plex reported buffering or stopped progress during a source wait.</summary>
        public const string Buffering = "buffering";

        /// <summary>
        /// Articles that could not be fetched were served as zeros. The only
        /// issue here that means the viewer got wrong data rather than late data.
        /// </summary>
        public const string Corrupted = "corrupted";

        /// <summary>
        /// A provider connection went silent mid-body and the segment had to be
        /// fetched again. Recovered — but a fault that heals leaves no other mark.
        /// </summary>
        public const string BodyStalled = "body-stalled";

        public const string Rescued = "rescued";
        public const string BackupUsed = "backup-used";
        public const string Rotated = "rotated";
        public const string BudgetExhausted = "budget-exhausted";
        public const string PoolStarved = "pool-starved";
        public const string PermitStarved = "permit-starved";
        public const string Aborted = "aborted";
        public const string TimedOut = "timeout";
        public const string Errored = "error";
    }

    /// <summary>
    /// Signals that could have affected the viewer. Recovery mechanics remain
    /// in the session diagnostics, but successful fallback, connection
    /// replacement, and internal queueing do not turn a play into a problem.
    /// </summary>
    private static readonly string[] PlaybackImpactIssues =
    [
        Issue.Corrupted,
        Issue.Buffering,
        Issue.Stalled,
        Issue.TimedOut,
        Issue.Errored,
    ];

    public static SessionDto BuildSession(
        ReadSession row,
        IReadOnlyDictionary<string, UsenetProviderConfig.ConnectionDetails> providersById)
    {
        var counters = new CountersDto
        {
            UpstreamStalls = row.UpstreamStalls,
            MaxUpstreamStallMs = row.MaxUpstreamStallMs,
            TotalUpstreamStallMs = row.TotalUpstreamStallMs,
            UpstreamWaitWallMs = row.UpstreamWaitWallMs,
            MaxUpstreamWaitWallMs = row.MaxUpstreamWaitWallMs,
            HeadOfLineStalls = row.HeadOfLineStalls,
            TotalHeadOfLineStallMs = row.TotalHeadOfLineStallMs,
            DownstreamStalls = row.DownstreamStalls,
            MaxDownstreamStallMs = row.MaxDownstreamStallMs,
            TotalDownstreamStallMs = row.TotalDownstreamStallMs,
            FallbackRescues = row.FallbackRescues,
            ProviderRotations = row.ProviderRotations,
            FallbackBudgetExhaustions = row.FallbackBudgetExhaustions,
            CacheHits = row.CacheHits,
            CacheMisses = row.CacheMisses,
            ConnectionPermitWaits = row.ConnectionPermitWaits,
            MaxConnectionPermitWaitMs = row.MaxConnectionPermitWaitMs,
            ProviderPoolWaits = row.ProviderPoolWaits,
            MaxProviderPoolWaitMs = row.MaxProviderPoolWaitMs,
            FailoverSaves = row.FailoverSaves,
            ZeroFilledSegments = row.ZeroFilledSegments,
            ZeroFilledBytes = row.ZeroFilledBytes,
            BodyStallRecoveries = row.BodyStallRecoveries,
        };
        var providers = BuildProviders(row.ProviderStatsJson, providersById);
        var endReason = row.EndReason.ToString().ToLowerInvariant();

        return new SessionDto
        {
            Id = row.Id.ToString(),
            Path = row.Path,
            FileName = row.FileName,
            DavItemId = row.DavItemId?.ToString(),
            HistoryItemId = row.HistoryItemId?.ToString(),
            ClientIp = row.ClientIp,
            ClientUserAgent = row.ClientUserAgent,
            StartedAtUnix = row.StartedAt / 1000,
            EndedAtUnix = row.EndedAt / 1000,
            StartedAtMs = row.StartedAt,
            EndedAtMs = row.EndedAt,
            DurationMs = row.DurationMs,
            RequestCount = row.RequestCount,
            BytesServed = row.BytesServed,
            BytesFetched = row.BytesFetched,
            FileSize = row.FileSize,
            MaxOffset = row.MaxOffset,
            FirstByteMs = row.FirstByteMs,
            EndReason = endReason,
            ErrorNote = row.ErrorNote,
            HasDiagnostics = row.RequestCount > 0 || row.ProviderStatsJson is not null,
            PlexPurpose = row.PlexPurpose,
            PlexConfidence = row.PlexConfidence,
            PlexProduct = row.PlexProduct,
            PlexPlayer = row.PlexPlayer,
            PlexPlatform = row.PlexPlatform,
            PlexRatingKey = row.PlexRatingKey,
            PlexDetail = row.PlexDetail,
            PlexIsTranscode = row.PlexIsTranscode,
            PlexPlaybackImpact = row.PlexPlaybackImpact,
            Issues = DescribeIssues(
                counters,
                endReason,
                providers,
                row.DurationMs,
                row.PlexPlaybackImpact),
            Counters = counters,
            Providers = providers,
        };
    }

    public static List<ProviderDto> BuildProviders(
        string? providerStatsJson,
        IReadOnlyDictionary<string, UsenetProviderConfig.ConnectionDetails> providersById)
    {
        if (string.IsNullOrWhiteSpace(providerStatsJson)) return [];

        List<PlaybackProviderStat>? stats;
        try
        {
            stats = JsonSerializer.Deserialize<List<PlaybackProviderStat>>(
                providerStatsJson, ProviderStatsJsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }
        if (stats is null) return [];

        return stats
            .Where(x => !string.IsNullOrWhiteSpace(x.ProviderId))
            .Select(x =>
            {
                providersById.TryGetValue(x.ProviderId, out var configured);
                return new ProviderDto
                {
                    ProviderId = x.ProviderId,
                    Host = configured?.Host ?? x.ProviderId,
                    Nickname = configured?.Nickname,
                    Segments = x.Segments,
                    Bytes = x.Bytes,
                    Attempts = x.Attempts,
                    Rescued = x.Rescued,
                    Missing = x.Missing,
                    Timeouts = x.Timeouts,
                    Errors = x.Errors,
                    // The recorded flag covers providers observed by the
                    // explicit fallback path. Provider usage can also be
                    // attributed without that path (for example, a backup
                    // taking over a prefetched pipeline), so honor the current
                    // configured role as well. Resolving this at render time
                    // also corrects existing history rows.
                    IsBackup = x.IsBackup ||
                        configured?.Type is ProviderType.BackupAndStats
                            or ProviderType.BackupOnly,
                };
            })
            .OrderByDescending(x => x.Segments)
            .ThenByDescending(x => x.Rescued)
            .ThenBy(x => x.Nickname ?? x.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<string> DescribeIssues(
        CountersDto counters,
        string endReason,
        IReadOnlyList<ProviderDto> providers,
        long activeMs = 0,
        string? plexPlaybackImpact = null)
    {
        var issues = new List<string>();
        var pressure = PlaybackOutcomeClassifier.ClassifySourcePressure(
            counters.UpstreamStalls,
            counters.MaxUpstreamStallMs,
            counters.ConnectionPermitWaits,
            counters.MaxConnectionPermitWaitMs,
            counters.ProviderPoolWaits,
            counters.MaxProviderPoolWaitMs);
        // Substituted bytes first: everything else on this list is about the
        // stream being slow, this one is about it being wrong.
        if (counters.ZeroFilledSegments > 0) issues.Add(Issue.Corrupted);
        if (counters.BodyStallRecoveries > 0) issues.Add(Issue.BodyStalled);
        var legacyWaitWallMs = counters.UpstreamStalls > 0
            ? Math.Min(Math.Max(0, activeMs), counters.TotalUpstreamStallMs)
            : 0;
        var waitWallMs = counters.UpstreamWaitWallMs > 0
            ? counters.UpstreamWaitWallMs
            : legacyWaitWallMs;
        var maxWaitWallMs = counters.MaxUpstreamWaitWallMs > 0
            ? counters.MaxUpstreamWaitWallMs
            : counters.MaxUpstreamStallMs;
        var hasNoRecordedWaitDuration = counters.UpstreamStalls > 0
                                        && counters.TotalUpstreamStallMs <= 0
                                        && counters.UpstreamWaitWallMs <= 0;
        if (plexPlaybackImpact is "buffering-observed" or "progress-stalled")
            issues.Add(Issue.Buffering);
        else if (plexPlaybackImpact != "progress-continued"
                 && (PlaybackIssueThresholds.PlaybackRisk(
                         activeMs,
                         waitWallMs,
                         maxWaitWallMs)
                     // Rows written before wait durations were recorded can
                     // only use the former count/maximum rule. Do not erase a
                     // warning merely because the migration defaulted their
                     // new wall-clock columns to zero.
                     || (hasNoRecordedWaitDuration && pressure.Stalled)))
            issues.Add(Issue.Stalled);
        if (counters.FallbackRescues > 0 || counters.FailoverSaves > 0 ||
            providers.Any(p => p.Rescued > 0))
            issues.Add(Issue.Rescued);
        if (providers.Any(p => p is { IsBackup: true, Segments: > 0 }))
            issues.Add(Issue.BackupUsed);
        if (counters.ProviderRotations > 0) issues.Add(Issue.Rotated);
        if (counters.FallbackBudgetExhaustions > 0) issues.Add(Issue.BudgetExhausted);
        if (pressure.ProviderPoolStarved) issues.Add(Issue.PoolStarved);
        if (pressure.ConnectionPermitStarved) issues.Add(Issue.PermitStarved);

        switch (endReason)
        {
            case "aborted": issues.Add(Issue.Aborted); break;
            case "timeout": issues.Add(Issue.TimedOut); break;
            case "error": issues.Add(Issue.Errored); break;
        }

        return issues;
    }

    /// <summary>
    /// Groups sessions of the same file and client into plays, splitting whenever
    /// the viewer was away longer than <paramref name="gap"/>. Returns newest
    /// first. <paramref name="resolveContent"/> supplies display names the metrics
    /// database does not hold.
    /// </summary>
    public static List<PlayDto> GroupIntoPlays(
        IEnumerable<SessionDto> sessions,
        Func<SessionDto, PlaybackContentInfo?>? resolveContent = null,
        TimeSpan? gap = null)
    {
        var gapMs = (long)(gap ?? DefaultPlayGap).TotalMilliseconds;
        var ordered = sessions.OrderBy(x => x.StartedAtMs).ThenBy(x => x.Id).ToList();
        var openPlays = new Dictionary<string, List<SessionDto>>(StringComparer.Ordinal);
        var completed = new List<List<SessionDto>>();

        foreach (var session in ordered)
        {
            var key = BuildGroupKey(session);
            if (openPlays.TryGetValue(key, out var current))
            {
                var previousEnd = current.Max(x => x.EndedAtMs);
                if (session.StartedAtMs - previousEnd <= gapMs)
                {
                    current.Add(session);
                    continue;
                }
                completed.Add(current);
            }

            openPlays[key] = [session];
        }

        completed.AddRange(openPlays.Values);

        var plays = completed
            .Select(group => BuildPlay(group, resolveContent))
            .OrderByDescending(x => x.StartedAtUnix)
            .ThenByDescending(x => x.EndedAtUnix)
            .ToList();
        ClassifyLikelyBackgroundActivity(plays);
        return plays;
    }

    public static bool MatchesFilter(PlayDto play, string? filter) => filter?.ToLowerInvariant() switch
    {
        null or "" or "all" => true,
        "playback" or "plays" => play.IsReliablePlayback || play.IsPlexPlayback,
        "probes" or "scans" => play.IsProbe,
        "mount" or "rclone" => play.IsRcloneActivity,
        "other" or "other-direct" =>
            !play.IsRcloneActivity && !play.IsProbe && !play.IsReliablePlayback,
        "issues" => play.Issues.Any(x => PlaybackImpactIssues.Contains(x)),
        "failed" => play.EndReason is "timeout" or "error",
        _ => true,
    };

    /// <summary>
    /// A tiny successful read whose intent cannot be observed. Anything with a
    /// viewer-impact signal stays out of this bucket no matter how little it
    /// served — a stream that died after 20 KB is the most interesting row.
    /// </summary>
    private static bool IsProbe(long bytesServed, string endReason, IReadOnlyList<string> issues) =>
        bytesServed < ProbeMaxBytesServed
        && endReason is not ("timeout" or "error")
        && !issues.Any(x => PlaybackImpactIssues.Contains(x));

    private static string BuildGroupKey(SessionDto session) => string.Join(
        '\n',
        session.DavItemId ?? session.Path,
        session.ClientIp ?? "",
        ClientIdentityKey(session.ClientUserAgent));

    private static string ClientIdentityKey(string? userAgent)
    {
        // One Android playback can switch between the platform HTTP stack and
        // media stack. Treat that observed pair as one identity, but do not
        // merge arbitrary agents: two real players on the same address must
        // still produce two plays.
        if (userAgent?.Contains("dalvik", StringComparison.OrdinalIgnoreCase) == true ||
            userAgent?.Contains("stagefright", StringComparison.OrdinalIgnoreCase) == true)
            return "direct:android-framework";

        return IsRcloneUserAgent(userAgent)
            ? $"rclone:{userAgent}"
            : $"direct:{userAgent}";
    }

    private static PlayDto BuildPlay(
        List<SessionDto> group,
        Func<SessionDto, PlaybackContentInfo?>? resolveContent)
    {
        var first = group[0];
        var last = group[^1];
        var startedMs = group.Min(x => x.StartedAtMs);
        var endedMs = group.Max(x => x.EndedAtMs);
        var watchedMs = group.Sum(x => (long)x.DurationMs);
        var bytesServed = group.Sum(x => x.BytesServed);
        var fileSize = group.Select(x => x.FileSize).FirstOrDefault(x => x is > 0);
        var maxOffset = group.Max(x => x.MaxOffset);
        var content = resolveContent?.Invoke(last) ?? resolveContent?.Invoke(first);
        var counters = MergeCounters(group.Select(x => x.Counters));
        var providers = MergeProviders(group.SelectMany(x => x.Providers));
        var plexPlaybackImpact = group
            .Select(session => session.PlexPlaybackImpact)
            .Where(impact => !string.IsNullOrWhiteSpace(impact))
            .OrderBy(PlaybackImpactPriority)
            .FirstOrDefault();
        var issues = DescribeIssues(
            counters,
            last.EndReason,
            providers,
            watchedMs,
            plexPlaybackImpact);
        var isProbe = IsProbe(bytesServed, last.EndReason, issues);
        var isRcloneActivity = IsRcloneUserAgent(first.ClientUserAgent);
        var isSymlinkResolution = isRcloneActivity && group.All(session =>
            (session.FileName ?? System.IO.Path.GetFileName(session.Path))
            .EndsWith(".rclonelink", StringComparison.OrdinalIgnoreCase));
        var plex = group
            .Where(IsUsefulPlexAttribution)
            .OrderByDescending(session =>
                session.PlexConfidence == "exact-path" ? 1 : 0)
            .ThenByDescending(session => session.BytesServed)
            .ThenByDescending(session => session.StartedAtMs)
            .FirstOrDefault();
        var representativeClient = group
            .GroupBy(x => x.ClientUserAgent ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(sessionsForAgent => new
            {
                Session = sessionsForAgent
                    .OrderByDescending(x => x.BytesServed)
                    .ThenBy(x => x.StartedAtMs)
                    .First(),
                BytesServed = sessionsForAgent.Sum(x => x.BytesServed),
            })
            .OrderByDescending(x => x.BytesServed)
            .ThenBy(x => x.Session.StartedAtMs)
            .First()
            .Session;

        return new PlayDto
        {
            Key = first.Id,
            Title = FirstNonEmpty(
                content?.Title,
                group.Select(x => x.FileName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                System.IO.Path.GetFileName(first.Path.TrimEnd('/')),
                first.Path),
            NzbName = content?.NzbName,
            Category = content?.Category,
            SubmissionSource = content?.SubmissionSource,
            Path = first.Path,
            DavItemId = first.DavItemId,
            HistoryItemId = group.Select(x => x.HistoryItemId).FirstOrDefault(x => x is not null),
            ClientIp = representativeClient.ClientIp ?? first.ClientIp,
            ClientUserAgent = representativeClient.ClientUserAgent,
            StartedAtUnix = startedMs / 1000,
            EndedAtUnix = endedMs / 1000,
            WatchedMs = watchedMs,
            SpanMs = Math.Max(0, endedMs - startedMs),
            FileSize = fileSize,
            MaxOffset = maxOffset,
            ReachedPct = fileSize is > 0
                ? Math.Min(100d, maxOffset * 100d / fileSize.Value)
                : null,
            BytesServed = bytesServed,
            BytesFetched = group.Sum(x => x.BytesFetched),
            AvgBytesPerSecond = watchedMs > 0
                ? (long)(bytesServed * 1000d / watchedMs)
                : 0,
            SourceBytesPerSecond = watchedMs > 0
                ? (long)(group.Sum(x => x.BytesFetched) * 1000d / watchedMs)
                : 0,
            // Startup latency: what the viewer waited before anything played.
            // The group is ordered oldest first, so this is the first session
            // that measured a first byte — not the smallest measurement, which
            // is a mid-play seek off an already-warm stream.
            FirstByteMs = group.FirstOrDefault(x => x.FirstByteMs.HasValue)?.FirstByteMs,
            // The last session decides how the play ended; earlier ones are seeks.
            EndReason = last.EndReason,
            ErrorNote = group.Select(x => x.ErrorNote).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            HasDiagnostics = group.Any(x => x.HasDiagnostics),
            IsProbe = isProbe,
            IsRcloneActivity = isRcloneActivity,
            // Finished direct reads are classified by what they did, not what
            // they called themselves. The user agent is frequently just
            // Dalvik, stagefright, or a browser after proxies and player stacks.
            IsReliablePlayback = !isProbe && !isRcloneActivity,
            IsLikelyBackgroundActivity = isSymlinkResolution,
            MountPurpose = isSymlinkResolution ? "symlink-resolution" : null,
            ContentCompletedAtUnix = content?.CompletedAtUnix,
            PlexPurpose = plex?.PlexPurpose,
            PlexConfidence = plex?.PlexConfidence,
            PlexProduct = plex?.PlexProduct,
            PlexPlayer = plex?.PlexPlayer,
            PlexPlatform = plex?.PlexPlatform,
            PlexRatingKey = plex?.PlexRatingKey,
            PlexDetail = plex?.PlexDetail,
            PlexIsTranscode = plex?.PlexIsTranscode,
            PlexPlaybackImpact = plexPlaybackImpact,
            // Reading a .rclonelink descriptor is proven mount metadata. A
            // playing Plex session that merely happened at the same time must
            // not promote those few bytes into Playback.
            IsPlexPlayback = !isSymlinkResolution
                             && plex?.PlexPurpose == "playback"
                             && plex.PlexConfidence is "exact-path" or "time-only",
            Issues = issues,
            Counters = counters,
            Providers = providers,
            Sessions = group.OrderByDescending(x => x.StartedAtMs).ToList(),
        };
    }

    private static bool IsUsefulPlexAttribution(SessionDto session)
    {
        if (string.IsNullOrWhiteSpace(session.PlexPurpose)) return false;
        if (session.PlexConfidence == "exact-path") return true;

        // Timing alone is useful for a playing session or an explicit Plex
        // maintenance activity. A paused, stopped, prebuffering, or transcode
        // session can coexist with unrelated mount work for minutes and proved
        // far too broad in real Sonarr imports.
        return session.PlexConfidence == "time-only" &&
               (session.PlexPurpose == "playback" ||
                session.PlexPurpose is
                    "library-scan" or
                    "intro-detection" or
                    "credits-detection" or
                    "thumbnail-generation" or
                    "chapter-generation" or
                    "loudness-analysis" or
                    "sonic-analysis" or
                    "fingerprinting" or
                    "deep-media-analysis" or
                    "media-analysis");
    }

    /// <summary>
    /// Identifies strong background-access patterns without pretending rclone can
    /// identify the process or container behind its shared mount.
    ///
    /// Two shapes are recognized:
    ///  1. repeated, short reads that touch the end while transferring very
    ///     little of the file; and
    ///  2. large reads of different files that start together and
    ///     overlap, followed by resumptions of the same background job.
    ///
    /// A single large rclone read is deliberately left alone. It may be a real
    /// playback request whose VFS cache is reading ahead.
    /// </summary>
    private static void ClassifyLikelyBackgroundActivity(IReadOnlyList<PlayDto> plays)
    {
        ClassifyImportInspection(plays);

        foreach (var play in plays.Where(IsAnalysisProbe))
        {
            play.MountPurpose = "analysis-probe";
            play.IsLikelyBackgroundActivity = true;
        }

        foreach (var play in plays.Where(IsRepeatedTailProbe))
            play.IsLikelyBackgroundActivity = true;

        var bulkCandidates = plays
            .Where(IsBulkBackgroundCandidate)
            .OrderBy(x => x.StartedAtUnix)
            .ToList();
        for (var i = 0; i < bulkCandidates.Count; i++)
        {
            for (var j = i + 1; j < bulkCandidates.Count; j++)
            {
                var first = bulkCandidates[i];
                var second = bulkCandidates[j];
                if (!SameRcloneClient(first, second)) continue;
                if (SameContent(first, second)) continue;
                if (!StartsWithin(first, second, BackgroundBulkReadMaxStartSkew)) continue;
                if (!IntervalsOverlapBy(first, second, BackgroundBulkReadMinOverlap)) continue;

                first.IsLikelyBackgroundActivity = true;
                second.IsLikelyBackgroundActivity = true;
            }
        }

        // A maintenance read can be interrupted and resume after the normal
        // ten-minute play-grouping gap. Once one fragment has strong batch
        // evidence, carry that classification across nearby fragments of the
        // same file. Repeat so a chain of resumptions is handled transitively.
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in plays.Where(x =>
                         !x.IsLikelyBackgroundActivity &&
                         IsBulkBackgroundContinuationCandidate(x)))
            {
                if (!plays.Any(known =>
                        known.IsLikelyBackgroundActivity &&
                        SameRcloneClient(candidate, known) &&
                        SameContent(candidate, known) &&
                        IntervalsWithin(
                            candidate,
                            known,
                            BackgroundBulkReadContinuationGap)))
                    continue;

                candidate.IsLikelyBackgroundActivity = true;
                changed = true;
            }
        } while (changed);
    }

    /// <summary>
    /// Identifies a media manager inspecting newly completed content. Multi-file
    /// imports are recognized from their batch shape. A single-file import also
    /// requires the matching .rclonelink resolution immediately beforehand and
    /// a brief head/tail inspection shape; completion timing alone is not enough.
    /// NzbDAVex still cannot tell whether the importer is Sonarr, Radarr, or
    /// another application behind rclone.
    /// </summary>
    private static void ClassifyImportInspection(IReadOnlyList<PlayDto> plays)
    {
        var candidates = plays
            .Where(play =>
                play.IsRcloneActivity &&
                play.MountPurpose is null &&
                play.HistoryItemId is not null &&
                play.ContentCompletedAtUnix is not null &&
                play.StartedAtUnix >= play.ContentCompletedAtUnix - 5 &&
                play.StartedAtUnix - play.ContentCompletedAtUnix <=
                    ImportInspectionWindow.TotalSeconds)
            .ToList();

        var symlinkResolutions = plays
            .Where(play => play.MountPurpose == "symlink-resolution")
            .ToList();
        foreach (var play in candidates.Where(play =>
                     IsSingleFileImportInspection(play, symlinkResolutions)))
            MarkImportInspection(play, 1);

        var batches = candidates
            .GroupBy(play => string.Join(
                '\n',
                play.HistoryItemId,
                play.ClientIp ?? "",
                play.ClientUserAgent ?? ""),
                StringComparer.Ordinal);

        foreach (var batch in batches)
        {
            var ordered = batch.OrderBy(play => play.StartedAtUnix).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var anchor = ordered[index].StartedAtUnix;
                var window = ordered
                    .Skip(index)
                    .TakeWhile(play =>
                        play.StartedAtUnix - anchor <=
                        ImportInspectionMaxStartSkew.TotalSeconds)
                    .ToList();
                var relatedFileCount = window
                    .Select(ContentKey)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                if (relatedFileCount < ImportInspectionMinRelatedFiles) continue;

                foreach (var play in window)
                    MarkImportInspection(play, relatedFileCount);
            }
        }
    }

    private static bool IsSingleFileImportInspection(
        PlayDto play,
        IReadOnlyList<PlayDto> symlinkResolutions)
    {
        if (play.FileSize is not (> 0) ||
            play.WatchedMs > ImportInspectionSingleMaxActiveTime.TotalMilliseconds ||
            play.Sessions.Sum(session => session.RequestCount) >
                ImportInspectionSingleMaxRequests)
            return false;

        var transferredFraction = play.BytesServed / (double)play.FileSize.Value;
        var reachedFraction = play.MaxOffset / (double)play.FileSize.Value;
        if (transferredFraction > ImportInspectionSingleMaxFileFraction ||
            reachedFraction < ImportInspectionSingleMinReachedFraction)
            return false;

        var mediaFileName = System.IO.Path.GetFileName(play.Title);
        return symlinkResolutions.Any(link =>
            SameRcloneClient(play, link) &&
            link.StartedAtUnix <= play.StartedAtUnix &&
            play.StartedAtUnix - link.EndedAtUnix >= -1 &&
            play.StartedAtUnix - link.EndedAtUnix <=
                ImportInspectionSymlinkMaxGap.TotalSeconds &&
            string.Equals(
                StripRcloneLinkSuffix(System.IO.Path.GetFileName(link.Title)),
                mediaFileName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string StripRcloneLinkSuffix(string fileName) =>
        fileName.EndsWith(".rclonelink", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".rclonelink".Length]
            : fileName;

    private static void MarkImportInspection(PlayDto play, int relatedFileCount)
    {
        play.MountPurpose = "import-inspection";
        play.MountRelatedFileCount = Math.Max(
            play.MountRelatedFileCount ?? 0,
            relatedFileCount);
        play.MountCompletedAtUnix = play.ContentCompletedAtUnix;
        play.IsLikelyBackgroundActivity = true;

        // Strong local evidence beats a weak timing-only Plex session.
        // Exact-path matches remain authoritative.
        if (play.PlexConfidence == "time-only")
            play.IsPlexPlayback = false;
    }

    private static bool IsRepeatedTailProbe(PlayDto play)
    {
        if (!play.IsRcloneActivity ||
            play.FileSize is not (> 0) ||
            play.Sessions.Count < BackgroundTailProbeMinSessions ||
            play.WatchedMs > BackgroundTailProbeMaxActiveTime.TotalMilliseconds)
            return false;

        var transferredFraction = play.BytesServed / (double)play.FileSize.Value;
        var reachedFraction = play.MaxOffset / (double)play.FileSize.Value;
        return transferredFraction <= BackgroundTailProbeMaxFileFraction &&
               reachedFraction >= BackgroundTailProbeMinReachedFraction;
    }

    private static bool IsAnalysisProbe(PlayDto play)
    {
        if (!play.IsRcloneActivity ||
            play.MountPurpose is not null ||
            play.FileSize is not (> 0) ||
            play.BytesServed != 0 ||
            play.BytesFetched != 0 ||
            play.WatchedMs > AnalysisProbeMaxActiveTime.TotalMilliseconds ||
            play.Sessions.Sum(session => session.RequestCount) < AnalysisProbeMinRequests)
            return false;

        return play.MaxOffset / (double)play.FileSize.Value >=
               AnalysisProbeMinReachedFraction;
    }

    private static bool IsBulkBackgroundCandidate(PlayDto play)
    {
        if (!play.IsRcloneActivity ||
            play.FileSize is not (> 0) ||
            play.BytesFetched <= 0 ||
            play.WatchedMs < BackgroundBulkReadMinActiveTime.TotalMilliseconds)
            return false;

        var transferredFraction = play.BytesServed / (double)play.FileSize.Value;
        var sourceUtilization = play.BytesServed / (double)play.BytesFetched;
        return transferredFraction >= BackgroundBulkReadMinFileFraction &&
               sourceUtilization >= BackgroundBulkReadMinSourceUtilization;
    }

    private static bool IsBulkBackgroundContinuationCandidate(PlayDto play)
    {
        if (!play.IsRcloneActivity ||
            play.FileSize is not (> 0) ||
            play.BytesFetched <= 0 ||
            play.WatchedMs < BackgroundBulkReadMinActiveTime.TotalMilliseconds)
            return false;

        // A continuation does not need to cover most of the file — it is often
        // precisely the remainder of an interrupted job — but it should still be
        // a substantial source-backed read, not a viewer briefly reopening the
        // same title after maintenance happened to touch it.
        var transferredFraction = play.BytesServed / (double)play.FileSize.Value;
        var sourceUtilization = play.BytesServed / (double)play.BytesFetched;
        return transferredFraction >= 0.10 &&
               sourceUtilization >= BackgroundBulkReadMinSourceUtilization;
    }

    private static bool SameRcloneClient(PlayDto first, PlayDto second) =>
        first.IsRcloneActivity &&
        second.IsRcloneActivity &&
        string.Equals(first.ClientIp, second.ClientIp, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            first.ClientUserAgent,
            second.ClientUserAgent,
            StringComparison.OrdinalIgnoreCase);

    private static bool SameContent(PlayDto first, PlayDto second) =>
        string.Equals(ContentKey(first), ContentKey(second), StringComparison.Ordinal);

    private static string ContentKey(PlayDto play) =>
        play.DavItemId ??
        play.HistoryItemId ??
        play.Path;

    private static bool StartsWithin(PlayDto first, PlayDto second, TimeSpan skew) =>
        Math.Abs(first.StartedAtUnix - second.StartedAtUnix) <= skew.TotalSeconds;

    private static bool IntervalsOverlapBy(PlayDto first, PlayDto second, TimeSpan overlap)
    {
        var overlapSeconds = Math.Min(first.EndedAtUnix, second.EndedAtUnix) -
                             Math.Max(first.StartedAtUnix, second.StartedAtUnix);
        return overlapSeconds >= overlap.TotalSeconds;
    }

    private static bool IntervalsWithin(PlayDto first, PlayDto second, TimeSpan gap)
    {
        var firstStart = first.StartedAtUnix;
        var firstEnd = first.EndedAtUnix;
        var secondStart = second.StartedAtUnix;
        var secondEnd = second.EndedAtUnix;
        var seconds = secondStart > firstEnd
            ? secondStart - firstEnd
            : firstStart > secondEnd
                ? firstStart - secondEnd
                : 0;
        return seconds <= gap.TotalSeconds;
    }

    internal static bool IsRcloneUserAgent(string? userAgent) =>
        userAgent?.Contains("rclone", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsRecognizedPlaybackUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return false;
        return RecognizedPlaybackUserAgentMarkers.Any(marker =>
            userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Live reads do not yet have the completed-session probe classification.
    /// Known players can be shown immediately; otherwise wait until the direct
    /// client has transferred enough data to look like playback. A request
    /// currently suffering viewer-impacting trouble is shown even if it failed
    /// before reaching the byte threshold.
    /// </summary>
    internal static bool IsLikelyActivePlayback(
        string? userAgent,
        long bytesRead,
        bool hasViewerImpact) =>
        !IsRcloneUserAgent(userAgent) &&
        (IsRecognizedPlaybackUserAgent(userAgent) ||
         bytesRead >= ProbeMaxBytesServed ||
         hasViewerImpact);

    private static CountersDto MergeCounters(IEnumerable<CountersDto> counters)
    {
        var all = counters.ToList();
        return new CountersDto
        {
            UpstreamStalls = all.Sum(x => x.UpstreamStalls),
            MaxUpstreamStallMs = all.Count == 0 ? 0 : all.Max(x => x.MaxUpstreamStallMs),
            TotalUpstreamStallMs = all.Sum(x => x.TotalUpstreamStallMs),
            UpstreamWaitWallMs = all.Sum(x => x.UpstreamWaitWallMs),
            MaxUpstreamWaitWallMs = all.Count == 0
                ? 0
                : all.Max(x => x.MaxUpstreamWaitWallMs),
            HeadOfLineStalls = all.Sum(x => x.HeadOfLineStalls),
            TotalHeadOfLineStallMs = all.Sum(x => x.TotalHeadOfLineStallMs),
            DownstreamStalls = all.Sum(x => x.DownstreamStalls),
            MaxDownstreamStallMs = all.Count == 0 ? 0 : all.Max(x => x.MaxDownstreamStallMs),
            TotalDownstreamStallMs = all.Sum(x => x.TotalDownstreamStallMs),
            FallbackRescues = all.Sum(x => x.FallbackRescues),
            ProviderRotations = all.Sum(x => x.ProviderRotations),
            FallbackBudgetExhaustions = all.Sum(x => x.FallbackBudgetExhaustions),
            CacheHits = all.Sum(x => x.CacheHits),
            CacheMisses = all.Sum(x => x.CacheMisses),
            ConnectionPermitWaits = all.Sum(x => x.ConnectionPermitWaits),
            MaxConnectionPermitWaitMs = all.Count == 0 ? 0 : all.Max(x => x.MaxConnectionPermitWaitMs),
            ProviderPoolWaits = all.Sum(x => x.ProviderPoolWaits),
            MaxProviderPoolWaitMs = all.Count == 0 ? 0 : all.Max(x => x.MaxProviderPoolWaitMs),
            FailoverSaves = all.Sum(x => x.FailoverSaves),
            ZeroFilledSegments = all.Sum(x => x.ZeroFilledSegments),
            ZeroFilledBytes = all.Sum(x => x.ZeroFilledBytes),
            BodyStallRecoveries = all.Sum(x => x.BodyStallRecoveries),
        };
    }

    private static int PlaybackImpactPriority(string? impact) => impact switch
    {
        "buffering-observed" => 0,
        "progress-stalled" => 1,
        "progress-continued" => 2,
        _ => 3,
    };

    private static List<ProviderDto> MergeProviders(IEnumerable<ProviderDto> providers) => providers
        .GroupBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase)
        .Select(group => new ProviderDto
        {
            ProviderId = group.Key,
            Host = group.First().Host,
            Nickname = group.Select(x => x.Nickname).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            Segments = group.Sum(x => x.Segments),
            Bytes = group.Sum(x => x.Bytes),
            Attempts = group.Sum(x => x.Attempts),
            Rescued = group.Sum(x => x.Rescued),
            Missing = group.Sum(x => x.Missing),
            Timeouts = group.Sum(x => x.Timeouts),
            Errors = group.Sum(x => x.Errors),
            IsBackup = group.Any(x => x.IsBackup),
        })
        .OrderByDescending(x => x.Segments)
        .ThenByDescending(x => x.Rescued)
        .ThenBy(x => x.Nickname ?? x.Host, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "unknown";
}

public sealed record PlaybackContentInfo(
    string? Title,
    string? NzbName,
    string? Category,
    long? CompletedAtUnix = null,
    string? SubmissionSource = null);
