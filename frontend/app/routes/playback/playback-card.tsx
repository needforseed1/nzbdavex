import { useState, type ReactNode } from "react";
import type { PlaybackPlay } from "~/clients/backend-client.server";
import { ProviderSummary } from "~/components/provider-summary/provider-summary";
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
    playVerdict,
    playVerdictLabel,
    playVerdictTitle,
    providerShares,
    summarizeDelays,
    summarizeRetrieval,
    usedBackupProvider,
} from "./playback-view";

export function PlaybackCard({ play }: { play: PlaybackPlay }) {
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
                    <PlaybackStat label="Watched" value={formatWatchTime(play.watchedMs)} />
                    <PlaybackStat label="Reached" value={formatPct(play.reachedPct)} />
                    <PlaybackStat label="Served" value={formatBytes(play.bytesServed)} />
                    <PlaybackStat
                        label="To client"
                        value={formatRate(play.avgBytesPerSecond)}
                        title="Average rate the player actually consumed." />
                    <PlaybackStat
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
                    <PlaybackStat
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
                {play.sessions.map(session => (
                    <PlaybackSessionRow key={session.id} session={session} />
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
