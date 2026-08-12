import { useState, type ReactNode } from "react";
import type { PlaybackPlay } from "~/clients/backend-client.server";
import styles from "./playback-card.module.css";
import { PlaybackSessionRow } from "./playback-session-row";
import { PlaybackStat } from "./playback-stat";
import {
    describeClient,
    formatAge,
    formatBytes,
    formatCount,
    formatMs,
    formatPct,
    formatRate,
    formatWatchTime,
    mountPurposeLabel,
    mountPurposeTitle,
    playVerdict,
    playVerdictLabel,
    playVerdictTitle,
    plexAttributionBadge,
    plexAttributionTitle,
    plexClientLabel,
    shouldShowPlexAttribution,
    shouldShowNzbName,
    summarizeDelays,
    summarizeRetrieval,
    submissionSourceLabel,
    usedBackupProvider,
} from "./playback-view";

export function PlaybackCard({ play }: { play: PlaybackPlay }) {
    const [open, setOpen] = useState(false);
    const verdict = playVerdict(play);
    const verdictLabel = playVerdictLabel(play);
    const client = describeClient(play.clientUserAgent, play.clientIp);
    const showPlexAttribution = shouldShowPlexAttribution(
        play.plexPurpose,
        play.plexConfidence,
        play.mountPurpose);
    const plexClient = showPlexAttribution
        ? plexClientLabel(
            play.plexProduct,
            play.plexPlatform,
            play.plexPlayer)
        : "";
    const mountLabel = mountPurposeLabel(play.mountPurpose, play.submissionSource);
    const mountTitle = mountPurposeTitle(play);
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
                <div className={styles.playIdent}>
                    <span
                        className={`${styles.verdictPill} ${styles[`verdict-${verdict}`]}`}
                        title={playVerdictTitle(play)}>
                        {verdictLabel}
                    </span>
                    <div className={styles.playTitle} title={play.nzbName ?? play.title}>{play.title}</div>
                    <div className={styles.playMeta}>
                        {mountLabel && (
                            <span className={styles.metaBadge} title={mountTitle ?? undefined}>
                                {mountLabel}
                            </span>
                        )}
                        {showPlexAttribution && play.plexPurpose && (
                            <span
                                className={styles.metaBadge}
                                title={plexAttributionTitle(
                                    play.plexPurpose,
                                    play.plexConfidence)}>
                                {plexAttributionBadge(
                                    play.plexPurpose,
                                    play.plexConfidence)}
                            </span>
                        )}
                        {!mountLabel && play.isLikelyBackgroundActivity && (
                            <span
                                className={styles.metaBadge}
                                title="The rclone access pattern strongly suggests background work: repeated tail probes or concurrent large reads. The originating application is unknown.">
                                Likely background
                            </span>
                        )}
                        {!mountLabel && !play.isLikelyBackgroundActivity && play.isProbe && (
                            <span
                                className={styles.metaBadge}
                                title={play.isRcloneActivity
                                    ? "Only a tiny part of the file was requested through rclone. The originating application and exact purpose are unknown."
                                    : "Only a tiny part of the file was read by a direct WebDAV client. Its exact purpose is unknown, but it does not look like playback."}>
                                Probe
                            </span>
                        )}
                        {backupUsed && (
                            <span
                                className={styles.backupFlag}
                                title="A configured backup provider served part of this playback.">
                                Backup used
                            </span>
                        )}
                        {play.category && <span className={styles.metaBadge}>{play.category}</span>}
                        {plexClient && (
                            <span className={styles.metaText} title={plexClient}>
                                {plexClient}
                            </span>
                        )}
                        {plexClient && <span className={styles.metaDot} aria-hidden="true">·</span>}
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
                    <PlaybackStat
                        label={play.isReliablePlayback ? "Watched" : "Active"}
                        value={formatWatchTime(play.watchedMs)}
                        title={!play.isReliablePlayback
                            ? "Combined time spent serving requests; this does not imply somebody watched the file."
                            : undefined} />
                    {play.isRcloneActivity ? (
                        <PlaybackStat
                            label="Fetched"
                            value={formatBytes(play.bytesFetched)}
                            title="Bytes downloaded from Usenet providers for this activity. Cache hits can make this lower than the bytes served." />
                    ) : (
                        <PlaybackStat label="Reached" value={formatPct(play.reachedPct)} />
                    )}
                    <PlaybackStat label="Served" value={formatBytes(play.bytesServed)} />
                    <PlaybackStat
                        label="Fetch avg"
                        value={formatRate(play.sourceBytesPerSecond)}
                        // Deliberately not called a provider speed: it is
                        // everything fetched divided by how long the activity
                        // lasted, so pauses and prefetch are both in it. It
                        // compares against the client rate, nothing more.
                        title={"Everything fetched from usenet divided by the length of the "
                            + "activity — including time spent paused, and bytes read ahead but "
                            + "never sent. Well above the client rate means prefetch ran "
                            + "ahead. It is not a measurement of provider speed."} />
                    <PlaybackStat
                        label="To client"
                        value={formatRate(play.avgBytesPerSecond)}
                        title="Average rate the requesting client consumed." />
                    <PlaybackStat
                        label="Startup"
                        value={formatMs(play.firstByteMs)}
                        title="How long the first request took to deliver its first byte." />
                </span>
                <span className={styles.rowTail}>
                    <button
                        type="button"
                        className={styles.detailsHint}
                        onClick={event => { event.stopPropagation(); toggle(); }}
                        aria-expanded={open}
                        aria-controls={`playback-detail-${play.key}`}
                        aria-label={open ? "Hide activity details" : "Show activity details"}>
                        <span className={styles.detailsHintLabel}>Stats</span>
                        <span className={styles.detailsToggle} aria-hidden="true">
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
                </span>
            </div>

            {open && (
                <PlaybackDetail
                    id={`playback-detail-${play.key}`}
                    play={play}
                    backupUsed={backupUsed}
                />
            )}
        </div>
    );
}

