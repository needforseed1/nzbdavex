import { useMemo, useState } from "react";
import styles from "./activity-heatmap.module.css";
import type { HeatmapCell } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type ActivityHeatmapProps = {
    maxCell: number,
    cells: HeatmapCell[],
}

const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

export function ActivityHeatmap({ maxCell, cells }: ActivityHeatmapProps) {
    const [hover, setHover] = useState<{ day: number, hour: number, count: number } | null>(null);

    const lookup = useMemo(() => {
        const m = new Map<string, number>();
        for (const c of cells) m.set(`${c.day}-${c.hour}`, c.count);
        return m;
    }, [cells]);

    const total = useMemo(() => cells.reduce((s, c) => s + c.count, 0), [cells]);
    const empty = total === 0;

    const peak = useMemo(() => {
        let best: HeatmapCell | null = null;
        for (const c of cells) if (!best || c.count > best.count) best = c;
        return best;
    }, [cells]);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div>
                    <h3 className={styles.title}>Activity heatmap</h3>
                    <div className={styles.sub}>Articles per hour, last 7 days</div>
                </div>
                {peak && peak.count > 0 && (
                    <div className={styles.peak}>
                        <span className={styles.peakLabel}>Peak</span>
                        <span className={styles.peakValue}>
                            {DAYS[peak.day]} {String(peak.hour).padStart(2, "0")}:00
                        </span>
                        <span className={styles.peakCount}>{formatNumber(peak.count)} articles</span>
                    </div>
                )}
            </div>

            {empty ? (
                <div className={styles.empty}>No activity in the last 7 days yet.</div>
            ) : (
                <>
                    <div className={styles.grid}>
                        {DAYS.map((label, day) => (
                            <div key={day} className={styles.row}>
                                <div className={styles.dayLabel}>{label}</div>
                                <div className={styles.cellRow}>
                                    {Array.from({ length: 24 }, (_, hour) => {
                                        const count = lookup.get(`${day}-${hour}`) ?? 0;
                                        const intensity = maxCell > 0 ? count / maxCell : 0;
                                        return (
                                            <div
                                                key={hour}
                                                className={styles.cell}
                                                style={{ backgroundColor: cellColor(intensity) }}
                                                onMouseEnter={() => setHover({ day, hour, count })}
                                                onMouseLeave={() => setHover(h => (h && h.day === day && h.hour === hour ? null : h))}
                                            />
                                        );
                                    })}
                                </div>
                            </div>
                        ))}
                        <div className={styles.axisRow}>
                            <div className={styles.dayLabel} aria-hidden />
                            <div className={styles.axisInner}>
                                <span>00</span>
                                <span>06</span>
                                <span>12</span>
                                <span>18</span>
                                <span>23</span>
                            </div>
                        </div>
                    </div>

                    <div className={styles.footer}>
                        <div className={styles.tooltip}>
                            {hover ? (
                                <>
                                    {DAYS[hover.day]} {String(hover.hour).padStart(2, "0")}:00 &mdash;{" "}
                                    {formatNumber(hover.count)} {hover.count === 1 ? "article" : "articles"}
                                </>
                            ) : (
                                <>Hover a cell for details</>
                            )}
                        </div>
                        <div className={styles.scale}>
                            <span>Less</span>
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(0) }} />
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(0.25) }} />
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(0.5) }} />
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(0.75) }} />
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(1) }} />
                            <span>More</span>
                        </div>
                    </div>
                </>
            )}
        </div>
    );
}

function cellColor(intensity: number): string {
    if (intensity <= 0) return "rgba(255,255,255,0.04)";
    const eased = Math.pow(Math.min(1, intensity), 0.6);
    const alpha = 0.15 + eased * 0.75;
    return `rgba(52, 211, 153, ${alpha.toFixed(3)})`;
}
