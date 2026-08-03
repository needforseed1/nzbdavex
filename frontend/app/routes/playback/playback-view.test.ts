import assert from "node:assert/strict";
import test from "node:test";
import type { PlaybackCounters, PlaybackPlay, PlaybackProvider } from "~/clients/backend-client.server";
import {
    computeStats,
    describeClient,
    describeIssues,
    formatMs,
    formatPct,
    formatWatchTime,
    hasPlaybackImpact,
    matchesFilter,
    mountPurposeLabel,
    mountPurposeTitle,
    playVerdict,
    playVerdictLabel,
    playVerdictTitle,
    plexAttributionBadge,
    plexAttributionTitle,
    playsEqual,
    providerShares,
    shortHost,
    summarizeDelays,
    summarizeRetrieval,
    shouldShowPlexAttribution,
    usedBackupProvider,
} from "./playback-view";

test("time-only playing sessions are probable while background jobs remain possible", () => {
    assert.equal(
        plexAttributionBadge("playback", "time-only"),
        "Plex playback");
    assert.match(
        plexAttributionTitle("playback", "time-only"),
        /probable source/);
    assert.equal(
        plexAttributionBadge("intro-detection", "time-only"),
        "Plex intro detection");
    assert.equal(
        plexAttributionBadge("deep-media-analysis", "time-only"),
        "Plex deep media analysis");
    assert.equal(
        shouldShowPlexAttribution("deep-media-analysis", "time-only"),
        true);
    assert.equal(shouldShowPlexAttribution("playback", "time-only"), true);
    assert.equal(shouldShowPlexAttribution("paused", "time-only"), false);
    assert.equal(shouldShowPlexAttribution("paused", "exact-path"), true);
    assert.equal(
        shouldShowPlexAttribution("playback", "time-only", "import-inspection"),
        false);
});

test("specific mount purposes explain symlink and import reads", () => {
    assert.equal(mountPurposeLabel("symlink-resolution"), "Symlink resolution");
    assert.equal(mountPurposeLabel("analysis-probe"), "Analysis probe");
    assert.equal(mountPurposeLabel("import-inspection", "sonarr"), "Sonarr import");
    assert.equal(mountPurposeLabel("import-inspection", "RADARR"), "Radarr import");
    assert.equal(mountPurposeLabel("import-inspection"), "Import inspection");
    assert.match(
        mountPurposeTitle(play({
            mountPurpose: "analysis-probe",
            bytesServed: 0,
            bytesFetched: 0,
        })) ?? "",
        /scanner or media analyzer/);
    assert.match(
        mountPurposeTitle(play({
            mountPurpose: "symlink-resolution",
            bytesServed: 76,
            bytesFetched: 0,
        })) ?? "",
        /mount metadata, not media playback/);

    const detail = mountPurposeTitle(play({
        mountPurpose: "import-inspection",
        mountRelatedFileCount: 22,
        mountCompletedAtUnix: 1000,
        startedAtUnix: 1002,
    })) ?? "";
    assert.match(detail, /22 files/);
    assert.match(detail, /2s after/);
    assert.match(detail, /Sonarr, Radarr/);

    const sonarrDetail = mountPurposeTitle(play({
        mountPurpose: "import-inspection",
        submissionSource: "sonarr",
        mountRelatedFileCount: 22,
        mountCompletedAtUnix: 1000,
        startedAtUnix: 1002,
    })) ?? "";
    assert.match(sonarrDetail, /submitted to NzbDAVex by Sonarr/);
    assert.match(sonarrDetail, /attributed to Sonarr import inspection/);

    const singleFileDetail = mountPurposeTitle(play({
        mountPurpose: "import-inspection",
        mountRelatedFileCount: 1,
        mountCompletedAtUnix: 1000,
        startedAtUnix: 1082,
    })) ?? "";
    assert.match(singleFileDetail, /matching \.rclonelink/);
    assert.match(singleFileDetail, /beginning and end/);
    assert.match(singleFileDetail, /1m after/);
});

