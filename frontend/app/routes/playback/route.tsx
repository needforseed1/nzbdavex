import { useCallback, useEffect, useState } from "react";
import type { Route } from "./+types/route";
import styles from "./playback-layout.module.css";
import {
    backendClient,
    type PlaybackPlay,
} from "~/clients/backend-client.server";
import { ActivePlays, useActiveReads } from "./active-plays";
import {
    PLAYBACK_DATA_ROUTE,
    PLAYBACK_HISTORY_LIMIT,
} from "./playback-api";
import { PlaybackHistory } from "./playback-history";
import { playsEqual } from "./playback-view";

const POLL_INTERVAL_MS = 5000;

export async function loader() {
    return {
        page: await backendClient.getPlaybackSessions(PLAYBACK_HISTORY_LIMIT),
    };
}

export default function Playback({ loaderData }: Route.ComponentProps) {
    const [plays, setPlays] = useState<PlaybackPlay[]>(loaderData.page.plays);
    const [sample, setSample] = useState({
        sampledSessions: loaderData.page.sampledSessions,
        truncated: loaderData.page.truncated,
    });
    const [autoRefresh, setAutoRefresh] = useState(true);
    const [refreshing, setRefreshing] = useState(false);
    const [clearing, setClearing] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const refresh = useCallback(async (silent: boolean = false) => {
        if (!silent) setRefreshing(true);
        try {
            const response = await fetch(
                `${PLAYBACK_DATA_ROUTE}?limit=${PLAYBACK_HISTORY_LIMIT}`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();
            // This crosses an untyped fetch boundary, so the shape is checked
            // rather than trusted: a wrong one used to reach useMemo and take
            // the whole page down with "e is not iterable".
            const next: PlaybackPlay[] = Array.isArray(data?.plays) ? data.plays : [];
            setPlays(previous => playsEqual(previous, next) ? previous : next);
            setSample({
                sampledSessions: data.sampledSessions ?? 0,
                truncated: data.truncated ?? false,
            });
            setError(null);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            if (!silent) setRefreshing(false);
        }
    }, []);

    const clearAll = useCallback(async () => {
        const confirmed = window.confirm(
            "Delete all playback history?\n\n" +
            "The overview page counts its session totals from the same records, " +
            "so those numbers will reset too. This can't be undone.");
        if (!confirmed) return;
        setClearing(true);
        try {
            const response = await fetch(PLAYBACK_DATA_ROUTE, { method: "POST" });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            setPlays([]);
            setError(null);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            setClearing(false);
        }
    }, []);

    useEffect(() => {
        if (!autoRefresh) return;
        let cancelled = false;
        let timer: ReturnType<typeof setTimeout> | null = null;
        const loop = async () => {
            if (cancelled) return;
            await refresh(true);
            if (cancelled) return;
            timer = setTimeout(loop, POLL_INTERVAL_MS);
        };
        timer = setTimeout(loop, POLL_INTERVAL_MS);
        return () => {
            cancelled = true;
            if (timer) clearTimeout(timer);
        };
    }, [autoRefresh, refresh]);

    const activeReads = useActiveReads();

    return (
        <div className={styles.page}>
            <ActivePlays reads={activeReads} />
            <PlaybackHistory
                plays={plays}
                sampledSessions={sample.sampledSessions}
                truncated={sample.truncated}
                autoRefresh={autoRefresh}
                refreshing={refreshing}
                clearing={clearing}
                error={error}
                onToggleAutoRefresh={() => setAutoRefresh(value => !value)}
                onRefresh={() => { void refresh(); }}
                onClear={() => { void clearAll(); }}
            />
        </div>
    );
}
