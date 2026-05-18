import { useMemo } from "react";
import styles from "./error-donut.module.css";
import type { ErrorSlice } from "~/clients/backend-client.server";
import { formatNumber, formatPercent } from "../../utils/format";

export type ErrorDonutProps = {
    errors: ErrorSlice[],
}

const COLORS: Record<string, string> = {
    Missing: "#d97706",
    Timeout: "#dc2626",
    Corrupt: "#7c3aed",
    Auth: "#0ea5e9",
    Network: "#ec4899",
    Other: "#64748b",
};

export function ErrorDonut({ errors }: ErrorDonutProps) {
    const { total, segments } = useMemo(() => {
        const total = errors.reduce((s, e) => s + e.count, 0);
        let offset = 0;
        const segments = errors.map(e => {
            const fraction = total > 0 ? e.count / total : 0;
            const seg = { ...e, fraction, offset, color: COLORS[e.status] ?? COLORS.Other };
            offset += fraction;
            return seg;
        });
        return { total, segments };
    }, [errors]);

    // SVG donut params.
    const size = 160;
    const radius = 64;
    const stroke = 22;
    const c = 2 * Math.PI * radius;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Error breakdown</h3>
                <div className={styles.sub}>Fetch failures, by type</div>
            </div>

            {total === 0 ? (
                <div className={styles.allClear}>
                    <div className={styles.dot} />
                    <div>
                        <div className={styles.allClearTitle}>All clear</div>
                        <div className={styles.allClearSub}>No fetch errors in this window.</div>
                    </div>
                </div>
            ) : (
                <div className={styles.body}>
                    <div className={styles.donutWrap}>
                        <svg viewBox={`0 0 ${size} ${size}`} className={styles.donut}>
                            <circle
                                cx={size / 2} cy={size / 2} r={radius}
                                fill="none" stroke="rgba(255,255,255,0.06)" strokeWidth={stroke}
                            />
                            {segments.map((s, i) => (
                                <circle
                                    key={i}
                                    cx={size / 2} cy={size / 2} r={radius}
                                    fill="none"
                                    stroke={s.color}
                                    strokeWidth={stroke}
                                    strokeDasharray={`${(s.fraction * c).toFixed(2)} ${c.toFixed(2)}`}
                                    strokeDashoffset={(-s.offset * c).toFixed(2)}
                                    transform={`rotate(-90 ${size / 2} ${size / 2})`}
                                />
                            ))}
                            <text
                                x={size / 2} y={size / 2 - 6}
                                textAnchor="middle"
                                className={styles.donutTotal}>
                                {formatNumber(total)}
                            </text>
                            <text
                                x={size / 2} y={size / 2 + 12}
                                textAnchor="middle"
                                className={styles.donutCaption}>
                                errors
                            </text>
                        </svg>
                    </div>
                    <ul className={styles.legend}>
                        {segments.map(s => (
                            <li key={s.status} className={styles.legendItem}>
                                <span className={styles.swatch} style={{ background: s.color }} />
                                <span className={styles.legendLabel}>{s.status}</span>
                                <span className={styles.legendCount}>{formatNumber(s.count)}</span>
                                <span className={styles.legendPct}>{formatPercent(s.fraction * 100, 0)}</span>
                            </li>
                        ))}
                    </ul>
                </div>
            )}
        </div>
    );
}