const emptyCounters: PlaybackCounters = {
    upstreamStalls: 0,
    maxUpstreamStallMs: 0,
    totalUpstreamStallMs: 0,
    headOfLineStalls: 0,
    totalHeadOfLineStallMs: 0,
    downstreamStalls: 0,
    maxDownstreamStallMs: 0,
    totalDownstreamStallMs: 0,
    fallbackRescues: 0,
    providerRotations: 0,
    fallbackBudgetExhaustions: 0,
    cacheHits: 0,
    cacheMisses: 0,
    connectionPermitWaits: 0,
    maxConnectionPermitWaitMs: 0,
    providerPoolWaits: 0,
    maxProviderPoolWaitMs: 0,
    failoverSaves: 0,
    zeroFilledSegments: 0,
    zeroFilledBytes: 0,
    bodyStallRecoveries: 0,
};

function play(overrides: Partial<PlaybackPlay> = {}): PlaybackPlay {
    return {
        key: "k1",
        title: "Some.Movie.mkv",
        path: "/content/abc",
        startedAtUnix: 1000,
        endedAtUnix: 1100,
        watchedMs: 100_000,
        spanMs: 100_000,
        maxOffset: 0,
        bytesServed: 0,
        bytesFetched: 0,
        avgBytesPerSecond: 0,
        sourceBytesPerSecond: 0,
        endReason: "completed",
        hasDiagnostics: true,
        isProbe: false,
        isRcloneActivity: false,
        isReliablePlayback: true,
        isLikelyBackgroundActivity: false,
        issues: [],
        counters: emptyCounters,
        providers: [],
        sessions: [],
        ...overrides,
    };
}

test("issue badges are ordered by what matters and unknown keys are dropped", () => {
    const badges = describeIssues(["backup-used", "error", "stalled", "invented-issue"]);
    assert.deepEqual(badges.map(b => b.key), ["error", "stalled", "backup-used"]);
    assert.deepEqual(badges.map(b => b.tone), ["bad", "warn", "warn"]);
    assert.equal(badges[0].label, "Failed");
});

test("only viewer-impact signals count as source issues", () => {
    assert.equal(hasPlaybackImpact(["aborted"]), false);
    assert.equal(hasPlaybackImpact(["body-stalled", "rescued", "backup-used"]), false);
    assert.equal(hasPlaybackImpact(["pool-starved", "permit-starved", "rotated"]), false);
    assert.equal(hasPlaybackImpact(["budget-exhausted"]), false);
    assert.equal(hasPlaybackImpact(["aborted", "stalled"]), true);
});

test("backup use remains visible without becoming a source issue", () => {
    assert.equal(usedBackupProvider(play({ issues: ["backup-used"] })), true);
    assert.equal(usedBackupProvider(play({
        providers: [{
            providerId: "backup-1",
            host: "news.backup.test",
            nickname: "Backup",
            segments: 3,
            bytes: 300,
            attempts: 3,
            rescued: 0,
            missing: 0,
            timeouts: 0,
            errors: 0,
            isBackup: true,
        }],
    })), true);
    assert.equal(usedBackupProvider(play()), false);
    assert.equal(playVerdict(play({ issues: ["backup-used"] })), "info");
});

test("verdict reflects delivered playback, not successful recovery work", () => {
    assert.equal(playVerdict(play()), "info");
    assert.equal(playVerdict(play({ issues: ["aborted"], endReason: "aborted" })), "info");
    assert.equal(playVerdict(play({ issues: ["stalled"] })), "warn");
    assert.equal(playVerdict(play({
        issues: [
            "body-stalled", "rescued", "backup-used", "rotated",
            "budget-exhausted", "pool-starved", "permit-starved",
        ],
    })), "info");
    assert.equal(playVerdict(play({ endReason: "timeout", issues: ["timeout"] })), "bad");
    // A play that ran to the end but served zeros is broken, not degraded.
    assert.equal(playVerdict(play({ issues: ["corrupted"] })), "bad");
});

