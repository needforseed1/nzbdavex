import { useEffect, useState } from "react";
import styles from "./indexer-api-usage.module.css";
import type { IndexerApiUsageRow } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type IndexerApiUsageProps = {
    rows: IndexerApiUsageRow[],
};

export function IndexerApiUsage({ rows }: IndexerApiUsageProps) {
    // Tick once a minute so the "resets in Xm" label stays roughly fresh without
    // forcing a backend roundtrip; the actual hit counts only refresh on the
    // overview's 30s poll, which is plenty for a daily/24h cap.
    const [now, setNow] = useState(() => Date.now());
    useEffect(() => {
        const id = setInterval(() => setNow(Date.now()), 60_000);
        return () => clearInterval(id);
    }, []);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Indexer API usage</h3>
                <div className={styles.sub}>Hits in the current reset window per indexer</div>
            </div>

            {rows.length === 0 ? (
                <div className={styles.empty}>No enabled indexers configured.</div>
            ) : (
                <div className={styles.tableWrap}>
                    <table className={styles.table}>
                        <thead>
                            <tr>
                                <th>Indexer</th>
                                <th className={styles.barCol}>API hits</th>
                                <th className={styles.barCol}>Downloads</th>
                                <th className={styles.resetCol}>Next reset</th>
                            </tr>
                        </thead>
                        <tbody>
                            {rows.map(r => (
                                <tr key={r.name}>
                                    <td className={styles.nameCell}>
                                        <span className={styles.nameInner} title={r.name}>{r.name}</span>
                                    </td>
                                    <td className={styles.barCol}>
                                        <UsageBar used={r.apiHits} limit={r.apiHitLimit} />
                                    </td>
                                    <td className={styles.barCol}>
                                        <UsageBar used={r.downloadHits} limit={r.downloadHitLimit} />
                                    </td>
                                    <td className={styles.resetCol}>
                                        {formatReset(r.resetAtMs, r.resetHourUtc, now)}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}
        </div>
    );
}

function UsageBar({ used, limit }: { used: number, limit: number | null | undefined }) {
    if (!limit || limit <= 0) {
        return (
            <div className={styles.usageRow}>
                <div className={styles.usageBar} title="No limit configured">
                    <div className={styles.usageFillInfinite} />
                </div>
                <span className={styles.usageText}>
                    {formatNumber(used)}<span className={styles.usageMuted}> · unlimited</span>
                </span>
            </div>
        );
    }
    const pct = Math.min(100, (used / limit) * 100);
    const near = pct >= 80 && pct < 100;
    const over = pct >= 100;
    const fillClass = over ? styles.usageFillOver : near ? styles.usageFillNear : styles.usageFill;
    return (
        <div className={styles.usageRow}>
            <div className={styles.usageBar}>
                <div className={fillClass} style={{ width: `${pct}%` }} />
            </div>
            <span className={styles.usageText}>
                {formatNumber(used)}<span className={styles.usageMuted}> / {formatNumber(limit)}</span>
            </span>
        </div>
    );
}

function formatReset(resetAtMs: number, resetHourUtc: number | null | undefined, nowMs: number): string {
    const remaining = resetAtMs - nowMs;
    if (remaining <= 0) return "now";
    const totalMinutes = Math.floor(remaining / 60_000);
    const days = Math.floor(totalMinutes / (24 * 60));
    const hours = Math.floor((totalMinutes % (24 * 60)) / 60);
    const mins = totalMinutes % 60;

    let countdown: string;
    if (days > 0) countdown = `${days}d ${hours}h`;
    else if (hours > 0) countdown = `${hours}h ${mins}m`;
    else countdown = `${Math.max(1, mins)}m`;

    const suffix = typeof resetHourUtc === "number"
        ? ` (${pad2(resetHourUtc)}:00 UTC)`
        : "";
    return `in ${countdown}${suffix}`;
}

function pad2(n: number): string {
    return n < 10 ? `0${n}` : `${n}`;
}
