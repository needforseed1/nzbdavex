import { useMemo } from "react";
import styles from "./throughput-chart.module.css";
import type { ThroughputPoint } from "~/clients/backend-client.server";
import { formatBytes, formatNumber } from "../../utils/format";

export type ThroughputChartProps = {
    points: ThroughputPoint[],
    totalArticles: number,
    totalErrors: number,
    totalBytesServed: number,
    window: "24h" | "7d",
}

export function ThroughputChart({ points, totalArticles, totalErrors, totalBytesServed, window }: ThroughputChartProps) {
    const { articlesPath, errorsPath, maxArticles } = useMemo(() => {
        const w = 800;
        const h = 160;
        if (points.length === 0) return { articlesPath: "", errorsPath: "", maxArticles: 0 };
        const maxArticles = Math.max(1, ...points.map(p => p.articles));
        const xStep = points.length > 1 ? w / (points.length - 1) : 0;
        const y = (v: number) => h - (v / maxArticles) * (h - 8) - 4;
        const buildPath = (key: "articles" | "errors") =>
            points.map((p, i) => `${i === 0 ? "M" : "L"}${(i * xStep).toFixed(1)},${y(p[key]).toFixed(1)}`).join(" ");
        return {
            articlesPath: buildPath("articles"),
            errorsPath: buildPath("errors"),
            maxArticles,
        };
    }, [points]);

    const hasData = points.length > 0 && maxArticles > 0;
    const bucketLabel = window === "7d" ? "hour" : "min";

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div>
                    <h3 className={styles.title}>Activity</h3>
                    <div className={styles.sub}>Articles fetched per {bucketLabel}, last {window}</div>
                </div>
                <div className={styles.totals}>
                    <Total label="Articles" value={formatNumber(totalArticles)} />
                    <Total label="Errors" value={formatNumber(totalErrors)} accent={totalErrors > 0 ? "danger" : undefined} />
                    <Total label="Served" value={formatBytes(totalBytesServed)} />
                </div>
            </div>

            {hasData ? (
                <>
                    <svg viewBox="0 0 800 160" preserveAspectRatio="none" className={styles.svg}>
                        <path d={articlesPath} className={styles.lineArticles} />
                        {totalErrors > 0 && <path d={errorsPath} className={styles.lineErrors} />}
                    </svg>
                    <div className={styles.legend}>
                        <span className={styles.legendItem}><span className={`${styles.swatch} ${styles.swatchArticles}`} /> Articles</span>
                        {totalErrors > 0 && <span className={styles.legendItem}><span className={`${styles.swatch} ${styles.swatchErrors}`} /> Errors</span>}
                        <span className={styles.legendPeak}>peak {formatNumber(maxArticles)} / {bucketLabel}</span>
                    </div>
                </>
            ) : (
                <div className={styles.empty}>
                    No activity in this window yet.
                    <div className={styles.emptySub}>Articles you fetch will appear here.</div>
                </div>
            )}
        </div>
    );
}

function Total({ label, value, accent }: { label: string, value: string, accent?: "danger" }) {
    return (
        <div className={`${styles.total} ${accent === "danger" ? styles.totalDanger : ""}`}>
            <div className={styles.totalLabel}>{label}</div>
            <div className={styles.totalValue}>{value}</div>
        </div>
    );
}
