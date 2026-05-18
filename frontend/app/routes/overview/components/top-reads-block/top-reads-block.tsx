import styles from "./top-reads-block.module.css";
import type { TopRead } from "~/clients/backend-client.server";
import { formatBytes, formatNumber } from "../../utils/format";

export type TopReadsBlockProps = {
    reads: TopRead[],
    window: "24h" | "7d",
}

export function TopReadsBlock({ reads, window }: TopReadsBlockProps) {
    const maxReads = reads.reduce((m, r) => Math.max(m, r.reads), 0);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Top reads</h3>
                <div className={styles.sub}>Most-read files, last {window}</div>
            </div>

            {reads.length === 0 ? (
                <div className={styles.empty}>No reads yet.</div>
            ) : (
                <ol className={styles.list}>
                    {reads.map(r => {
                        const sharePct = maxReads > 0 ? (r.reads / maxReads) * 100 : 0;
                        return (
                            <li key={r.path} className={styles.row}>
                                <div className={styles.bar} style={{ width: `${sharePct.toFixed(1)}%` }} />
                                <div className={styles.rowContent}>
                                    <div className={styles.path} title={r.path}>{lastSegment(r.path)}</div>
                                    <div className={styles.stats}>
                                        <span className={styles.statReads}>{formatNumber(r.reads)} {r.reads === 1 ? "read" : "reads"}</span>
                                        <span className={styles.statBytes}>{formatBytes(r.bytesServed)}</span>
                                    </div>
                                </div>
                            </li>
                        );
                    })}
                </ol>
            )}
        </div>
    );
}

function lastSegment(path: string): string {
    const idx = path.lastIndexOf("/");
    if (idx < 0 || idx === path.length - 1) return path;
    return path.slice(idx + 1);
}
