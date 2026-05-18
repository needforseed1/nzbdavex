import styles from "./provider-scoreboard.module.css";
import type { ProviderRow } from "~/clients/backend-client.server";
import { formatNumber, formatPercent } from "../../utils/format";

export type ProviderScoreboardProps = {
    providers: ProviderRow[],
    window: "24h" | "7d",
}

export function ProviderScoreboard({ providers, window }: ProviderScoreboardProps) {
    const total = providers.reduce((s, p) => s + p.articles, 0);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Providers</h3>
                <div className={styles.sub}>Per-provider fetches, last {window}</div>
            </div>

            {providers.length === 0 ? (
                <div className={styles.empty}>No fetches yet.</div>
            ) : (
                <table className={styles.table}>
                    <thead>
                        <tr>
                            <th>Provider</th>
                            <th className={styles.sparkCol}>Activity</th>
                            <th className={styles.numCol}>Articles</th>
                            <th className={styles.numCol}>Share</th>
                            <th className={styles.numCol}>Errors</th>
                            <th className={styles.numCol}>Retries</th>
                            <th className={styles.numCol}>Avg ms</th>
                        </tr>
                    </thead>
                    <tbody>
                        {providers.map(p => {
                            const share = total > 0 ? (p.articles / total) * 100 : 0;
                            return (
                                <tr key={p.provider}>
                                    <td className={styles.providerCell}>
                                        <span className={styles.dot} />
                                        {p.provider}
                                    </td>
                                    <td className={styles.sparkCol}>
                                        <Sparkline values={p.spark} />
                                    </td>
                                    <td className={styles.numCol}>{formatNumber(p.articles)}</td>
                                    <td className={styles.numCol}>
                                        <div className={styles.shareBar}>
                                            <div className={styles.shareFill} style={{ width: `${share.toFixed(1)}%` }} />
                                            <span className={styles.shareText}>{formatPercent(share, 0)}</span>
                                        </div>
                                    </td>
                                    <td className={`${styles.numCol} ${p.errorRate > 0.05 ? styles.warn : ""}`}>
                                        {formatNumber(p.errors)}
                                        {p.errorRate > 0 && <span className={styles.errorRate}> ({formatPercent(p.errorRate * 100, 1)})</span>}
                                    </td>
                                    <td className={styles.numCol}>{formatNumber(p.retries)}</td>
                                    <td className={styles.numCol}>{p.avgDurationMs.toFixed(0)}</td>
                                </tr>
                            );
                        })}
                    </tbody>
                </table>
            )}
        </div>
    );
}

function Sparkline({ values }: { values: number[] }) {
    if (values.length === 0) return <div className={styles.sparkEmpty} />;
    const w = 110;
    const h = 22;
    const max = Math.max(1, ...values);
    const step = values.length > 1 ? w / (values.length - 1) : 0;
    const y = (v: number) => h - (v / max) * (h - 4) - 2;
    const path = values
        .map((v, i) => `${i === 0 ? "M" : "L"}${(i * step).toFixed(1)},${y(v).toFixed(1)}`)
        .join(" ");
    const area = `${path} L${((values.length - 1) * step).toFixed(1)},${h} L0,${h} Z`;
    return (
        <svg viewBox={`0 0 ${w} ${h}`} className={styles.spark} preserveAspectRatio="none">
            <path d={area} className={styles.sparkArea} />
            <path d={path} className={styles.sparkLine} />
        </svg>
    );
}
