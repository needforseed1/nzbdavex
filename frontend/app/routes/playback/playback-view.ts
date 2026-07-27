import type { PlaybackCounters, PlaybackPlay, PlaybackProvider } from "~/clients/backend-client.server";

export type IssueTone = "info" | "warn" | "bad";

export type IssueBadge = {
    key: string,
    label: string,
    tone: IssueTone,
    title: string,
};

export type FilterKey = "plays" | "scans" | "issues" | "failed";

/**
 * Every issue the backend can report, in the order they are worth reading:
 * how it ended first, then what retrieval had to do about it.
 */
const ISSUE_META: Record<string, { label: string, tone: IssueTone, title: string }> = {
    "corrupted": {
        label: "Damaged data",
        tone: "bad",
        title: "Articles could not be fetched and were served to the player as " +
            "zeros. Those parts of the file are damaged — expect glitches, " +
            "audio dropouts or a stuck picture at those points.",
    },
    "error": {
        label: "Failed",
        tone: "bad",
        title: "The stream ended with an error.",
    },
    "timeout": {
        label: "Timed out",
        tone: "bad",
        title: "The stream timed out waiting for data.",
    },
    "stalled": {
        label: "Source delays",
        tone: "warn",
        title: "Usenet caused at least three source waits or one wait of 3 seconds " +
            "or longer. This can cause buffering if the player's buffer runs out, " +
            "but does not prove that the viewer saw a pause.",
    },
    "body-stalled": {
        label: "Connection recovered",
        tone: "info",
        title: "A provider connection stopped sending in the middle of an article. " +
            "The connection was replaced and the article was fetched again.",
    },
    "budget-exhausted": {
        label: "Retry limit reached",
        tone: "info",
        title: "At least one article reached the provider retry limit. The play's " +
            "main status shows whether that affected the delivered stream.",
    },
    "pool-starved": {
        label: "Provider pool wait",
        tone: "info",
        title: "Retrieval waited for a free connection in a provider's pool.",
    },
    "permit-starved": {
        label: "Connection slot wait",
        tone: "info",
        title: "Retrieval waited for a global connection slot.",
    },
    "rotated": {
        label: "Provider changed",
        tone: "info",
        title: "Retrieval moved to another provider and continued.",
    },
    "rescued": {
        label: "Provider rescue",
        tone: "info",
        title: "Another provider supplied articles the first one could not.",
    },
    "backup-used": {
        label: "Backup served data",
        tone: "info",
        title: "A backup provider served part of this stream.",
    },
    "aborted": {
        label: "Client stopped request",
        tone: "info",
        title: "The player closed the stream — normal when seeking or stopping.",
    },
};

const ISSUE_ORDER = Object.keys(ISSUE_META);

/**
 * Signals that could have affected what the viewer received. Provider changes,
 * retries and recovered connections are useful diagnostics, but a recovery that
 * completed successfully is not itself a bad playback outcome.
 */
const PLAYBACK_IMPACT_ISSUES = new Set(["corrupted", "error", "timeout", "stalled"]);

export function describeIssues(issues: readonly string[]): IssueBadge[] {
    return issues
        .filter(key => key in ISSUE_META)
        .sort((a, b) => ISSUE_ORDER.indexOf(a) - ISSUE_ORDER.indexOf(b))
        .map(key => ({ key, ...ISSUE_META[key] }));
}

export function hasPlaybackImpact(issues: readonly string[]): boolean {
    return issues.some(issue => PLAYBACK_IMPACT_ISSUES.has(issue));
}

/** A useful headline fact, but not a playback problem when delivery succeeded. */
export function usedBackupProvider(
    play: Pick<PlaybackPlay, "issues" | "providers">,
): boolean {
    return play.issues.includes("backup-used")
        || play.providers.some(provider => provider.isBackup && provider.segments > 0);
}

