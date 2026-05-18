import styles from "./indexer-scoreboard.module.css";
import type { IndexerRow } from "~/clients/backend-client.server";
import { formatBytes, formatNumber, formatPercent } from "../../utils/format";

export type IndexerScoreboardProps = {
    indexers: IndexerRow[],
}

export function IndexerScoreboard({ indexers }: IndexerScoreboardProps) {
    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Indexers</h3>
                <div className={styles.sub}>Completed vs failed downloads, last 30 days</div>
            </div>

            {indexers.length === 0 ? (
                <div className={styles.empty}>No imports recorded yet.</div>
            ) : (
                <table className={styles.table}>
                    <thead>
                        <tr>
                            <th>Indexer</th>
                            <th className={styles.numCol}>Completed</th>
                            <th className={styles.numCol}>Failed</th>
                            <th className={styles.numCol}>Success</th>
                            <th className={styles.numCol}>Bytes</th>
                            <th className={styles.numCol}>Avg time</th>
                        </tr>
                    </thead>
                    <tbody>
                        {indexers.map(i => (
                            <tr key={i.name}>
                                <td className={styles.nameCell}>{i.name}</td>
                                <td className={styles.numCol}>{formatNumber(i.completed)}</td>
                                <td className={`${styles.numCol} ${i.failed > 0 ? styles.fail : ""}`}>{formatNumber(i.failed)}</td>
                                <td className={styles.numCol}>
                                    <div className={styles.successBar}>
                                        <div className={styles.successFill} style={{ width: `${(i.successRate * 100).toFixed(1)}%` }} />
                                        <span className={styles.successText}>{formatPercent(i.successRate * 100, 0)}</span>
                                    </div>
                                </td>
                                <td className={styles.numCol}>{formatBytes(i.bytesCompleted)}</td>
                                <td className={styles.numCol}>{formatSeconds(i.avgSeconds)}</td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </div>
    );
}

function formatSeconds(s: number): string {
    if (s < 60) return `${s}s`;
    if (s < 3600) return `${(s / 60).toFixed(1)}m`;
    return `${(s / 3600).toFixed(1)}h`;
}
