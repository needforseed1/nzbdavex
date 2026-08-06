import { useCallback, useEffect, useState } from "react";
import type { Route } from "./+types/route";
import styles from "./playback-layout.module.css";
import {
    backendClient,
    type PlaybackHistoryPage,
    type PlaybackPlay,
    type PlexStatus,
} from "~/clients/backend-client.server";
import { ActivePlays, useActiveReads } from "./active-plays";
import {
    DEEP_PLAYBACK_HISTORY_LIMIT,
    PLAYBACK_DATA_ROUTE,
    PLAYBACK_HISTORY_LIMIT,
} from "./playback-api";
import { PlaybackHistory } from "./playback-history";
import { playsEqual, type FilterKey } from "./playback-view";

const POLL_INTERVAL_MS = 5000;

export async function loader() {
    return {
        page: await backendClient.getPlaybackSessions(PLAYBACK_HISTORY_LIMIT),
    };
}

export default function Playback({ loaderData }: Route.ComponentProps) {
    const [plays, setPlays] = useState<PlaybackPlay[]>(loaderData.page.plays);
    const [filter, setFilter] = useState<FilterKey>("mount");
    const [retainedPlayback, setRetainedPlayback] =
        useState<PlaybackHistoryPage | null>(null);
    const [retainedPlaybackRefreshing, setRetainedPlaybackRefreshing] = useState(false);
    const [retainedPlaybackError, setRetainedPlaybackError] = useState<string | null>(null);
    const [plexStatus, setPlexStatus] = useState<PlexStatus>(loaderData.page.plexStatus);
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
            setPlexStatus(data?.plexStatus ?? { enabled: false, connected: false });
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

    const refreshRetainedPlayback = useCallback(async () => {
        setRetainedPlaybackRefreshing(true);
        try {
            const params = new URLSearchParams({
                limit: String(DEEP_PLAYBACK_HISTORY_LIMIT),
                filter: "playback",
                deep: "true",
            });
            const response = await fetch(`${PLAYBACK_DATA_ROUTE}?${params}`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();
            setRetainedPlayback({
                plays: Array.isArray(data?.plays) ? data.plays : [],
                plexStatus: data?.plexStatus ?? { enabled: false, connected: false },
                sampledSessions: data?.sampledSessions ?? 0,
                truncated: data?.truncated ?? false,
                limit: data?.limit ?? DEEP_PLAYBACK_HISTORY_LIMIT,
            });
            setRetainedPlaybackError(null);
        } catch (e: any) {
            setRetainedPlaybackError(e?.message ?? String(e));
        } finally {
            setRetainedPlaybackRefreshing(false);
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
            setRetainedPlayback(null);
            setRetainedPlaybackError(null);
            setError(null);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            setClearing(false);
        }
    }, []);

    useEffect(() => {
        if (filter !== "playback") return;
        void refreshRetainedPlayback();
    }, [filter, refreshRetainedPlayback]);

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
    const refreshVisible = useCallback(() => {
        void refresh();
        if (filter === "playback") void refreshRetainedPlayback();
    }, [filter, refresh, refreshRetainedPlayback]);
    const visibleError = filter === "playback" && retainedPlaybackError
        ? retainedPlaybackError
        : error;

    return (
        <div className={styles.page}>
            <ActivePlays reads={activeReads} />
            <PlaybackHistory
                plays={plays}
                filter={filter}
                retainedPlayback={retainedPlayback}
                retainedPlaybackLoading={
                    filter === "playback" && retainedPlaybackRefreshing
                }
                plexStatus={plexStatus}
                sampledSessions={sample.sampledSessions}
                truncated={sample.truncated}
                autoRefresh={autoRefresh}
                refreshing={
                    refreshing
                    || (filter === "playback" && retainedPlaybackRefreshing)
                }
                clearing={clearing}
                error={visibleError}
                onFilterChange={setFilter}
                onToggleAutoRefresh={() => setAutoRefresh(value => !value)}
                onRefresh={refreshVisible}
                onClear={() => { void clearAll(); }}
            />
        </div>
    );
}