export function playVerdict(play: Pick<PlaybackPlay, "issues" | "endReason">): IssueTone {
    if (play.endReason === "error" || play.endReason === "timeout") return "bad";
    if (play.issues.includes("corrupted")) return "bad";
    return play.issues.includes("stalled") ? "warn" : "info";
}

/** The word for the verdict pill, given how the play ended and what it hit. */
export function playVerdictLabel(play: Pick<PlaybackPlay, "issues" | "endReason">): string {
    if (play.endReason === "error") return "Failed";
    if (play.endReason === "timeout") return "Timed out";
    if (play.issues.includes("corrupted")) return "Damaged";
    if (play.issues.includes("stalled")) return "Source delays";
    return "No source issue";
}

/** Explains what the headline does — and deliberately does not — claim. */
export function playVerdictTitle(play: Pick<PlaybackPlay, "issues" | "endReason">): string {
    if (play.endReason === "error") return "Playback ended with a server error.";
    if (play.endReason === "timeout") return "Playback timed out waiting for data.";
    if (play.issues.includes("corrupted")) {
        return "Some articles could not be fetched and were delivered as zeros.";
    }
    if (play.issues.includes("stalled")) return ISSUE_META.stalled.title;
    return "No failed, timed-out, damaged, or materially delayed source request was detected. " +
        "Expand the play to see provider and recovery details.";
}

type FilterablePlay = Pick<PlaybackPlay, "issues" | "endReason" | "isProbe">;

export function matchesFilter(play: FilterablePlay, filter: FilterKey): boolean {
    switch (filter) {
        // Library scans outnumber real viewing several to one, so the default
        // view is what a person actually watched.
        case "plays": return !play.isProbe;
        case "scans": return play.isProbe;
        case "issues":
            return play.endReason === "error"
                || play.endReason === "timeout"
                || hasPlaybackImpact(play.issues);
        case "failed": return play.endReason === "error" || play.endReason === "timeout";
    }
}

export function computeStats(plays: readonly PlaybackPlay[]) {
    let watched = 0;
    let scans = 0;
    let issues = 0;
    let failed = 0;
    for (const play of plays) {
        if (matchesFilter(play, "plays")) watched++;
        if (matchesFilter(play, "scans")) scans++;
        if (matchesFilter(play, "issues")) issues++;
        if (matchesFilter(play, "failed")) failed++;
    }
    return { all: plays.length, watched, scans, issues, failed };
}

const KNOWN_CLIENTS: [RegExp, string][] = [
    [/infuse/i, "Infuse"],
    [/vlc/i, "VLC"],
    [/kodi|xbmc/i, "Kodi"],
    [/plex/i, "Plex"],
    [/jellyfin/i, "Jellyfin"],
    [/emby/i, "Emby"],
    [/stremio/i, "Stremio"],
    [/mpv/i, "mpv"],
    [/exoplayer/i, "ExoPlayer"],
    [/AppleCoreMedia|AVPlayer/i, "Apple player"],
    [/rclone/i, "rclone"],
    [/lavf|ffmpeg/i, "FFmpeg"],
    [/chrome|safari|firefox/i, "Browser"],
];

/** Turns a raw user-agent into something a person can scan in a table cell. */
export function describeClient(userAgent?: string | null, clientIp?: string | null): string {
    const agent = userAgent?.trim();
    if (agent) {
        for (const [pattern, label] of KNOWN_CLIENTS) {
            if (pattern.test(agent)) return label;
        }
        const firstToken = agent.split(/[\s/]/)[0];
        if (firstToken) return firstToken.slice(0, 24);
    }
    return clientIp?.trim() || "unknown";
}

/**
 * Non-zero delay facts, worst first. Returns an empty list for a stream that
 * never waited on anything, so the UI can say so instead of printing zeros.
 */
