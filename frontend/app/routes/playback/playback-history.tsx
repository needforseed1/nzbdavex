import { useMemo, useState, type ReactNode } from "react";
import type { PlaybackPlay } from "~/clients/backend-client.server";
import cardStyles from "./playback-card.module.css";
import styles from "./playback-layout.module.css";
import { PlaybackCard } from "./playback-card";
import {
    computeStats,
    formatBytes,
    formatCount,
    matchesFilter,
    type FilterKey,
} from "./playback-view";

export function PlaybackHistory({
    plays,
    sampledSessions,
    truncated,
    autoRefresh,
    refreshing,
    clearing,
    error,
    onToggleAutoRefresh,
    onRefresh,
    onClear,
}: {
    plays: PlaybackPlay[],
    sampledSessions: number,
    truncated: boolean,
    autoRefresh: boolean,
    refreshing: boolean,
    clearing: boolean,
    error: string | null,
    onToggleAutoRefresh: () => void,
    onRefresh: () => void,
    onClear: () => void,
}) {
    const [filter, setFilter] = useState<FilterKey>("playback");
    const stats = useMemo(() => computeStats(plays), [plays]);
    const visible = useMemo(
        () => plays.filter(play => matchesFilter(play, filter)),
        [plays, filter]);

    return (
        <div className={styles.group}>
            <div className={styles.groupHeader}>
                <div className={styles.groupHeading}>
                    <h2 className={styles.title}>Playback &amp; file activity</h2>
                    <div className={styles.subtitle}>
                        Playback is identified by direct read behavior, independent of the client name.
                    </div>
                </div>
                <div className={styles.controls}>
                    <button
                        type="button"
                        className={`${styles.toolbarBtn} ${styles.liveBtn} ${autoRefresh ? styles.liveBtnOn : ""}`}
                        onClick={onToggleAutoRefresh}
                        title={autoRefresh
                            ? "Re-reads finished plays every few seconds. Click to pause."
                            : "Auto-refresh paused. Click to resume."}>
                        <span className={`${styles.liveDot} ${autoRefresh ? styles.liveDotOn : ""}`} />
                        {/* Not "Live": this polls finished plays. What is live
                            is the Playing now section above. */}
                        {autoRefresh ? (refreshing ? "Refreshing…" : "Auto-refresh") : "Paused"}
                    </button>
                    <button
                        type="button"
                        className={styles.toolbarBtn}
                        onClick={onRefresh}
                        disabled={refreshing || clearing}
                        title="Refresh now.">
                        <svg
                            className={`${styles.toolbarIcon} ${refreshing ? styles.spinning : ""}`}
                            viewBox="0 0 16 16"
                            fill="currentColor"
                            aria-hidden="true">
                            <path d="M8 3a5 5 0 1 0 4.546 2.914.5.5 0 0 1 .908-.417A6 6 0 1 1 8 2v1z" />
                            <path d="M8 4.466V.534a.25.25 0 0 1 .41-.192l2.36 1.966c.12.1.12.284 0 .384L8.41 4.658A.25.25 0 0 1 8 4.466z" />
                        </svg>
                        Refresh
                    </button>
                    <button
                        type="button"
                        className={`${styles.toolbarBtn} ${styles.toolbarBtnDanger}`}
                        onClick={onClear}
                        disabled={plays.length === 0 || clearing}
                        title="Permanently delete playback and file-activity history.">
                        {clearing ? "Clearing…" : "Clear history"}
                    </button>
                </div>
            </div>

            <div className={styles.filterBar}>
                <FilterChip
                    active={filter === "playback"}
                    onClick={() => setFilter("playback")}
                    count={stats.playback}
                    title="Substantial direct reads that look like playback. Client names are not required; shared-mount traffic and tiny probes are excluded.">
                    Playback
                </FilterChip>
                <FilterChip
                    active={filter === "mount"}
                    onClick={() => setFilter("mount")}
                    count={stats.mount}
                    title="Everything requested through rclone. The originating application or container is not visible.">
                    Mount activity
                </FilterChip>
                <FilterChip
                    active={filter === "probes"}
                    onClick={() => setFilter("probes")}
                    count={stats.probes}
                    title="Tiny successful reads from clients connecting directly to WebDAV. Their exact purpose is unknown; rclone requests remain under Mount activity.">
                    Direct probes
                </FilterChip>
                <FilterChip active={filter === "issues"} onClick={() => setFilter("issues")} count={stats.issues}>
                    Source issues
                </FilterChip>
                <FilterChip active={filter === "failed"} onClick={() => setFilter("failed")} count={stats.failed}>
                    Failed
                </FilterChip>
            </div>

            {filter === "mount" && stats.mount > 0 && (
                <div className={styles.filterSummary}>
                    Originating app unknown
                    <span aria-hidden="true">·</span>
                    <strong>{formatBytes(stats.mountBytesFetched)}</strong> fetched from Usenet
                    <span aria-hidden="true">·</span>
                    {formatBytes(stats.mountBytesServed)} served through rclone
                    <span aria-hidden="true">·</span>
                    {formatCount(stats.mount)} activit{stats.mount === 1 ? "y" : "ies"}
                </div>
            )}

            {/* Plays are grouped after the sample is taken, so these counts
                are counts over the sample. Saying so is the difference
                between "no failures" and "none in the last N reads". */}
            {truncated && (
                <div className={`${cardStyles.notice} ${cardStyles.noticeBar}`}>
                    Counts cover the most recent {formatCount(sampledSessions)} reads.
                    Older history exists and is not shown, and the oldest play here may be
                    missing its earlier parts.
                </div>
            )}

            {error && <div className={styles.errorBox}>Could not load: {error}</div>}

            {visible.length === 0 ? (
                <div className={styles.emptyState}>
                    {plays.length === 0
                        ? "No file activity recorded yet."
                        : filter === "playback" &&
                          stats.probes + stats.mount > 0
                            ? "No direct playback in this sample. Mount activity and small probes remain available in their filters."
                            : "Nothing matches this filter."}
                </div>
            ) : (
                <div className={styles.playList}>
                    {visible.map(play => <PlaybackCard key={play.key} play={play} />)}
                </div>
            )}
        </div>
    );
}

function FilterChip({
    active,
    onClick,
    count,
    title,
    children,
}: {
    active: boolean,
    onClick: () => void,
    count: number,
    title?: string,
    children: ReactNode,
}) {
    return (
        <button
            type="button"
            className={`${styles.filterChip} ${active ? styles.filterChipActive : ""}`}
            title={title}
            onClick={onClick}>
            {children}
            <span className={styles.filterChipCount}>{count}</span>
        </button>
    );
}
