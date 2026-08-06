import { useMemo, type ReactNode } from "react";
import type {
    PlaybackHistoryPage,
    PlaybackPlay,
    PlexStatus,
} from "~/clients/backend-client.server";
import cardStyles from "./playback-card.module.css";
import styles from "./playback-layout.module.css";
import { PlaybackCard } from "./playback-card";
import {
    computeStats,
    formatBytes,
    formatCount,
    playsForFilter,
    type FilterKey,
} from "./playback-view";

export function PlaybackHistory({
    plays,
    filter,
    retainedPlayback,
    retainedPlaybackLoading,
    plexStatus,
    sampledSessions,
    truncated,
    autoRefresh,
    refreshing,
    clearing,
    error,
    onFilterChange,
    onToggleAutoRefresh,
    onRefresh,
    onClear,
}: {
    plays: PlaybackPlay[],
    filter: FilterKey,
    retainedPlayback: PlaybackHistoryPage | null,
    retainedPlaybackLoading: boolean,
    plexStatus: PlexStatus,
    sampledSessions: number,
    truncated: boolean,
    autoRefresh: boolean,
    refreshing: boolean,
    clearing: boolean,
    error: string | null,
    onFilterChange: (filter: FilterKey) => void,
    onToggleAutoRefresh: () => void,
    onRefresh: () => void,
    onClear: () => void,
}) {
    const stats = useMemo(() => computeStats(plays), [plays]);
    const retainedPlaybackPlays = retainedPlayback?.plays ?? null;
    const visiblePlays = useMemo(
        () => playsForFilter(plays, retainedPlaybackPlays, filter),
        [plays, retainedPlaybackPlays, filter]);
    const playbackCount = retainedPlayback?.plays.length ?? stats.playback;
    const nothingVisible = visiblePlays.length === 0;
    const noActivity = plays.length === 0
        && (retainedPlayback?.plays.length ?? 0) === 0;

    return (
        <div className={styles.group}>
            <div className={styles.groupHeader}>
                <div className={styles.groupHeading}>
                    <h2 className={styles.title}>Read activity</h2>
                    <div className={styles.subtitle}>
                        Mount labels explain symlink and import reads; Plex labels identify
                        correlated Plex activity.
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
                        disabled={noActivity || clearing}
                        title="Permanently delete playback and file-activity history.">
                        {clearing ? "Clearing…" : "Clear history"}
                    </button>
                </div>
            </div>

            <div className={styles.filterBar}>
                <FilterChip
                    active={filter === "mount"}
                    onClick={() => onFilterChange("mount")}
                    count={stats.mount}
                    title="Every request made through rclone, including symlink resolution, import inspection, Plex activity, probes, scans, and unattributed mount reads.">
                    All mount activity
                </FilterChip>
                <FilterChip
                    active={filter === "playback"}
                    onClick={() => onFilterChange("playback")}
                    count={playbackCount}
                    title="Substantial direct reads plus exact or uniquely correlated Plex sessions reported playing. Probable time-only matches retain an uncertainty badge.">
                    Playback
                </FilterChip>
                <FilterChip
                    active={filter === "probes"}
                    onClick={() => onFilterChange("probes")}
                    count={stats.probes}
                    title="Tiny successful reads from direct clients or rclone. Known .rclonelink reads are identified as symlink resolution rather than guessed from size.">
                    Probes
                </FilterChip>
                <FilterChip active={filter === "issues"} onClick={() => onFilterChange("issues")} count={stats.issues}>
                    Source issues
                </FilterChip>
                <FilterChip active={filter === "failed"} onClick={() => onFilterChange("failed")} count={stats.failed}>
                    Failed
                </FilterChip>
            </div>

            {filter === "mount" && stats.mount > 0 && (
                <div className={styles.filterSummary}>
                    All rclone reads
                    <span aria-hidden="true">·</span>
                    <strong>{formatBytes(stats.mountBytesFetched)}</strong> fetched from Usenet
                    <span aria-hidden="true">·</span>
                    {formatBytes(stats.mountBytesServed)} served through rclone
                    <span aria-hidden="true">·</span>
                    {formatCount(stats.mount)} activit{stats.mount === 1 ? "y" : "ies"}
                    <span aria-hidden="true">·</span>
                    Specific mount and Plex badges explain known purposes
                </div>
            )}

            {plexStatus.enabled && !plexStatus.connected && (
                <div className={styles.errorBox}>
                    Plex is unavailable{plexStatus.lastError ? `: ${plexStatus.lastError}` : "."}
                    {" "}Existing reads remain visible; new rclone reads will not receive Plex labels.
                </div>
            )}

            {filter === "mount"
                && plexStatus.enabled
                && plexStatus.connected
                && plexStatus.activitiesConnected === false && (
                <div className={`${cardStyles.notice} ${cardStyles.noticeBar}`}>
                    Plex playback attribution is connected, but scanner and analyzer attribution is unavailable
                    {plexStatus.activitiesError ? `: ${plexStatus.activitiesError}` : "."}
                    {" "}The affected mount reads remain visible without a Plex tag.
                </div>
            )}

            {/* Plays are grouped after the sample is taken, so these counts
                are counts over the sample. Saying so is the difference
                between "no failures" and "none in the last N reads". */}
            {filter !== "playback" && truncated && (
                <div className={`${cardStyles.notice} ${cardStyles.noticeBar}`}>
                    Counts cover the most recent {formatCount(sampledSessions)} reads.
                    Older history exists and is not shown, and the oldest play here may be
                    missing its earlier parts.
                </div>
            )}

            {filter === "playback" && retainedPlayback?.truncated && (
                <div className={`${cardStyles.notice} ${cardStyles.noticeBar}`}>
                    Showing the most recent {formatCount(retainedPlayback.limit)} playbacks
                    from retained history.
                </div>
            )}

            {filter === "playback" && retainedPlaybackLoading && !retainedPlayback && (
                <div className={`${cardStyles.notice} ${cardStyles.noticeBar}`}>
                    Loading retained playback history…
                </div>
            )}

            {error && <div className={styles.errorBox}>Could not load: {error}</div>}

            {nothingVisible ? (
                <div className={styles.emptyState}>
                    {filter === "playback" && retainedPlaybackLoading
                        ? "Loading playback history…"
                        : noActivity
                        ? "No file activity recorded yet."
                        : filter === "playback" && retainedPlayback
                            ? "No reliable playback in retained history."
                        : filter === "playback" &&
                          stats.probes + stats.mount > 0
                            ? "No reliable playback in this sample. Mount activity and small probes remain available in their filters."
                            : "Nothing matches this filter."}
                </div>
            ) : (
                <div className={styles.playList}>
                    {visiblePlays.map(play => <PlaybackCard key={play.key} play={play} />)}
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
