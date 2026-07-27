import { useMemo, useState, type ReactNode } from "react";
import type { PlaybackPlay } from "~/clients/backend-client.server";
import cardStyles from "./playback-card.module.css";
import styles from "./playback-layout.module.css";
import { PlaybackCard } from "./playback-card";
import {
    computeStats,
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
    const [filter, setFilter] = useState<FilterKey>("plays");
    const stats = useMemo(() => computeStats(plays), [plays]);
    const visible = useMemo(
        () => plays.filter(play => matchesFilter(play, filter)),
        [plays, filter]);

    return (
        <div className={styles.group}>
            <div className={styles.groupHeader}>
                <div className={styles.groupHeading}>
                    <h2 className={styles.title}>Playback history</h2>
                    <div className={styles.subtitle}>
                        Source health at a glance. Expand a play for provider and recovery details.
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
                        title="Permanently delete playback history.">
                        {clearing ? "Clearing…" : "Clear history"}
                    </button>
                </div>
            </div>

            <div className={styles.filterBar}>
                <FilterChip active={filter === "plays"} onClick={() => setFilter("plays")} count={stats.watched}>
                    Watched
                </FilterChip>
                <FilterChip
                    active={filter === "scans"}
                    onClick={() => setFilter("scans")}
                    count={stats.scans}
                    title="Library scans — a media server reading file headers, not playback.">
                    Scans
                </FilterChip>
                <FilterChip active={filter === "issues"} onClick={() => setFilter("issues")} count={stats.issues}>
                    Source issues
                </FilterChip>
                <FilterChip active={filter === "failed"} onClick={() => setFilter("failed")} count={stats.failed}>
                    Failed
                </FilterChip>
            </div>

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
                        ? "Nothing streamed yet. Play something from your media client and it will show up here."
                        : filter === "plays" && stats.scans > 0
                            ? `Nothing watched recently — the last ${stats.scans} reads were library scans.`
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
