import { useCallback, useState } from "react";
import type {
    PlaybackSession,
    PlaybackSessionDetail,
} from "~/clients/backend-client.server";
import { PLAYBACK_DATA_ROUTE } from "./playback-api";
import styles from "./playback-card.module.css";
import {
    describeIssues,
    formatBytes,
    formatCount,
    formatMs,
    formatWatchTime,
    plexAttributionBadge,
    plexAttributionTitle,
    shouldShowPlexAttribution,
} from "./playback-view";

export function PlaybackSessionRow({
    session,
    mountPurpose,
}: {
    session: PlaybackSession,
    mountPurpose?: string | null,
}) {
    const [open, setOpen] = useState(false);
    const [detail, setDetail] = useState<PlaybackSessionDetail | null>(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const toggle = useCallback(async () => {
        const next = !open;
        setOpen(next);
        if (!next || detail || loading) return;
        setLoading(true);
        try {
            const response = await fetch(
                `${PLAYBACK_DATA_ROUTE}?id=${encodeURIComponent(session.id)}`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();
            setDetail(data.detail ?? null);
            setError(null);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            setLoading(false);
        }
    }, [open, detail, loading, session.id]);

    const badges = describeIssues(session.issues);
    const showPlexAttribution = shouldShowPlexAttribution(
        session.plexPurpose,
        session.plexConfidence,
        mountPurpose);

    return (
        <div className={styles.sessionCard}>
            <button type="button" className={styles.sessionHeader} onClick={toggle} aria-expanded={open}>
                <span className={styles.sessionTime}>
                    {new Date(session.startedAtMs).toLocaleTimeString()}
                </span>
                <span className={styles.sessionFacts}>
                    <span>{formatWatchTime(session.durationMs)}</span>
                    <span>{formatBytes(session.bytesServed)}</span>
                    {session.requestCount > 0 && <span>{formatCount(session.requestCount)} requests</span>}
                    <span className={styles.sessionReason}>{session.endReason}</span>
                </span>
                <span className={styles.badgeRow}>
                    {showPlexAttribution && session.plexPurpose && (
                        <span
                            className={`${styles.issueBadge} ${styles["issue-info"]}`}
                            title={plexAttributionTitle(
                                session.plexPurpose,
                                session.plexConfidence)}>
                            {plexAttributionBadge(
                                session.plexPurpose,
                                session.plexConfidence)}
                        </span>
                    )}
                    {badges.map(badge => (
                        <span
                            key={badge.key}
                            className={`${styles.issueBadge} ${styles[`issue-${badge.tone}`]}`}
                            title={badge.title}>
                            {badge.label}
                        </span>
                    ))}
                </span>
                <span className={styles.chevronHolder} aria-hidden="true">
                    <svg
                        className={`${styles.detailsChevron} ${open ? styles.detailsChevronOpen : ""}`}
                        viewBox="0 0 16 16">
                        <path
                            d="m4 6 4 4 4-4"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2"
                            strokeLinecap="round"
                            strokeLinejoin="round" />
                    </svg>
                </span>
            </button>

            {open && (
                <div className={styles.sessionDetail}>
                    {loading && <div className={styles.detailEmpty}>Loading article detail…</div>}
                    {error && <div className={styles.errorNote}>Could not load: {error}</div>}
                    {/* "Expired" is only true when retention explains the absence.
                        Telling someone their five-minute-old session has expired
                        sends them looking for a retention bug that isn't there. */}
                    {detail && !detail.articleDetailAvailable && (
                        <div className={styles.detailEmpty}>
                            {detail.articleDetailExpired
                                ? `Per-article detail is kept for ${detail.articleRetentionHours} hours and has `
                                  + "expired for this session. The provider totals above are permanent."
                                : "No per-article records were kept for this session. The provider "
                                  + "totals above are permanent."}
                        </div>
                    )}
                    {detail && detail.articleDetailAvailable && (
                        <div className={styles.tableWrap}>
                            <table className={styles.dataTable}>
                                <thead>
                                    <tr>
                                        <th>Provider</th>
                                        <th>Result</th>
                                        <th className={styles.numeric}>Articles</th>
                                        <th className={styles.numeric}>Avg</th>
                                        <th className={styles.numeric}>Worst</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {detail.articleCounts.map(row => (
                                        <tr key={`${row.providerId}-${row.status}`}>
                                            <td title={row.host}>{row.nickname || row.host}</td>
                                            <td>
                                                <span className={`${styles.statusTag} ${row.status === "Ok" ? styles.statusOk : styles.statusBad}`}>
                                                    {row.status}
                                                </span>
                                            </td>
                                            <td className={styles.numeric}>{formatCount(row.count)}</td>
                                            <td className={styles.numeric}>{formatMs(row.avgDurationMs)}</td>
                                            <td className={styles.numeric}>{formatMs(row.maxDurationMs)}</td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    )}
                </div>
            )}
        </div>
    );
}
