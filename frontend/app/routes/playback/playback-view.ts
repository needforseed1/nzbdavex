import type { PlaybackCounters, PlaybackPlay, PlaybackProvider } from "~/clients/backend-client.server";

export type IssueTone = "info" | "warn" | "bad";

export type IssueBadge = {
    key: string,
    label: string,
    tone: IssueTone,
    title: string,
};

export type FilterKey =
    "playback" | "mount" | "probes" | "issues" | "failed";

const PLEX_PURPOSE_LABELS: Record<string, string> = {
    playback: "Playback",
    paused: "Paused or prebuffering",
    prebuffering: "Prebuffering",
    stopped: "Stopped session",
    transcode: "Transcode only",
    "plex-session": "Player session",
    "library-scan": "Library scan",
    "intro-detection": "Intro detection",
    "credits-detection": "Credits or outro detection",
    "thumbnail-generation": "Thumbnail generation",
    "chapter-generation": "Chapter generation",
    "loudness-analysis": "Loudness analysis",
    "sonic-analysis": "Sonic analysis",
    fingerprinting: "Fingerprinting",
    "deep-media-analysis": "Deep media analysis",
    "media-analysis": "Analysis",
};

export function plexPurposeLabel(purpose?: string | null): string {
    if (!purpose) return "Unknown Plex activity";
    return PLEX_PURPOSE_LABELS[purpose]
        ?? purpose.replaceAll("-", " ").replace(/\b\w/g, letter => letter.toUpperCase());
}

export function plexAttributionBadge(
    purpose?: string | null,
    _confidence?: string | null,
): string {
    return "Plex " + plexPurposeLabel(purpose).toLowerCase();
}

export function plexAttributionTitle(
    purpose?: string | null,
    confidence?: string | null,
): string {
    const label = plexPurposeLabel(purpose).toLowerCase();
    return confidence === "exact-path"
        ? `Plex reported ${label} for this exact media item during the NzbDAVex read.`
        : purpose === "playback"
            ? "A single Plex session reported playing at the same time as this rclone read. " +
              "Plex is the probable source, but the media path was not proven."
            : `Plex reported ${label} at the same time as this rclone read, but did not expose a matching media path. Plex is possible, not proven.`;
}

const USEFUL_TIME_ONLY_PLEX_PURPOSES = new Set([
    "playback",
    "library-scan",
    "intro-detection",
    "credits-detection",
    "thumbnail-generation",
    "chapter-generation",
    "loudness-analysis",
    "sonic-analysis",
    "fingerprinting",
    "deep-media-analysis",
    "media-analysis",
]);

export function shouldShowPlexAttribution(
    purpose?: string | null,
    confidence?: string | null,
    mountPurpose?: string | null,
): boolean {
    if (!purpose) return false;
    if (confidence === "exact-path") return true;
    if (confidence !== "time-only" || !USEFUL_TIME_ONLY_PLEX_PURPOSES.has(purpose))
        return false;

    // A proven symlink request or newly completed import batch is stronger
    // evidence than a Plex session that merely happened at the same time.
    return !mountPurpose || mountPurpose === "analysis-probe";
}

export function submissionSourceLabel(source?: string | null): string | null {
    switch (source?.toLowerCase()) {
        case "sonarr": return "Sonarr";
        case "radarr": return "Radarr";
        default: return null;
    }
}

export function mountPurposeLabel(
    purpose?: string | null,
    submissionSource?: string | null,
): string | null {
    switch (purpose) {
        case "symlink-resolution": return "Symlink resolution";
        case "import-inspection": {
            const source = submissionSourceLabel(submissionSource);
            return source ? `${source} import` : "Import inspection";
        }
        case "analysis-probe": return "Analysis probe";
        default: return null;
    }
}