test("a play that served zeros says so instead of reading as clean", () => {
    const damaged = play({ issues: ["corrupted"] });
    assert.equal(playVerdictLabel(damaged), "Damaged");
    assert.equal(playVerdictLabel(play()), "Source OK");
    assert.equal(playVerdictLabel(play({ issues: ["stalled"] })), "Usenet wait");
    assert.equal(
        playVerdictLabel(play({ endReason: "aborted", issues: ["aborted"] })),
        "Source OK");

    // It counts as viewer impact and is listed in Retrieval.
    assert.equal(hasPlaybackImpact(["corrupted"]), true);
    const rows = summarizeRetrieval({
        ...emptyCounters,
        zeroFilledSegments: 2,
        zeroFilledBytes: 1_500_000,
        bodyStallRecoveries: 1,
    });
    assert.deepEqual(rows.map(r => r.key), ["zero-filled", "body-stalls"]);
    assert.match(rows[0].value, /^2 · 1\.4[0-9] MB damaged$/);
});

test("filters mirror the backend rules", () => {
    const stopped = play({ issues: ["aborted"], endReason: "aborted" });
    const slow = play({ issues: ["stalled"] });
    const failed = play({ issues: ["error"], endReason: "error" });
    const recovered = play({ issues: ["body-stalled", "backup-used", "rotated"] });
    const probe = play({ isProbe: true, isReliablePlayback: false });
    const mountRead = play({
        isRcloneActivity: true,
        isReliablePlayback: false,
        bytesServed: 20,
        bytesFetched: 30,
    });
    const mountBackground = play({
        isRcloneActivity: true,
        isReliablePlayback: false,
        isLikelyBackgroundActivity: true,
        isProbe: true,
        bytesServed: 40,
        bytesFetched: 50,
    });
    const browserPlayback = play({
        clientUserAgent: "Mozilla/5.0 Chrome/138",
        isReliablePlayback: true,
    });

    assert.equal(matchesFilter(stopped, "playback"), true);
    assert.equal(matchesFilter(stopped, "issues"), false);
    assert.equal(matchesFilter(slow, "issues"), true);
    assert.equal(matchesFilter(slow, "failed"), false);
    assert.equal(matchesFilter(failed, "failed"), true);
    assert.equal(matchesFilter(recovered, "issues"), false);

    // Tiny reads remain observable regardless of whether they are direct or
    // also part of mount activity.
    assert.equal(matchesFilter(probe, "playback"), false);
    assert.equal(matchesFilter(probe, "probes"), true);
    assert.equal(matchesFilter(stopped, "probes"), false);
    assert.equal(matchesFilter(mountRead, "playback"), false);
    assert.equal(matchesFilter(mountRead, "mount"), true);
    assert.equal(matchesFilter(mountRead, "probes"), false);
    assert.equal(matchesFilter(mountBackground, "playback"), false);
    assert.equal(matchesFilter(mountBackground, "probes"), true);
    assert.equal(matchesFilter(mountBackground, "mount"), true);
    assert.equal(matchesFilter(browserPlayback, "playback"), true);
    assert.equal(matchesFilter(play({
        isRcloneActivity: true,
        isReliablePlayback: false,
        isPlexPlayback: true,
    }), "playback"), true);
    const stats = computeStats(
        [stopped, slow, failed, probe, mountRead, mountBackground, browserPlayback]);
    assert.deepEqual(
        stats,
        {
            all: 7,
            playback: 4,
            probes: 2,
            mount: 2,
            mountBytesServed: 60,
            mountBytesFetched: 80,
            issues: 2,
            failed: 1,
        });
});

