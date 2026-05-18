import { useMemo } from "react";
import styles from "./throughput-chart.module.css";
import type { ThroughputPoint } from "~/clients/backend-client.server";
import { formatBytes } from "../../utils/format";

export type ThroughputChartProps = {
    points: ThroughputPoint[],
    amplification: string,
    window: "24h" | "7d",
}

export function ThroughputChart({ points, amplification, window }: ThroughputChartProps) {
    const { servedPath, fetchedPath, maxValue, totalServed, totalFetched } = useMemo(() => {
        const w = 800;
        const h = 160;
        if (points.length === 0) return { servedPath: "", fetchedPath: "", maxValue: 0, totalServed: 0, totalFetched: 0 };
        const maxValue = Math.max(1, ...points.map(p => Math.max(p.bytesServed, p.bytesFetched)));
        const xStep = points.length > 1 ? w / (points.length - 1) : 0;
        const y = (v: number) => h - (v / maxValue) * h;
        const buildPath = (key: "bytesServed" | "bytesFetched") =>
            points.map((p, i) => `${i === 0 ? "M" : "L"}${(i * xStep).toFixed(1)},${y(p[key]).toFixed(1)}`).join(" ");
        const totalServed = points.reduce((s, p) => s + p.bytesServed, 0);
        const totalFetched = points.reduce((s, p) => s + p.bytesFetched, 0);
        return {
            servedPath: buildPath("bytesServed"),
            fetchedPath: buildPath("bytesFetched"),
            maxValue,
            totalServed,
            totalFetched,
        };
    }, [points]);

    const hasData = points.length > 0 && maxValue > 0;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div>
                    <h3 className={styles.title}>Throughput</h3>
                    <div className={styles.sub}>Bytes served vs fetched, last {window}</div>
                </div>
                <div className={styles.amplification}>
                    <div className={styles.ampLabel}>Read amplification</div>
                    <div className={styles.ampValue}>{amplification}×</div>
                </div>
            </div>

            <div className={styles.legend}>
                <span className={styles.legendItem}><span className={`${styles.swatch} ${styles.swatchServed}`} /> Served {formatBytes(totalServed)}</span>
                <span className={styles.legendItem}><span className={`${styles.swatch} ${styles.swatchFetched}`} /> Fetched {formatBytes(totalFetched)}</span>
            </div>

            {hasData ? (
                <svg viewBox="0 0 800 160" preserveAspectRatio="none" className={styles.svg}>
                    <path d={fetchedPath} className={styles.lineFetched} />
                    <path d={servedPath} className={styles.lineServed} />
                </svg>
            ) : (
                <div className={styles.empty}>No reads yet.</div>
            )}
        </div>
    );
}
