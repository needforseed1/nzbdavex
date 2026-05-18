import styles from "./sessions-block.module.css";
import { formatBytes, formatNumber } from "../../utils/format";

export type SessionsBlockProps = {
    sessions: {
        count: number,
        totalBytesServed: number,
        avgDurationMs: number,
        longestDurationMs: number,
        biggestReadBytes: number,
    },
    window: "24h" | "7d",
}

export function SessionsBlock({ sessions, window }: SessionsBlockProps) {
    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Read sessions</h3>
                <div className={styles.sub}>Last {window}</div>
            </div>

            {sessions.count === 0 ? (
                <div className={styles.empty}>No completed read sessions yet.</div>
            ) : (
                <div className={styles.grid}>
                    <Cell label="Sessions" value={formatNumber(sessions.count)} />
                    <Cell label="Bytes served" value={formatBytes(sessions.totalBytesServed)} />
                    <Cell label="Avg duration" value={formatDuration(sessions.avgDurationMs)} />
                    <Cell label="Longest read" value={formatDuration(sessions.longestDurationMs)} />
                    <Cell label="Biggest single read" value={formatBytes(sessions.biggestReadBytes)} fullWidth />
                </div>
            )}
        </div>
    );
}

function Cell({ label, value, fullWidth }: { label: string, value: string, fullWidth?: boolean }) {
    return (
        <div className={`${styles.cell} ${fullWidth ? styles.full : ""}`}>
            <div className={styles.label}>{label}</div>
            <div className={styles.value}>{value}</div>
        </div>
    );
}

function formatDuration(ms: number): string {
    if (ms < 1000) return `${ms} ms`;
    const s = ms / 1000;
    if (s < 60) return `${s.toFixed(1)} s`;
    const m = s / 60;
    if (m < 60) return `${m.toFixed(1)} m`;
    return `${(m / 60).toFixed(1)} h`;
}