export function mountPurposeTitle(
    play: Pick<
        PlaybackPlay,
        "mountPurpose" | "mountRelatedFileCount" | "mountCompletedAtUnix"
        | "startedAtUnix" | "bytesServed" | "bytesFetched" | "submissionSource"
    >,
): string | null {
    if (play.mountPurpose === "symlink-resolution") {
        return "rclone read NzbDAVex's small .rclonelink descriptor to create a filesystem symlink. " +
            "This is mount metadata, not media playback.";
    }
    if (play.mountPurpose === "analysis-probe") {
        return "rclone made a burst of requests reaching the end of the file without " +
            "NzbDAVex serving or fetching media bytes. This strongly matches a scanner " +
            "or media analyzer working from cached data. rclone alone cannot identify " +
            "the originating application; a separate Plex badge is shown when Plex " +
            "reports a matching activity.";
    }
    if (play.mountPurpose !== "import-inspection") return null;

    const source = submissionSourceLabel(play.submissionSource);
    const attribution = source
        ? ` The NZB was submitted to NzbDAVex by ${source}, so this is attributed to ${source} import inspection.`
        : " The submitting application was not recorded, so rclone cannot reveal whether the caller was Sonarr, Radarr, or another application.";
    const count = play.mountRelatedFileCount ?? 0;
    const delta = play.mountCompletedAtUnix == null
        ? null
        : Math.max(0, play.startedAtUnix - play.mountCompletedAtUnix);
    const timing = delta == null
        ? ""
        : delta < 60
            ? ` Activity began ${delta}s after NzbDAVex completed the NZB.`
            : ` Activity began ${Math.floor(delta / 60)}m after NzbDAVex completed the NZB.`;
    if (count === 1) {
        return "The matching .rclonelink descriptor was resolved immediately before this newly " +
            `completed file was opened, and rclone only inspected small ranges at the beginning ` +
            `and end.${timing} This strongly matches media-manager import inspection.${attribution}`;
    }
    return `${count || "Multiple"} files from the same newly completed NZB were read through ` +
        `rclone as one batch.${timing} This strongly matches media-manager import inspection.` +
        attribution;
}

export function plexClientLabel(
    product?: string | null,
    platform?: string | null,
    player?: string | null,
): string {
    return [product, platform, player].filter(Boolean).join(" · ");
}

/**
 * Every issue the backend can report, in the order they are worth reading:
 * how it ended first, then what retrieval had to do about it.
 */