test("clients are named from the user agent, then the ip", () => {
    assert.equal(describeClient("Infuse-Direct/8.1", "10.0.0.5"), "Infuse");
    assert.equal(describeClient("VLC/3.0.20 LibVLC/3.0.20"), "VLC");
    assert.equal(describeClient("Lavf/61.7.100"), "FFmpeg");
    assert.equal(describeClient("SomeUnknownPlayer/2.0"), "SomeUnknownPlayer");
    assert.equal(describeClient(null, "10.0.0.5"), "10.0.0.5");
    assert.equal(describeClient(null, null), "unknown");
});

test("expanded diagnostics keep successful recovery neutral", () => {
    const badges = describeIssues([
        "body-stalled", "rotated", "rescued", "backup-used",
        "pool-starved", "permit-starved", "budget-exhausted", "aborted",
    ]);
    assert.equal(badges.length, 8);
    assert.equal(badges.find(badge => badge.key === "backup-used")?.tone, "warn");
    assert.ok(badges
        .filter(badge => badge.key !== "backup-used")
        .every(badge => badge.tone === "info"));
    assert.equal(badges.find(badge => badge.key === "body-stalled")?.label, "Connection recovered");
});

test("upstream waits are split by cause so the fix is obvious", () => {
    // Same 12 s of waiting. Left: the source could not keep up. Right: it kept
    // up and one article held up segments already downloaded. Opposite fixes,
    // so the row has to say which happened.
    const slowSource = summarizeDelays({
        ...emptyCounters,
        upstreamStalls: 4,
        maxUpstreamStallMs: 5_000,
        totalUpstreamStallMs: 12_000,
    });
    assert.deepEqual(slowSource.map(r => r.key), ["upstream"]);

    const blocked = summarizeDelays({
        ...emptyCounters,
        upstreamStalls: 4,
        maxUpstreamStallMs: 5_000,
        totalUpstreamStallMs: 12_000,
        headOfLineStalls: 3,
        totalHeadOfLineStallMs: 10_000,
    });
    assert.deepEqual(blocked.map(r => r.key), ["upstream", "head-of-line"]);
    assert.equal(blocked[1].label, "Cause");
    assert.equal(blocked[1].value, "A slow article blocked prefetched data in 3 of 4 waits");
});

test("delays are listed worst first and omitted when nothing waited", () => {
    assert.deepEqual(summarizeDelays(emptyCounters), []);

    const rows = summarizeDelays({
        ...emptyCounters,
        upstreamStalls: 2,
        maxUpstreamStallMs: 1_500,
        providerPoolWaits: 1,
        maxProviderPoolWaitMs: 9_000,
        // Client backpressure always sorts last — it is not a fault.
        downstreamStalls: 8,
        maxDownstreamStallMs: 3_137,
    });
    assert.deepEqual(rows.map(r => r.key), ["pool", "upstream", "downstream"]);

    // Time waited leads the line when it is known, because that is what decides
    // whether the viewer saw anything.
    const withTotals = summarizeDelays({
        ...emptyCounters,
        upstreamStalls: 3,
        maxUpstreamStallMs: 4_000,
        totalUpstreamStallMs: 9_000,
    });
    assert.equal(withTotals[0].value, "9s total · 3 waits · longest 4s");

    // A single wait names itself, and never claims a total it does not have.
    const single = summarizeDelays({
        ...emptyCounters,
        providerPoolWaits: 1,
        maxProviderPoolWaitMs: 1_426,
    });
    assert.equal(single[0].value, "1 wait · longest 1.4s");

    // Client pacing is downstream backpressure, not an observed playback pause.
    // Its cumulative total is omitted because grouped requests can overlap.
    const paced = summarizeDelays({
        ...emptyCounters,
        downstreamStalls: 7,
        maxDownstreamStallMs: 1_836,
        totalDownstreamStallMs: 8_646,
    });
    assert.equal(paced[0].value, "7 waits · longest 1.8s");
    assert.equal(rows[0].value, "1 wait · longest 9s");
    assert.equal(rows[1].value, "2 waits · longest 1.5s");
    assert.equal(rows[2].label, "Client pacing (normal)");
    assert.equal(rows[2].value, "8 waits · longest 3.1s");
});