function PlaybackDetail({
    id,
    play,
    backupUsed,
}: {
    id: string,
    play: PlaybackPlay,
    backupUsed: boolean,
}) {
    const delays = summarizeDelays(play.counters);
    const retrieval = summarizeRetrieval(play.counters);
    const quiet = delays.length === 0 && retrieval.length === 0;
    const mountLabel = mountPurposeLabel(play.mountPurpose, play.submissionSource);
    const mountTitle = mountPurposeTitle(play);
    const submissionSource = submissionSourceLabel(play.submissionSource);
    const showNzbName = shouldShowNzbName(play.title, play.nzbName);
    const showPlexAttribution = shouldShowPlexAttribution(
        play.plexPurpose,
        play.plexConfidence,
        play.mountPurpose);

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

                <DetailBlock title="Details">
                    {showNzbName && play.nzbName && (
                        <div className={styles.detailRow}>
                            <span className={styles.detailLabel}>NZB</span>
                            <span className={styles.detailValue} title={play.nzbName}>{play.nzbName}</span>
                        </div>
                    )}
                    {submissionSource && (
                        <div className={styles.detailRow}>
                            <span className={styles.detailLabel}>Added by</span>
                            <span className={styles.detailValue}>{submissionSource}</span>
                        </div>
                    )}
                    <div className={styles.detailRow}>
                        <span className={styles.detailLabel}>Size</span>
                        <span className={styles.detailValue}>{formatBytes(play.fileSize)}</span>
                    </div>
                    {play.averageReadAheadBytes != null && (
                        <div className={styles.detailRow}>
                            <span className={styles.detailLabel}>Read-ahead avg</span>
                            <span
                                className={styles.detailValue}
                                title="Time-weighted data queued ahead of the article being read.">
                                {formatBytes(play.averageReadAheadBytes)}
                            </span>
                        </div>
                    )}
                    {play.averageReadAheadBytes != null && (
                        <div className={styles.detailRow}>
                            <span className={styles.detailLabel}>Read-ahead min</span>
                            <span
                                className={styles.detailValue}
                                title="Lowest queued data sustained for at least one second after the buffer first reached its configured target. Brief dips, startup, and the final EOF drain are excluded.">
                                {play.minimumReadAheadBytes == null
                                    ? "Target not reached"
                                    : play.minimumReadAheadBytes === 0
                                    ? "0 B"
                                    : formatBytes(play.minimumReadAheadBytes)}
                            </span>
                        </div>
                    )}
                    {mountLabel && (
                        <>
                            <div className={styles.detailRow}>
                                <span className={styles.detailLabel}>Mount purpose</span>
                                <span className={styles.detailValue}>{mountLabel}</span>
                            </div>
                            <div className={styles.detailRow}>
                                <span className={styles.detailLabel}>Mount evidence</span>
                                <span className={styles.detailValue}>{mountTitle}</span>
                            </div>
                        </>
                    )}
                    {showPlexAttribution && (play.plexDetail || play.plexIsTranscode) && (
                        <div className={styles.detailRow}>
                            <span className={styles.detailLabel}>Plex media</span>
                            <span className={styles.detailValue}>
                                {play.plexDetail ?? "Transcoding"}
                                {play.plexDetail && play.plexIsTranscode ? " · transcoding" : ""}
                            </span>
                        </div>
                    )}
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
                {play.sessions.map(session => (
                <PlaybackSessionRow
                    key={session.id}
                    session={session}
                    mountPurpose={play.mountPurpose}
                />
                ))}
            </div>
        </div>
    );
}

function DetailBlock({ title, children }: { title: string, children: ReactNode }) {
    return (
        <div className={styles.detailBlock}>
            <div className={styles.detailBlockTitle}>{title}</div>
            {children}
        </div>
    );
}