const ISSUE_META: Record<string, { label: string, tone: IssueTone, title: string }> = {
    "corrupted": {
        label: "Damaged data",
        tone: "bad",
        title: "Articles could not be fetched and were served to the requesting " +
            "client as zeros. Those parts of the file are damaged.",
    },
    "error": {
        label: "Failed",
        tone: "bad",
        title: "The request ended with an error.",
    },
    "timeout": {
        label: "Timed out",
        tone: "bad",
        title: "The request timed out waiting for data.",
    },
    "stalled": {
        label: "Usenet wait",
        tone: "warn",
        title: "During media delivery, Usenet caused at least three waits or one " +
            "wait of 3 seconds or longer. This can cause buffering if the client " +
            "runs out of buffered data, but does not prove playback paused. NZB " +
            "preparation and health-check time are not included.",
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
        label: "Backup used",
        tone: "warn",
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
    if (play.issues.includes("stalled")) return "Usenet wait";
    return "Source OK";
}

/** Explains what the headline does — and deliberately does not — claim. */
export function playVerdictTitle(play: Pick<PlaybackPlay, "issues" | "endReason">): string {
    if (play.endReason === "error") return "The request ended with a server error.";
    if (play.endReason === "timeout") return "The request timed out waiting for data.";
    if (play.issues.includes("corrupted")) {
        return "Some articles could not be fetched and were delivered as zeros.";
    }
    if (play.issues.includes("stalled")) return ISSUE_META.stalled.title;
    return "No failed, timed-out, damaged, or materially delayed source request was detected. " +
        "Expand the play to see provider and recovery details.";
}

type FilterablePlay = Pick<
    PlaybackPlay,
    "issues" | "endReason" | "isProbe" | "isRcloneActivity"
    | "isReliablePlayback" | "isPlexPlayback"
>;

export function matchesFilter(play: FilterablePlay, filter: FilterKey): boolean {
    switch (filter) {
        // Playback is identified from direct read behavior rather than the
        // user-agent string, which is frequently generic after a proxy.
        case "playback": return play.isReliablePlayback || play.isPlexPlayback === true;
        case "probes": return play.isProbe;
        case "mount": return play.isRcloneActivity;
        case "issues":
            return play.endReason === "error"
                || play.endReason === "timeout"
                || hasPlaybackImpact(play.issues);
        case "failed": return play.endReason === "error" || play.endReason === "timeout";
    }
}

export function computeStats(plays: readonly PlaybackPlay[]) {
    let playback = 0;
    let probes = 0;
    let mount = 0;
    let mountBytesServed = 0;
    let mountBytesFetched = 0;
    let issues = 0;
    let failed = 0;
    for (const play of plays) {
        if (matchesFilter(play, "playback")) playback++;
        if (matchesFilter(play, "probes")) probes++;
        if (matchesFilter(play, "mount")) {
            mount++;
            mountBytesServed += play.bytesServed;
            mountBytesFetched += play.bytesFetched;
        }
        if (matchesFilter(play, "issues")) issues++;
        if (matchesFilter(play, "failed")) failed++;
    }
    return {
        all: plays.length,
        playback,
        probes,
        mount,
        mountBytesServed,
        mountBytesFetched,
        issues,
        failed,
    };
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
        label: "Usenet wait",
        value: describeWaits(
            counters.upstreamStalls, counters.totalUpstreamStallMs, counters.maxUpstreamStallMs),
        weight: counters.maxUpstreamStallMs,
    });
    // Splits the wait above by cause. Without it, "the source could not keep up"
    // and "one slow article held up segments already downloaded" are the same
    // number — and they call for opposite fixes.
    if (counters.headOfLineStalls > 0) rows.push({
        key: "head-of-line",
        label: "Cause",
        value: counters.headOfLineStalls === counters.upstreamStalls
            ? counters.headOfLineStalls === 1
                ? "A slow article blocked prefetched data"
                : `A slow article blocked prefetched data in all ${counters.headOfLineStalls} waits`
            : `A slow article blocked prefetched data in ${counters.headOfLineStalls} of `
              + `${counters.upstreamStalls} waits`,
        // Sorts immediately under the upstream row it qualifies.
        weight: counters.maxUpstreamStallMs - 1,
    });
    // Downstream backpressure is usually ordinary player/proxy pacing, not an
    // observed playback pause. Its cumulative duration is deliberately hidden:
    // grouped, overlapping range sessions can make that total exceed wall time.
    if (counters.downstreamStalls > 0) rows.push({
        key: "downstream",
        label: "Client pacing (normal)",
        value: describeWaits(
            counters.downstreamStalls, 0, counters.maxDownstreamStallMs),
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
        if (x.bytesFetched !== y.bytesFetched) return false;
        if (x.sessions.length !== y.sessions.length) return false;
        if (x.endReason !== y.endReason) return false;
        if (x.isRcloneActivity !== y.isRcloneActivity) return false;
        if (x.isReliablePlayback !== y.isReliablePlayback) return false;
        if (x.isLikelyBackgroundActivity !== y.isLikelyBackgroundActivity) return false;
        if (x.mountPurpose !== y.mountPurpose) return false;
        if (x.mountRelatedFileCount !== y.mountRelatedFileCount) return false;
        if (x.mountCompletedAtUnix !== y.mountCompletedAtUnix) return false;
        if (x.submissionSource !== y.submissionSource) return false;
        if (x.isPlexPlayback !== y.isPlexPlayback) return false;
        if (x.plexPurpose !== y.plexPurpose) return false;
        if (x.plexConfidence !== y.plexConfidence) return false;
        if (x.issues.join(",") !== y.issues.join(",")) return false;
    }
    return true;
}