test("client pacing is never labelled as buffering", () => {
    // The counter exists for diagnostics, but a healthy stream that raced ahead
    // and got throttled by the client must not be badged as a problem.
    assert.equal(hasPlaybackImpact([]), false);
    const badge = describeIssues(["stalled"])[0];
    assert.equal(badge.label, "Usenet wait");
    assert.match(badge.title, /three waits/);
    assert.match(badge.title, /preparation and health-check time are not included/i);
    assert.match(playVerdictTitle(play({ issues: ["stalled"] })), /does not prove/);
});

test("retrieval summary reports rescues and cache ratio", () => {
    const rows = summarizeRetrieval({
        ...emptyCounters,
        fallbackRescues: 3,
        failoverSaves: 5,
        cacheHits: 30,
        cacheMisses: 10,
    });
    assert.deepEqual(rows.map(r => r.key), ["rescues", "cache"]);
    // The two counters describe the same rescues from different angles.
    assert.equal(rows[0].value, "5");
    assert.equal(rows[1].value, "30 of 40 (75%)");
});

test("provider shares are computed from segments served", () => {
    const providers: PlaybackProvider[] = [
        {
            providerId: "p1", host: "news.primary.test", nickname: "Primary",
            segments: 750, bytes: 0, attempts: 0, rescued: 0, missing: 0,
            timeouts: 0, errors: 0, isBackup: false,
        },
        {
            providerId: "b1", host: "news.backup.test", nickname: null,
            segments: 250, bytes: 0, attempts: 9, rescued: 4, missing: 2,
            timeouts: 0, errors: 0, isBackup: true,
        },
    ];
    const shares = providerShares(providers);
    assert.deepEqual(shares.map(s => s.label), ["Primary", "backup"]);
    assert.deepEqual(shares.map(s => s.share), ["75%", "25%"]);
    assert.equal(shares[0].amount, "750 articles");
});

test("host shortening skips generic prefixes", () => {
    assert.equal(shortHost("news.eweka.nl"), "eweka");
    assert.equal(shortHost("premium.frugalusenet.com:563"), "frugalusenet");
    assert.equal(shortHost("localhost"), "localhost");
});

test("durations and percentages read naturally", () => {
    assert.equal(formatMs(430), "430ms");
    assert.equal(formatMs(1_500), "1.5s");
    assert.equal(formatMs(65_000), "65s");
    assert.equal(formatMs(null), "—");
    assert.equal(formatWatchTime(45_000), "45s");
    assert.equal(formatWatchTime(125_000), "2m 5s");
    assert.equal(formatWatchTime(3_930_000), "1h 5m");
    assert.equal(formatPct(45.4), "45%");
    assert.equal(formatPct(2.25), "2.3%");
    assert.equal(formatPct(null), "—");
});

test("polling skips re-render when nothing changed", () => {
    const a = [play()];
    const b = [play()];
    assert.equal(playsEqual(a, b), true);
    assert.equal(playsEqual(a, [play({ bytesServed: 10 })]), false);
    assert.equal(playsEqual(a, [play({ bytesFetched: 10 })]), false);
    assert.equal(playsEqual(a, [play({ isReliablePlayback: false })]), false);
    assert.equal(playsEqual(a, [play({ isLikelyBackgroundActivity: true })]), false);
    assert.equal(playsEqual(a, [play({ mountPurpose: "import-inspection" })]), false);
    assert.equal(playsEqual(a, [play({ mountRelatedFileCount: 22 })]), false);
    assert.equal(playsEqual(a, [play({ submissionSource: "sonarr" })]), false);
    assert.equal(playsEqual(a, [play({ isPlexPlayback: true })]), false);
    assert.equal(playsEqual(a, [play({ issues: ["stalled"] })]), false);
    assert.equal(playsEqual(a, []), false);
});
