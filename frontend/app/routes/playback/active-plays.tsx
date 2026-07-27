import { useEffect, useState } from "react";
import { useNavigate } from "react-router";
import { receiveMessage } from "~/utils/websocket-util";
import styles from "./route.module.css";
import { formatBytes, formatMs, formatPct, formatRate, formatWatchTime } from "./playback-view";

const activeReadsTopic = { ar: "state" };

/**
 * A read that is happening right now. Sent by the same broadcaster the
 * dashboard's live strip listens to.
 */
export type ActiveRead = {
    id: string,
    fileName: string,
    path: string,
    startedAt: number,
    lastActivityAt: number,
    bytesRead: number,
    bytesPerSecond: number,
    currentOffset: number,
    fileSize: number | null,
    upstreamStalls: number,
    totalUpstreamStallMs: number,
    upstreamWaitsInProgress: number,
    downstreamStalls: number,
    zeroFilledSegments: number,
    bodyStallRecoveries: number,
    providers: { host: string, nickname?: string | null, segments: number }[],
};

/**
 * History cannot answer "is it bad right now". A session is only written once
 * it has been idle long enough to be considered over, so a play in progress —
 * exactly the one someone opens this page to look at while it misbehaves —
 * would otherwise be missing from it for its entire duration.
 */
export function useActiveReads(): ActiveRead[] {
    const navigate = useNavigate();
    const [reads, setReads] = useState<ActiveRead[]>([]);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, "ws"));
            ws.onmessage = receiveMessage((_, message) => {
                try { setReads(JSON.parse(message).reads ?? []); }
                catch { /* ignore malformed frames */ }
            });
            ws.onopen = () => ws.send(JSON.stringify(activeReadsTopic));
            ws.onerror = () => { ws.close(); };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); };
        }
        function onClose(e: CloseEvent) {
            if (e.code == 1008) navigate("/login");
            !disposed && setTimeout(() => connect(), 1000);
            setReads([]);
        }
        return connect();
    }, []);

    return reads;
}

export function ActivePlays({ reads }: { reads: ActiveRead[] }) {
    if (reads.length === 0) return null;

    return (
        <div className={styles.group}>
            <div className={styles.groupHeader}>
                <div className={styles.groupHeading}>
                    <h2 className={styles.title}>Playing now</h2>
                    <div className={styles.subtitle}>
                        Reads in flight. Each one joins the history below once it finishes.
                    </div>
                </div>
                <span className={styles.liveTag}>
                    <span className={`${styles.liveDot} ${styles.liveDotOn}`} />
                    {reads.length} active
                </span>
            </div>
            <div className={styles.playList}>
                {reads.map(read => <ActiveReadCard key={read.id} read={read} />)}
            </div>
        </div>
    );
}

function ActiveReadCard({ read }: { read: ActiveRead }) {
    const reachedPct = read.fileSize && read.fileSize > 0
        ? (read.currentOffset * 100) / read.fileSize
        : null;
    const providers = read.providers
        .map(p => p.nickname?.trim() || p.host)
        .slice(0, 2)
        .join(" · ");
    // The headline is about the stream right now. A connection that recovered
    // earlier remains useful detail, but must not leave a healthy live stream
    // permanently looking unhealthy.
    const damaged = read.zeroFilledSegments > 0;
    const waiting = read.upstreamWaitsInProgress > 0;
    const recovered = read.bodyStallRecoveries > 0;

    return (
        <div className={styles.playCard}>
            <div className={styles.playRow}>
                <span className={`${styles.verdictPill} ${
                    damaged ? styles["verdict-bad"]
                        : waiting ? styles["verdict-warn"]
                        : styles["verdict-info"]}`}>
                    {damaged ? "Damaged" : waiting ? "Waiting on source" : "Streaming"}
                </span>
                <div className={styles.playIdent}>
                    <div className={styles.playTitle} title={read.path}>{read.fileName}</div>
                    <div className={styles.playMeta}>
                        {providers && <span className={styles.metaText}>{providers}</span>}
                        {providers && <span className={styles.metaDot} aria-hidden="true">·</span>}
                        <span className={styles.timestamp}>
                            {formatWatchTime(Date.now() - read.startedAt)} in
                        </span>
                    </div>
                </div>
                {/* Same six-column track the history rows use, so a live play and
                    the finished play it becomes read as the same object. */}
                <span className={styles.statGrid}>
                    <StatBox label="Rate" value={formatRate(read.bytesPerSecond)} />
                    <StatBox label="Position" value={formatPct(reachedPct)} />
                    <StatBox label="Served" value={formatBytes(read.bytesRead)} />
                    <StatBox
                        label="Waited"
                        value={waiting
                            ? `${read.upstreamWaitsInProgress} now`
                            : read.totalUpstreamStallMs > 0
                                ? formatMs(read.totalUpstreamStallMs)
                            : "—"}
                        title={waiting
                            ? `${read.upstreamWaitsInProgress} source wait(s) in progress · `
                              + `${formatMs(read.totalUpstreamStallMs)} waited in total`
                            : "Cumulative time this read has spent waiting on usenet, "
                              + `across ${read.upstreamStalls} wait(s).`} />
                    {recovered && (
                        <StatBox
                            label="Recovered"
                            value={`${read.bodyStallRecoveries}`}
                            title="Provider connections that stopped mid-article, were replaced, and continued." />
                    )}
                    {damaged && (
                        <StatBox
                            label="Zero-filled"
                            value={`${read.zeroFilledSegments}`}
                            title="Articles that could not be fetched and were replaced with zeros." />
                    )}
                </span>
            </div>
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
