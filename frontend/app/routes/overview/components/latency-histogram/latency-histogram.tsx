import { useMemo } from "react";
import styles from "./latency-histogram.module.css";
import type { LatencyBucket } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type LatencyHistogramProps = {
    p50Ms: number,
    p95Ms: number,
    p99Ms: number,
    samples: number,
    buckets: LatencyBucket[],
}

export function LatencyHistogram({ p50Ms, p95Ms, p99Ms, samples, buckets }: LatencyHistogramProps) {
    const { maxCount, displayBuckets } = useMemo(() => {
        const max = buckets.reduce((m, b) => Math.max(m, b.count), 0);
        return { maxCount: max, displayBuckets: buckets };
    }, [buckets]);

    const empty = samples === 0;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div>
                    <h3 className={styles.title}>Fetch latency</h3>
                    <div className={styles.sub}>{empty ? "Waiting for fetches" : `${formatNumber(samples)} samples`}</div>
                </div>
                <div className={styles.percentiles}>
                    <Pctile label="p50" ms={p50Ms} kind="ok" />
                    <Pctile label="p95" ms={p95Ms} kind={p95Ms > 1500 ? "warn" : "ok"} />
                    <Pctile label="p99" ms={p99Ms} kind={p99Ms > 5000 ? "danger" : p99Ms > 2000 ? "warn" : "ok"} />
                </div>
            </div>

            {empty ? (
                <div className={styles.empty}>No successful fetches in this window yet.</div>
            ) : (
                <>
                    <div className={styles.bars}>
                        {displayBuckets.map((b, i) => {
                            const h = maxCount > 0 ? (b.count / maxCount) * 100 : 0;
                            return (
                                <div key={i} className={styles.barCol} title={`${formatRange(b.loMs, b.hiMs)} • ${formatNumber(b.count)} fetches`}>
                                    <div className={styles.barWrap}>
                                        <div className={styles.bar} style={{ height: `${h.toFixed(1)}%` }} />
                                    </div>
                                    <div className={styles.barLabel}>{shortLabel(b.loMs)}</div>
                                </div>
                            );
                        })}
                    </div>
                    <div className={styles.legend}>Bucket: lower bound in ms.</div>
                </>
            )}
        </div>
    );
}

function Pctile({ label, ms, kind }: { label: string, ms: number, kind: "ok" | "warn" | "danger" }) {
    const cls = kind === "danger" ? styles.danger : kind === "warn" ? styles.warn : styles.ok;
    return (
        <div className={`${styles.pctile} ${cls}`}>
            <div className={styles.pctileLabel}>{label}</div>
            <div className={styles.pctileValue}>{formatMs(ms)}</div>
        </div>
    );
}

function formatMs(ms: number): string {
    if (ms >= 1000) return `${(ms / 1000).toFixed(1)} s`;
    return `${ms} ms`;
}

function formatRange(lo: number, hi: number): string {
    if (hi >= 1e9) return `≥ ${lo} ms`;
    return `${lo}–${hi} ms`;
}

function shortLabel(lo: number): string {
    if (lo >= 1000) return `${lo / 1000}s`;
    return `${lo}`;
}
