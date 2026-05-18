import { useMemo, useState } from "react";
import styles from "./error-donut.module.css";
import type { ErrorSlice } from "~/clients/backend-client.server";
import { formatNumber, formatPercent } from "../../utils/format";

export type ErrorDonutProps = {
    errors: ErrorSlice[],
}

// Severity-aware muted palette. The two grays are for non-critical signals
// (Missing is the most common and just means "article wasn't on the first
// provider"); the rest escalate toward real problems.
const COLORS: Record<string, string> = {
    Missing: "#71717a",  // zinc-500 — info / common
    Other:   "#52525b",  // zinc-600 — unknown
    Timeout: "#ca8a04",  // amber-600 — warning
    Corrupt: "#9333ea",  // purple-600 — distinct
    Network: "#dc2626",  // red-600 — real network issue
    Auth:    "#0284c7",  // sky-600 — blocking / config
};
const DEFAULT_COLOR = "#525252";

export function ErrorBreakdown({ errors }: ErrorDonutProps) {
    const [hover, setHover] = useState<string | null>(null);

    const { total, segments } = useMemo(() => {
        const total = errors.reduce((s, e) => s + e.count, 0);
        const segments = errors.map(e => ({
            ...e,
            fraction: total > 0 ? e.count / total : 0,
            color: COLORS[e.status] ?? DEFAULT_COLOR,
        }));
        return { total, segments };
    }, [errors]);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Error breakdown</h3>
                <div className={styles.sub}>Fetch failures, by type</div>
            </div>

            {total === 0 ? (
                <div className={styles.allClear}>
                    <div className={styles.allClearDot} />
                    <div>
                        <div className={styles.allClearTitle}>All clear</div>
                        <div className={styles.allClearSub}>No fetch errors in this window.</div>
                    </div>
                </div>
            ) : (
                <>
                    <div className={styles.headline}>
                        <span className={styles.headlineCount}>{formatNumber(total)}</span>
                        <span className={styles.headlineLabel}>{total === 1 ? "error" : "errors"}</span>
                    </div>

                    <div
                        className={styles.stack}
                        onMouseLeave={() => setHover(null)}
                        role="img"
                        aria-label={`${total} fetch errors broken down by type`}
                    >
                        {segments.map(s => (
                            <div
                                key={s.status}
                                className={`${styles.stackSeg} ${hover && hover !== s.status ? styles.stackSegDim : ""}`}
                                style={{
                                    flex: s.count,
                                    background: s.color,
                                }}
                                onMouseEnter={() => setHover(s.status)}
                                title={`${s.status}: ${formatNumber(s.count)} (${formatPercent(s.fraction * 100, 1)})`}
                            />
                        ))}
                    </div>

                    <ul className={styles.legend}>
                        {segments.map(s => (
                            <li
                                key={s.status}
                                className={`${styles.legendItem} ${hover === s.status ? styles.legendActive : ""}`}
                                onMouseEnter={() => setHover(s.status)}
                                onMouseLeave={() => setHover(null)}
                            >
                                <span className={styles.swatch} style={{ background: s.color }} />
                                <span className={styles.legendLabel}>{s.status}</span>
                                <span className={styles.legendCount}>{formatNumber(s.count)}</span>
                                <span className={styles.legendPct}>{formatPercent(s.fraction * 100, 0)}</span>
                            </li>
                        ))}
                    </ul>
                </>
            )}
        </div>
    );
}

// Backwards-compatible export so the existing import name keeps working.
export { ErrorBreakdown as ErrorDonut };
