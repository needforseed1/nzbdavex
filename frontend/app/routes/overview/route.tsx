import type { Route } from "./+types/route";
import styles from "./route.module.css";
import { backendClient, type LiveStatsMessage, type OverviewStatsResponse } from "~/clients/backend-client.server";
import { useCallback, useEffect, useMemo, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { LiveTiles } from "./components/live-tiles/live-tiles";
import { ThroughputChart } from "./components/throughput-chart/throughput-chart";
import { ProviderScoreboard } from "./components/provider-scoreboard/provider-scoreboard";
import { CatalogueBlock } from "./components/catalogue-block/catalogue-block";
import { RepairBlock } from "./components/repair-block/repair-block";

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

    // Re-fetch when window changes.
    useEffect(() => {
        let cancelled = false;
        (async () => {
            const res = await fetch(`/api/get-overview-stats?window=${window}`);
            if (!res.ok || cancelled) return;
            const data: OverviewStatsResponse = await res.json();
            if (!cancelled) setStats(data);
        })();
        return () => { cancelled = true; };
    }, [window]);

    // Subscribe to live tile updates.
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
        } catch { /* ignore malformed */ }
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

    const headlineAmp = useMemo(() => stats.readAmplification.toFixed(2), [stats.readAmplification]);

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

            <ThroughputChart points={stats.throughput} amplification={headlineAmp} window={window} />

            <ProviderScoreboard providers={stats.providers} window={window} />

            <div className={styles.twoCol}>
                <RepairBlock repair={stats.repair} />
                <CatalogueBlock catalogue={stats.catalogue} />
            </div>
        </div>
    );
}
