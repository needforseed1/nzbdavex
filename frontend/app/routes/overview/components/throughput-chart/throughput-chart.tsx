import { useMemo, useState, useCallback } from "react";
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

const VB_W = 800;
const VB_H = 160;
const TOP_PAD = 6;
const BOT_PAD = 4;

export function ThroughputChart({ points, totalArticles, totalErrors, totalBytesServed, window }: ThroughputChartProps) {
    const [hoverIdx, setHoverIdx] = useState<number | null>(null);

    const { articlesPath, errorsPath, maxArticles, xPercent, yPercent } = useMemo(() => {
        if (points.length === 0) {
            return {
                articlesPath: "",
                errorsPath: "",
                maxArticles: 0,
                xPercent: (_: number) => 0,
                yPercent: (_: number) => 0,
            };
        }
        const max = Math.max(1, ...points.map(p => p.articles));
        const xStep = points.length > 1 ? VB_W / (points.length - 1) : 0;
        const innerH = VB_H - TOP_PAD - BOT_PAD;
        const y = (v: number) => VB_H - BOT_PAD - (v / max) * innerH;
        const buildPath = (key: "articles" | "errors") =>
            points.map((p, i) => `${i === 0 ? "M" : "L"}${(i * xStep).toFixed(1)},${y(p[key]).toFixed(1)}`).join(" ");

        const xPct = (i: number) => points.length > 1 ? (i / (points.length - 1)) * 100 : 50;
        const yPct = (v: number) => 100 - ((v / max) * (1 - (TOP_PAD + BOT_PAD) / VB_H) * 100 + (BOT_PAD / VB_H) * 100);

        return {
            articlesPath: buildPath("articles"),
            errorsPath: buildPath("errors"),
            maxArticles: max,
            xPercent: xPct,
            yPercent: yPct,
        };
    }, [points]);

    const xTicks = useMemo(() => {
        if (points.length === 0) return [];
        const count = Math.min(5, points.length);
        if (count < 2) return [{ idx: 0, label: formatBucketTime(points[0].bucket, window) }];
        return Array.from({ length: count }, (_, i) => {
            const idx = Math.round((points.length - 1) * (i / (count - 1)));
            return { idx, label: formatBucketTime(points[idx].bucket, window) };
        });
    }, [points, window]);

    const onMove = useCallback((clientX: number, target: HTMLElement) => {
        if (points.length === 0) return;
        const rect = target.getBoundingClientRect();
        const rel = (clientX - rect.left) / rect.width;
        const idx = Math.round(rel * (points.length - 1));
        setHoverIdx(Math.max(0, Math.min(points.length - 1, idx)));
    }, [points.length]);

    const handleMouseMove = (e: React.MouseEvent<HTMLDivElement>) => onMove(e.clientX, e.currentTarget);
    const handleMouseLeave = () => setHoverIdx(null);
    const handleTouchMove = (e: React.TouchEvent<HTMLDivElement>) => {
        const t = e.touches[0];
        if (t) onMove(t.clientX, e.currentTarget);
    };
    const handleTouchStart = (e: React.TouchEvent<HTMLDivElement>) => {
        const t = e.touches[0];
        if (t) onMove(t.clientX, e.currentTarget);
    };

    const hasData = points.length > 0 && maxArticles > 0;
    const bucketLabel = window === "7d" ? "hour" : "min";
    const hover = hoverIdx !== null ? points[hoverIdx] : null;

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
                    <div className={styles.plot}>
                        <div className={styles.yAxis}>
                            <span>{formatNumber(maxArticles)}</span>
                            <span>{formatNumber(Math.round(maxArticles / 2))}</span>
                            <span>0</span>
                        </div>
                        <div
                            className={styles.chartArea}
                            onMouseMove={handleMouseMove}
                            onMouseLeave={handleMouseLeave}
                            onTouchStart={handleTouchStart}
                            onTouchMove={handleTouchMove}
                        >
                            <svg viewBox={`0 0 ${VB_W} ${VB_H}`} preserveAspectRatio="none" className={styles.svg}>
                                {/* faint gridlines */}
                                <line x1="0" y1={(VB_H - BOT_PAD).toFixed(1)} x2={VB_W} y2={(VB_H - BOT_PAD).toFixed(1)} className={styles.gridline} />
                                <line x1="0" y1={(VB_H / 2).toFixed(1)} x2={VB_W} y2={(VB_H / 2).toFixed(1)} className={styles.gridline} />
                                <line x1="0" y1={TOP_PAD.toFixed(1)} x2={VB_W} y2={TOP_PAD.toFixed(1)} className={styles.gridline} />
                                <path d={articlesPath} className={styles.lineArticles} />
                                {totalErrors > 0 && <path d={errorsPath} className={styles.lineErrors} />}
                            </svg>

                            {hover && hoverIdx !== null && (
                                <>
                                    <div className={styles.crosshair} style={{ left: `${xPercent(hoverIdx)}%` }} />
                                    <div
                                        className={styles.hoverDot}
                                        style={{
                                            left: `${xPercent(hoverIdx)}%`,
                                            top: `${yPercent(hover.articles)}%`,
                                        }}
                                    />
                                    {totalErrors > 0 && hover.errors > 0 && (
                                        <div
                                            className={`${styles.hoverDot} ${styles.hoverDotErr}`}
                                            style={{
                                                left: `${xPercent(hoverIdx)}%`,
                                                top: `${yPercent(hover.errors)}%`,
                                            }}
                                        />
                                    )}
                                </>
                            )}
                        </div>
                    </div>

                    <div className={styles.xAxis}>
                        {xTicks.map(t => (
                            <span
                                key={t.idx}
                                className={styles.xTick}
                                style={{ left: `${xPercent(t.idx)}%` }}
                            >
                                {t.label}
                            </span>
                        ))}
                    </div>

                    <div className={styles.legend}>
                        <span className={styles.legendItem}><span className={`${styles.swatch} ${styles.swatchArticles}`} /> Articles</span>
                        {totalErrors > 0 && <span className={styles.legendItem}><span className={`${styles.swatch} ${styles.swatchErrors}`} /> Errors</span>}
                        <span className={styles.legendRight}>
                            {hover && hoverIdx !== null ? (
                                <>
                                    <strong>{formatBucketTime(hover.bucket, window)}</strong>
                                    &nbsp;·&nbsp;{formatNumber(hover.articles)} articles
                                    {hover.errors > 0 && <> · {formatNumber(hover.errors)} errors</>}
                                    {hover.bytesServed > 0 && <> · {formatBytes(hover.bytesServed)} served</>}
                                </>
                            ) : (
                                <>Peak {formatNumber(maxArticles)} / {bucketLabel} · hover for details</>
                            )}
                        </span>
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

function formatBucketTime(ms: number, window: "24h" | "7d"): string {
    const d = new Date(ms);
    const hh = String(d.getHours()).padStart(2, "0");
    const mm = String(d.getMinutes()).padStart(2, "0");
    if (window === "7d") {
        const day = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][d.getDay()];
        return `${day} ${hh}:00`;
    }
    return `${hh}:${mm}`;
}
