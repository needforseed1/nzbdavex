import type { Route } from "./+types/route";
import styles from "./route.module.css";
import { backendClient, type LiveStatsMessage, type OverviewStatsResponse } from "~/clients/backend-client.server";
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

const topicNames = {
    liveStats: 'ls',
};
const topicSubscriptions = {
    [topicNames.liveStats]: 'state',
};

export async function loader() {
    const stats = await backendClient.getOverviewStats("24h");
    return { stats };
}

export default function Overview({ loaderData }: Route.ComponentProps) {
    const [stats, setStats] = useState<OverviewStatsResponse>(loaderData.stats);
    const [window, setWindow] = useState<"24h" | "7d">("24h");

    const liveTiles = stats.tiles;

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
                    <button
                        role="tab"
                        aria-selected={window === "24h"}
                        className={window === "24h" ? styles.windowActive : styles.windowOption}
                        onClick={() => setWindow("24h")}>24h</button>
                    <button
                        role="tab"
                        aria-selected={window === "7d"}
                        className={window === "7d" ? styles.windowActive : styles.windowOption}
                        onClick={() => setWindow("7d")}>7d</button>
                </div>
            </div>

            <LiveTiles tiles={liveTiles} />

            <LiveReadsPanel />

            <ActivityHeatmap maxCell={stats.heatmap.maxCell} cells={stats.heatmap.cells} />

            <ThroughputChart
                points={stats.throughput}
                totalArticles={stats.totalArticles}
                totalErrors={stats.totalErrors}
                totalBytesServed={stats.sessions.totalBytesServed}
                window={window}
            />

            <LatencyHistogram
                p50Ms={stats.latency.p50Ms}
                p95Ms={stats.latency.p95Ms}
                p99Ms={stats.latency.p99Ms}
                samples={stats.latency.samples}
                buckets={stats.latency.buckets}
            />

            <div className={styles.twoCol}>
                <ErrorDonut errors={stats.errors} />
                <SessionsBlock sessions={stats.sessions} window={window} />
            </div>

            <ProviderScoreboard providers={stats.providers} window={window} />

            <IndexerScoreboard indexers={stats.indexers} />

            <CatalogueBlock catalogue={stats.catalogue} />
        </div>
    );
}
