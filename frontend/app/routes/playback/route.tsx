import { useCallback, useEffect, useMemo, useState } from "react";
import type { Route } from "./+types/route";
import styles from "./route.module.css";
import {
    backendClient,
    type PlaybackPlay,
    type PlaybackSession,
    type PlaybackSessionDetail,
} from "~/clients/backend-client.server";
import { ProviderSummary } from "~/components/provider-summary/provider-summary";
import {
    computeStats,
    describeClient,
    describeIssues,
    formatAge,
    formatBytes,
    formatCount,
    formatMs,
    formatPct,
    formatRate,
    formatWatchTime,
    matchesFilter,
    playVerdict,
    playVerdictLabel,
    playVerdictTitle,
    playsEqual,
    providerShares,
    summarizeDelays,
    summarizeRetrieval,
    type FilterKey,
    usedBackupProvider,
} from "./playback-view";
import { ActivePlays, useActiveReads } from "./active-plays";

const POLL_INTERVAL_MS = 5000;
const DATA_ROUTE = "/settings/playback-sessions";
const HISTORY_LIMIT = 500;

export async function loader() {
    return { page: await backendClient.getPlaybackSessions(HISTORY_LIMIT) };
}

export default function Playback({ loaderData }: Route.ComponentProps) {
    const [plays, setPlays] = useState<PlaybackPlay[]>(loaderData.page.plays);
    const [sample, setSample] = useState({
        sampledSessions: loaderData.page.sampledSessions,
        truncated: loaderData.page.truncated,
    });
    const [autoRefresh, setAutoRefresh] = useState(true);
    const [filter, setFilter] = useState<FilterKey>("plays");
    const [refreshing, setRefreshing] = useState(false);
    const [clearing, setClearing] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const refresh = useCallback(async (silent: boolean = false) => {
        if (!silent) setRefreshing(true);
        try {
            const response = await fetch(`${DATA_ROUTE}?limit=${HISTORY_LIMIT}`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();
            // This crosses an untyped fetch boundary, so the shape is checked
            // rather than trusted: a wrong one used to reach useMemo and take
            // the whole page down with "e is not iterable".
            const next: PlaybackPlay[] = Array.isArray(data?.plays) ? data.plays : [];
            setPlays(prev => playsEqual(prev, next) ? prev : next);
            setSample({
                sampledSessions: data.sampledSessions ?? 0,
                truncated: data.truncated ?? false,
            });
            setError(null);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            if (!silent) setRefreshing(false);
        }
    }, []);

    const clearAll = useCallback(async () => {
        const confirmed = window.confirm(
            "Delete all playback history?\n\n" +
            "The overview page counts its session totals from the same records, " +
            "so those numbers will reset too. This can't be undone.");
        if (!confirmed) return;
        setClearing(true);
        try {
            const response = await fetch(DATA_ROUTE, { method: "POST" });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            setPlays([]);
            setError(null);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            setClearing(false);
        }
    }, []);

    useEffect(() => {
        if (!autoRefresh) return;
        let cancelled = false;
        let timer: ReturnType<typeof setTimeout> | null = null;
        const loop = async () => {
            if (cancelled) return;
            await refresh(true);
            if (cancelled) return;
            timer = setTimeout(loop, POLL_INTERVAL_MS);
        };
        timer = setTimeout(loop, POLL_INTERVAL_MS);
        return () => {
            cancelled = true;
            if (timer) clearTimeout(timer);
        };
    }, [autoRefresh, refresh]);

    const stats = useMemo(() => computeStats(plays), [plays]);
    const visible = useMemo(
        () => plays.filter(play => matchesFilter(play, filter)),
        [plays, filter]);
    const activeReads = useActiveReads();

    return (
        <div className={styles.page}>
            <ActivePlays reads={activeReads} />

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
                            onClick={() => setAutoRefresh(value => !value)}
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
                            onClick={() => refresh()}
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
                            onClick={clearAll}
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
                {sample.truncated && (
                    <div className={`${styles.notice} ${styles.noticeBar}`}>
                        Counts cover the most recent {formatCount(sample.sampledSessions)} reads.
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
                        {visible.map(play => <PlayCard key={play.key} play={play} />)}
                    </div>
                )}
            </div>
        </div>
    );
}

function PlayCard({ play }: { play: PlaybackPlay }) {
    const [open, setOpen] = useState(false);
    const verdict = playVerdict(play);
    const verdictLabel = playVerdictLabel(play);
    const providers = providerShares(play.providers);
    const client = describeClient(play.clientUserAgent, play.clientIp);
    const backupUsed = usedBackupProvider(play);

    // The row is the click target, but the chevron is the real button: the
    // provider chip opens a popover of its own, and a button nested inside a
    // button is invalid markup and unreachable by keyboard.
    const toggle = () => setOpen(value => !value);

    return (
        <div className={styles.playCard}>
            <div
                className={`${styles.playRow} ${styles.playRowClickable} ${open ? styles.playRowOpen : ""}`}
                onClick={toggle}>
                <span
                    className={`${styles.verdictPill} ${styles[`verdict-${verdict}`]}`}
                    title={playVerdictTitle(play)}>
                    {verdictLabel}
                </span>
                <div className={styles.playIdent}>
                    <div className={styles.playTitle} title={play.nzbName ?? play.title}>{play.title}</div>
                    <div className={styles.playMeta}>
                        {play.isProbe && (
                            <span
                                className={styles.metaBadge}
                                title="Only a few kilobytes were read — a media server scanning the file, not playback.">
                                scan
                            </span>
                        )}
                        {play.category && <span className={styles.metaBadge}>{play.category}</span>}
                        <span className={styles.metaText} title={play.clientUserAgent ?? undefined}>{client}</span>
                        <span className={styles.metaDot} aria-hidden="true">·</span>
                        <span
                            className={styles.timestamp}
                            title={new Date(play.startedAtUnix * 1000).toLocaleString()}>
                            {formatAge(play.startedAtUnix)}
                        </span>
                    </div>
                </div>
                <span className={styles.statGrid}>
                    <StatBox label="Watched" value={formatWatchTime(play.watchedMs)} />
                    <StatBox label="Reached" value={formatPct(play.reachedPct)} />
                    <StatBox label="Served" value={formatBytes(play.bytesServed)} />
                    <StatBox
                        label="To client"
                        value={formatRate(play.avgBytesPerSecond)}
                        title="Average rate the player actually consumed." />
                    <StatBox
                        label="Fetch avg"
                        value={formatRate(play.sourceBytesPerSecond)}
                        // Deliberately not called a provider speed: it is
                        // everything fetched divided by how long the play
                        // lasted, so pauses and prefetch are both in it. It
                        // compares against the client rate, nothing more.
                        title={"Everything fetched from usenet divided by the length of the "
                            + "play — including time spent paused, and bytes read ahead but "
                            + "never sent. Well above the client rate means prefetch ran "
                            + "ahead. It is not a measurement of provider speed."} />
                    <StatBox
                        label="Startup"
                        value={formatMs(play.firstByteMs)}
                        title="How long the first request took to deliver its first byte." />
                </span>
                <span className={styles.rowTail}>
                    {providers.length > 0 && (
                        <ProviderSummary
                            items={providers}
                            heading="Articles served"
                            meta={`${providers.length} provider${providers.length === 1 ? "" : "s"}`}
                        />
                    )}
                    <button
                        type="button"
                        className={styles.detailsToggle}
                        onClick={event => { event.stopPropagation(); toggle(); }}
                        aria-expanded={open}
                        aria-controls={`playback-detail-${play.key}`}
                        aria-label={open ? "Hide play details" : "Show play details"}>
                        <svg
                            className={`${styles.detailsChevron} ${open ? styles.detailsChevronOpen : ""}`}
                            viewBox="0 0 16 16"
                            aria-hidden="true">
                            <path
                                d="m4 6 4 4 4-4"
                                fill="none"
                                stroke="currentColor"
                                strokeWidth="2"
                                strokeLinecap="round"
                                strokeLinejoin="round" />
                        </svg>
                    </button>
                </span>
            </div>

            {open && <PlayDetail id={`playback-detail-${play.key}`} play={play} backupUsed={backupUsed} />}
        </div>
    );
}

function PlayDetail({ id, play, backupUsed }: { id: string, play: PlaybackPlay, backupUsed: boolean }) {
    const delays = summarizeDelays(play.counters);
    const retrieval = summarizeRetrieval(play.counters);
    const sourceDelayed = play.issues.includes("stalled");
    const quiet = delays.length === 0 && retrieval.length === 0;

    return (
        <div className={styles.detailPanel} id={id}>
            {!play.hasDiagnostics && (
                <div className={styles.notice}>
                    This play was recorded before playback diagnostics existed, so stalls,
                    waits and per-provider detail were never captured for it.
                </div>
            )}

            {play.errorNote && <div className={styles.errorNote}>{play.errorNote}</div>}

            {/* The common case is a play that waited on nothing and retried
                nothing. Two headed blocks saying so is a lot of furniture for
                the absence of news, so it collapses to one line. */}
            {quiet && (
                <div className={styles.quietLine}>
                    No waits or retries. Served straight from the first provider.
                </div>
            )}

            <div className={styles.detailGrid}>
                {delays.length > 0 && (
                    <DetailBlock title="Delays">
                        {delays.map(row => (
                            <div key={row.key} className={styles.detailRow}>
                                <span className={styles.detailLabel}>{row.label}</span>
                                <span className={styles.detailValue}>{row.value}</span>
                            </div>
                        ))}
                        {play.counters.upstreamStalls > 0 && (
                            <div className={styles.detailNote}>
                                {sourceDelayed
                                    ? "Source issue threshold: at least 3 source waits, or one lasting "
                                      + "3 seconds. This indicates a buffering risk, not proof that "
                                      + "the player paused."
                                    : "These source waits stayed below the source-issue threshold and "
                                      + "may have been absorbed by the player's buffer."}
                            </div>
                        )}
                    </DetailBlock>
                )}

                {retrieval.length > 0 && (
                    <DetailBlock title="Retrieval">
                        {retrieval.map(row => (
                            <div key={row.key} className={styles.detailRow}>
                                <span className={styles.detailLabel}>{row.label}</span>
                                <span className={styles.detailValue}>{row.value}</span>
                            </div>
                        ))}
                    </DetailBlock>
                )}

                <DetailBlock title="Source">
                    <div className={styles.detailRow}>
                        <span className={styles.detailLabel}>File</span>
                        <span className={styles.detailValue} title={play.path}>{play.title}</span>
                    </div>
                    {play.nzbName && (
                        <div className={styles.detailRow}>
                            <span className={styles.detailLabel}>NZB</span>
                            <span className={styles.detailValue} title={play.nzbName}>{play.nzbName}</span>
                        </div>
                    )}
                    <div className={styles.detailRow}>
                        <span className={styles.detailLabel}>Size</span>
                        <span className={styles.detailValue}>{formatBytes(play.fileSize)}</span>
                    </div>
                    <div className={styles.detailRow}>
                        <span className={styles.detailLabel}>Fetched from usenet</span>
                        <span className={styles.detailValue}>{formatBytes(play.bytesFetched)}</span>
                    </div>
                    <div className={styles.detailRow}>
                        <span className={styles.detailLabel}>Client</span>
                        <span className={styles.detailValue} title={play.clientUserAgent ?? undefined}>
                            {describeClient(play.clientUserAgent, play.clientIp)}
                            {play.clientIp ? ` · ${play.clientIp}` : ""}
                        </span>
                    </div>
                    {/* Off the collapsed row: it is a detail about how the play
                        was served, not part of identifying it. */}
                    {backupUsed && (
                        <div className={styles.detailRow}>
                            <span className={styles.detailLabel}>Backup provider</span>
                            <span
                                className={styles.detailValue}
                                title="A configured backup provider successfully served part of this play.">
                                Served part of this play
                            </span>
                        </div>
                    )}
                </DetailBlock>
            </div>

            {play.providers.length > 0 && (
                <div className={styles.tableWrap}>
                    <table className={styles.dataTable}>
                        <thead>
                            <tr>
                                <th>Provider</th>
                                <th className={styles.numeric}>Articles</th>
                                <th className={styles.numeric}>Bytes</th>
                                <th className={styles.numeric}>Rescued</th>
                                <th className={styles.numeric}>Missing</th>
                                <th className={styles.numeric}>Timeouts</th>
                                <th className={styles.numeric}>Errors</th>
                            </tr>
                        </thead>
                        <tbody>
                            {play.providers.map(provider => (
                                <tr key={provider.providerId}>
                                    <td title={provider.host}>
                                        {provider.nickname || provider.host}
                                        {provider.isBackup && <span className={styles.backupTag}>backup</span>}
                                    </td>
                                    <td className={styles.numeric}>{formatCount(provider.segments)}</td>
                                    <td className={styles.numeric}>{formatBytes(provider.bytes)}</td>
                                    <td className={styles.numeric}>{formatCount(provider.rescued)}</td>
                                    <td className={styles.numeric}>{formatCount(provider.missing)}</td>
                                    <td className={styles.numeric}>{formatCount(provider.timeouts)}</td>
                                    <td className={styles.numeric}>{formatCount(provider.errors)}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}

            <div className={styles.sessionList}>
                <div className={styles.sessionListTitle}>
                    {play.sessions.length} session{play.sessions.length === 1 ? "" : "s"}
                </div>
                {play.sessions.map(session => <SessionRow key={session.id} session={session} />)}
            </div>
        </div>
    );
}

function SessionRow({ session }: { session: PlaybackSession }) {
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
            const response = await fetch(`${DATA_ROUTE}?id=${encodeURIComponent(session.id)}`);
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

function DetailBlock({ title, children }: { title: string, children: React.ReactNode }) {
    return (
        <div className={styles.detailBlock}>
            <div className={styles.detailBlockTitle}>{title}</div>
            {children}
        </div>
    );
}

function StatBox({ label, value, title }: { label: string, value: string, title?: string }) {
    return (
        <span className={styles.statBox} title={title}>
            <span className={styles.statLabel}>{label}</span>
            <span className={styles.statValue}>{value}</span>
        </span>
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
    children: React.ReactNode,
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
