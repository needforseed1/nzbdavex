import { useEffect, useState } from "react";
import styles from "./live-reads.module.css";
import { receiveMessage } from "~/utils/websocket-util";
import { useNavigate } from "react-router";

const activeReadsTopic = { ar: 'state' };

type ProviderUsage = { host: string; nickname?: string | null; segments: number };
type Read = {
    id: string;
    fileName: string;
    path: string;
    startedAt: number;
    lastActivityAt: number;
    bytesRead: number;
    bytesPerSecond: number;
    fileSize: number | null;
    upstreamStalls: number;
    totalUpstreamStallMs: number;
    downstreamStalls: number;
    zeroFilledSegments: number;
    providers: ProviderUsage[];
};
type Snapshot = { reads: Read[] };

const GENERIC_HOST_PREFIXES = new Set(["news", "reader", "premium", "secure", "ssl", "nntp", "usenet", "block"]);

function stripHost(host: string): string {
    if (!host) return "—";
    const cleanHost = host.replace(/\s*\(\d+%\)\s*$/, "").replace(/:\d+$/, "");
    const labels = cleanHost.split(".").filter(Boolean);
    if (labels.length === 0) return host;
    if (labels.length === 1) return labels[0];
    const identifying = labels.find(label => !GENERIC_HOST_PREFIXES.has(label.toLowerCase()));
    if (identifying) return identifying;
    return labels[0].length >= labels[1].length ? labels[0] : labels[1];
}

function formatRate(bytesPerSecond: number): string {
    if (!Number.isFinite(bytesPerSecond) || bytesPerSecond <= 0) return "0 B/s";
    const units = ["B/s", "KB/s", "MB/s", "GB/s"];
    let value = bytesPerSecond;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }
    return `${value >= 10 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

function formatSeconds(ms: number): string {
    const seconds = ms / 1000;
    return seconds >= 10 ? `${Math.round(seconds)}s` : `${seconds.toFixed(1)}s`;
}

function shortName(name: string): string {
    if (!name) return "—";
    const max = 28;
    return name.length <= max ? name : name.slice(0, max - 1) + "…";
}

export function LiveReads() {
    const navigate = useNavigate();
    const [snapshot, setSnapshot] = useState<Snapshot | null>(null);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => {
                try { setSnapshot(JSON.parse(message)); }
                catch { /* ignore malformed frames */ }
            });
            ws.onopen = () => ws.send(JSON.stringify(activeReadsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            if (e.code == 1008) navigate('/login');
            !disposed && setTimeout(() => connect(), 1000);
            setSnapshot(null);
        }
        return connect();
    }, []);

    const reads = snapshot?.reads ?? [];
    if (reads.length === 0) return null;

    return (
        <div className={styles.container}>
            <div className={styles.title}>
                Active Reads
            </div>
            <div className={styles.list}>
                {reads.map(r => <ReadRow key={r.id} item={r} />)}
            </div>
        </div>
    );
}

function ReadRow({ item }: { item: Read }) {
    const totalSegments = item.providers.reduce((acc, p) => acc + p.segments, 0);
    return (
        <div className={styles.row} title={item.fileName}>
            <div className={styles.fileName}>{shortName(item.fileName)}</div>
            <div className={styles.stats}>
                <span className={styles.rate}>{formatRate(item.bytesPerSecond)}</span>
                {item.totalUpstreamStallMs > 0 && (
                    <span
                        className={styles.waited}
                        title={`${item.upstreamStalls} wait(s) on usenet so far this session`}>
                        waited {formatSeconds(item.totalUpstreamStallMs)} on usenet
                    </span>
                )}
                {/* Not a delay — this part of the file is being served as zeros. */}
                {item.zeroFilledSegments > 0 && (
                    <span
                        className={styles.damaged}
                        title="Articles that could not be fetched and were replaced with zeros.">
                        {item.zeroFilledSegments} article{item.zeroFilledSegments === 1 ? "" : "s"} missing
                    </span>
                )}
            </div>
            <div className={styles.providers}>
                {/* An empty provider list means no articles have been attributed
                    yet, which is how a read starts — not evidence of buffering. */}
                {item.providers.length === 0
                    ? <span className={styles.providersIdle}>starting…</span>
                    : item.providers.map((p, i) => (
                        <span key={p.host} className={styles.providersEntry}>
                            {i > 0 && <span className={styles.providersSep}>·</span>}
                            <span className={styles.providersHost} title={p.host}>{p.nickname?.trim() || stripHost(p.host)}</span>
                            {totalSegments > 0 && (
                                <span className={styles.providersPct}>
                                    {Math.round((p.segments / totalSegments) * 100)}%
                                </span>
                            )}
                        </span>
                    ))}
            </div>
        </div>
    );
}
