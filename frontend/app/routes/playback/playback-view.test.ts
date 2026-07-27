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
    playVerdict,
    playVerdictLabel,
    playVerdictTitle,
    playsEqual,
    providerShares,
    shortHost,
    summarizeDelays,
    summarizeRetrieval,
    usedBackupProvider,
} from "./playback-view";

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
    assert.deepEqual(badges.map(b => b.tone), ["bad", "warn", "info"]);
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
    assert.equal(playVerdictLabel(play()), "No source issue");
    assert.equal(playVerdictLabel(play({ issues: ["stalled"] })), "Source delays");
    assert.equal(
        playVerdictLabel(play({ endReason: "aborted", issues: ["aborted"] })),
        "No source issue");

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
    const scan = play({ isProbe: true });

    assert.equal(matchesFilter(stopped, "plays"), true);
    assert.equal(matchesFilter(stopped, "issues"), false);
    assert.equal(matchesFilter(slow, "issues"), true);
    assert.equal(matchesFilter(slow, "failed"), false);
    assert.equal(matchesFilter(failed, "failed"), true);
    assert.equal(matchesFilter(recovered, "issues"), false);

    // A library scan is not something anybody watched.
    assert.equal(matchesFilter(scan, "plays"), false);
    assert.equal(matchesFilter(scan, "scans"), true);
    assert.equal(matchesFilter(stopped, "scans"), false);

    assert.deepEqual(
        computeStats([stopped, slow, failed, scan]),
        { all: 4, watched: 3, scans: 1, issues: 2, failed: 1 });
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
    assert.ok(badges.every(badge => badge.tone === "info"));
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
    // No "longest" here: that measurement belongs to the parent row.
    assert.equal(blocked[1].value, "10s total · 3 waits");
    assert.match(blocked[1].label, /blocked behind one article/);
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

    // Client pacing counts pauses, not waits — it is not waiting on anything.
    const paced = summarizeDelays({
        ...emptyCounters,
        downstreamStalls: 7,
        maxDownstreamStallMs: 1_836,
        totalDownstreamStallMs: 8_646,
    });
    assert.equal(paced[0].value, "8.6s total · 7 pauses · longest 1.8s");
    assert.equal(rows[0].value, "1 wait · longest 9s");
    assert.equal(rows[1].value, "2 waits · longest 1.5s");
    assert.equal(rows[2].label, "Player buffer full (normal)");
    assert.equal(rows[2].value, "8 pauses · longest 3.1s");
});

test("a full player buffer is never labelled as buffering", () => {
    // The counter exists for diagnostics, but a healthy stream that raced ahead
    // and got throttled by the client must not be badged as a problem.
    assert.equal(hasPlaybackImpact([]), false);
    const badge = describeIssues(["stalled"])[0];
    assert.equal(badge.label, "Source delays");
    assert.match(badge.title, /three source waits/);
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
    assert.equal(playsEqual(a, [play({ issues: ["stalled"] })]), false);
    assert.equal(playsEqual(a, []), false);
});