export function summarizeDelays(counters: PlaybackCounters): { key: string, label: string, value: string }[] {
    const rows: { key: string, label: string, value: string, weight: number }[] = [];
    if (counters.upstreamStalls > 0) rows.push({
        key: "upstream",
        label: "Waited on usenet",
        value: describeWaits(
            counters.upstreamStalls, counters.totalUpstreamStallMs, counters.maxUpstreamStallMs),
        weight: counters.maxUpstreamStallMs,
    });
    // Splits the wait above by cause. Without it, "the source could not keep up"
    // and "one slow article held up segments already downloaded" are the same
    // number — and they call for opposite fixes.
    if (counters.headOfLineStalls > 0) rows.push({
        key: "head-of-line",
        label: "…of those, blocked behind one article",
        value: describeWaits(
            counters.headOfLineStalls,
            counters.totalHeadOfLineStallMs,
            counters.maxUpstreamStallMs,
            "wait",
            { withLongest: false }),
        // Sorts immediately under the upstream row it qualifies.
        weight: counters.maxUpstreamStallMs - 1,
    });
    // Not a delay in any harmful sense: the client stopped reading because it
    // had buffered enough. Shown because it explains the timeline, and ranked
    // last so it never looks like the problem.
    if (counters.downstreamStalls > 0) rows.push({
        key: "downstream",
        label: "Player buffer full (normal)",
        value: describeWaits(
            counters.downstreamStalls, counters.totalDownstreamStallMs, counters.maxDownstreamStallMs,
            "pause"),
        weight: -1,
    });
    if (counters.providerPoolWaits > 0) rows.push({
        key: "pool",
        label: "Waited for a free connection",
        value: describeWaits(counters.providerPoolWaits, 0, counters.maxProviderPoolWaitMs),
        weight: counters.maxProviderPoolWaitMs,
    });
    if (counters.connectionPermitWaits > 0) rows.push({
        key: "permit",
        label: "Waited for a connection slot",
        value: describeWaits(counters.connectionPermitWaits, 0, counters.maxConnectionPermitWaitMs),
        weight: counters.maxConnectionPermitWaitMs,
    });
    return rows
        .sort((a, b) => b.weight - a.weight)
        .map(({ key, label, value }) => ({ key, label, value }));
}

/**
 * Spells out what was counted. "2×" left the reader guessing whether it meant
 * two waits, two seconds or two segments.
 */
function describeWaits(
    count: number,
    totalMs: number,
    longestMs: number,
    noun = "wait",
    { withLongest = true }: { withLongest?: boolean } = {},
): string {
    const times = `${count} ${noun}${count === 1 ? "" : "s"}`;
    // The subset rows have no longest of their own — quoting the parent's would
    // claim a measurement that was never taken for them.
    const longest = withLongest ? `longest ${formatMs(longestMs)}` : null;
    // Total is only meaningful when it is recorded and adds to the count.
    const head = totalMs > 0 && count > 1 ? `${formatMs(totalMs)} total` : null;
    return [head, times, longest].filter(Boolean).join(" · ");
}

export function summarizeRetrieval(counters: PlaybackCounters): { key: string, label: string, value: string }[] {
    const rows: { key: string, label: string, value: string }[] = [];
    // First: the part of the file the viewer did not really receive.
    if (counters.zeroFilledSegments > 0) rows.push({
        key: "zero-filled",
        label: "Articles served as zeros",
        value: `${formatCount(counters.zeroFilledSegments)}` +
            (counters.zeroFilledBytes > 0 ? ` · ${formatBytes(counters.zeroFilledBytes)} damaged` : ""),
    });
    if (counters.bodyStallRecoveries > 0) rows.push({
        key: "body-stalls",
        label: "Connections recovered",
        value: formatCount(counters.bodyStallRecoveries),
    });
    if (counters.fallbackRescues > 0 || counters.failoverSaves > 0) rows.push({
        key: "rescues",
        label: "Articles rescued by another provider",
        value: formatCount(Math.max(counters.fallbackRescues, counters.failoverSaves)),
    });
    if (counters.providerRotations > 0) rows.push({
        key: "rotations",
        label: "Mid-stream provider switches",
        value: formatCount(counters.providerRotations),
    });
    if (counters.fallbackBudgetExhaustions > 0) rows.push({
        key: "budget",
        label: "Provider retry limit reached",
        value: formatCount(counters.fallbackBudgetExhaustions),
    });
    const cacheTotal = counters.cacheHits + counters.cacheMisses;
    if (cacheTotal > 0) rows.push({
        key: "cache",
        label: "Segment cache hits",
        value: `${formatCount(counters.cacheHits)} of ${formatCount(cacheTotal)}` +
            ` (${Math.round(counters.cacheHits * 100 / cacheTotal)}%)`,
    });
    return rows;
}

