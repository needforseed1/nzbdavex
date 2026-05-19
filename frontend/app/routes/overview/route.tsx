import type { Route } from "./+types/route";
import styles from "./route.module.css";
import { backendClient, type LiveStatsMessage, type OverviewStatsResponse, type OverviewWindow } from "~/clients/backend-client.server";
import { useCallback, useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { LiveTiles } from "./components/live-tiles/live-tiles";
import { LiveReadsPanel } from "./components/live-reads-panel/live-reads-panel";
import { ActivityHeatmap } from "./components/activity-heatmap/activity-heatmap";
import { ThroughputChart } from "./components/throughput-chart/throughput-chart";
import { LatencyHistogram } from "./components/latency-histogram/latency-histogram";
import { ErrorDonut } from "./components/error-donut/error-donut";
import { ProviderScoreboard } from "./components/provider-scoreboard/provider-scoreboard";
import { IndexerScoreboard } from "./components/indexer-scoreboard/indexer-scoreboard";
import { SessionsBlock } from "./components/sessions-block/sessions-block";
import { CatalogueBlock } from "./components/catalogue-block/catalogue-block";
import { LifetimeBlock } from "./components/lifetime-block/lifetime-block";
import { RecordsBlock } from "./components/records-block/records-block";

const topicNames = {
    liveStats: 'ls',
};
const topicSubscriptions = {
    [topicNames.liveStats]: 'state',
};

const WINDOWS: { value: OverviewWindow, label: string }[] = [
    { value: "24h", label: "24h" },
    { value: "7d", label: "7d" },
    { value: "30d", label: "30d" },
    { value: "all", label: "All" },
];

export async function loader() {
    const stats = await backendClient.getOverviewStats("24h");
    return { stats };
}

export default function Overview({ loaderData }: Route.ComponentProps) {
    const [stats, setStats] = useState<OverviewStatsResponse>(loaderData.stats);
    const [window, setWindow] = useState<OverviewWindow>("24h");

    const liveTiles = stats.tiles;
    const isLongWindow = window === "30d" || window === "all";

    // Re-fetch on window change + every 30s so chart, heatmap, providers, etc.
    // stay fresh without manual refresh. Skipped when the tab is hidden so
    // background tabs don't churn the backend; an immediate refetch fires when
    // the tab becomes visible again.
    useEffect(() => {
        let cancelled = false;
        const refetch = async () => {
            if (typeof document !== "undefined" && document.hidden) return;
            try {
                const res = await fetch(`/api/get-overview-stats?window=${window}`);
                if (!res.ok || cancelled) return;
                const data: OverviewStatsResponse = await res.json();
                if (!cancelled) setStats(data);
            } catch { /* network blip, retry next tick */ }
        };
        refetch();
        const interval = setInterval(refetch, 30_000);
        const onVisible = () => { if (!document.hidden) refetch(); };
        document.addEventListener("visibilitychange", onVisible);
        return () => {
            cancelled = true;
            clearInterval(interval);
            document.removeEventListener("visibilitychange", onVisible);
        };
    }, [window]);

    const onWsMessage = useCallback((topic: string, message: string) => {
        if (topic !== topicNames.liveStats) return;
        try {
            const live: LiveStatsMessage = JSON.parse(message);
            setStats(s => ({
                ...s,
                tiles: {
                    activeReads: live.activeReads,
                    articlesPerMinute: live.articlesPerMinute,
                    errorsPerMinute: live.errorsPerMinute,
                    bytesServedPerMinute: live.bytesServedPerMinute,
                }
            }));
        } catch { /* ignore */ }
    }, []);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(globalThis.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage(onWsMessage);
            ws.onopen = () => { ws.send(JSON.stringify(topicSubscriptions)); };
            ws.onclose = () => { if (!disposed) setTimeout(connect, 1000); };
            ws.onerror = () => { ws.close(); };
        }
        connect();
        return () => { disposed = true; ws?.close(); };
    }, [onWsMessage]);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h2 className={styles.title}>Overview</h2>
                <div className={styles.windowToggle} role="tablist">
                    {WINDOWS.map(w => (
                        <button
                            key={w.value}
                            role="tab"
                            aria-selected={window === w.value}
                            className={window === w.value ? styles.windowActive : styles.windowOption}
                            onClick={() => setWindow(w.value)}>{w.label}</button>
                    ))}
                </div>
            </div>

            <LiveTiles tiles={liveTiles} />

            <LiveReadsPanel />

            <ThroughputChart
                points={stats.throughput}
                totalArticles={stats.totalArticles}
                totalErrors={stats.totalErrors}
                totalBytesServed={stats.sessions.totalBytesServed}
                window={window}
            />

            {!isLongWindow && (
                <ActivityHeatmap maxCell={stats.heatmap.maxCell} cells={stats.heatmap.cells} />
            )}

            {!isLongWindow && (
                <LatencyHistogram
                    p50Ms={stats.latency.p50Ms}
                    p95Ms={stats.latency.p95Ms}
                    p99Ms={stats.latency.p99Ms}
                    samples={stats.latency.samples}
                    buckets={stats.latency.buckets}
                />
            )}

            <div className={styles.twoCol}>
                {!isLongWindow && <ErrorDonut errors={stats.errors} />}
                <SessionsBlock sessions={stats.sessions} window={window} />
            </div>

            <ProviderScoreboard providers={stats.providers} window={window} />

            <IndexerScoreboard indexers={stats.indexers} />

            <div className={styles.twoCol}>
                <RecordsBlock records={stats.records} />
                <CatalogueBlock catalogue={stats.catalogue} />
            </div>

            <LifetimeBlock lifetime={stats.lifetime} />
        </div>
    );
}