export function providerShares(providers: readonly PlaybackProvider[]) {
    const total = providers.reduce((sum, provider) => sum + provider.segments, 0);
    return providers.map(provider => ({
        key: provider.providerId,
        label: provider.nickname || shortHost(provider.host),
        host: provider.host,
        amount: provider.segments > 0 ? `${formatCount(provider.segments)} articles` : undefined,
        share: total > 0 && provider.segments > 0
            ? `${Math.round(provider.segments * 100 / total)}%`
            : undefined,
    }));
}

const GENERIC_HOST_PREFIXES = new Set(
    ["news", "reader", "premium", "secure", "ssl", "nntp", "usenet", "block"]);

export function shortHost(host: string): string {
    if (!host) return "";
    const clean = host.replace(/:\d+$/, "");
    const labels = clean.split(".").filter(Boolean);
    if (labels.length <= 1) return clean;
    const identifying = labels.find(label => !GENERIC_HOST_PREFIXES.has(label.toLowerCase()));
    return identifying ?? labels[0];
}

export function formatMs(milliseconds: number | null | undefined): string {
    if (milliseconds == null) return "—";
    const ms = Math.max(0, milliseconds);
    if (ms < 1000) return `${Math.round(ms)}ms`;
    const seconds = ms / 1000;
    if (seconds >= 10) return `${Math.round(seconds)}s`;
    // One decimal, but never a bare ".0" — "9s" reads better than "9.0s".
    return `${seconds.toFixed(1).replace(/\.0$/, "")}s`;
}

/** Wall-clock length, in the units a person would use to describe a viewing. */
export function formatWatchTime(milliseconds: number): string {
    const seconds = Math.max(0, Math.round(milliseconds / 1000));
    if (seconds < 60) return `${seconds}s`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
    return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}

export function formatBytes(bytes: number | null | undefined): string {
    if (bytes == null || bytes <= 0) return "—";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let index = 0;
    let value = bytes;
    while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
    return `${value.toFixed(value >= 100 ? 0 : value >= 10 ? 1 : 2)} ${units[index]}`;
}

export function formatRate(bytesPerSecond: number): string {
    if (bytesPerSecond <= 0) return "—";
    return `${formatBytes(bytesPerSecond)}/s`;
}

export function formatCount(value: number): string {
    return Math.max(0, value).toLocaleString();
}

export function formatAge(unixSeconds: number): string {
    const age = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (age < 5) return "just now";
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}

export function formatPct(value: number | null | undefined): string {
    if (value == null) return "—";
    return `${Math.min(100, Math.max(0, value)).toFixed(value >= 10 ? 0 : 1)}%`;
}

/** True when two polls returned the same thing, so React can skip a re-render. */
export function playsEqual(a: readonly PlaybackPlay[], b: readonly PlaybackPlay[]): boolean {
    if (a === b) return true;
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
        const x = a[i];
        const y = b[i];
        if (x.key !== y.key) return false;
        if (x.endedAtUnix !== y.endedAtUnix) return false;
        if (x.bytesServed !== y.bytesServed) return false;
        if (x.sessions.length !== y.sessions.length) return false;
        if (x.endReason !== y.endReason) return false;
        if (x.issues.join(",") !== y.issues.join(",")) return false;
    }
    return true;
}
