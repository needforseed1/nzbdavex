import type { Route } from "./+types/route";
import {
    type ChangeEvent,
    type KeyboardEvent,
    useCallback,
    useEffect,
    useMemo,
    useRef,
    useState,
} from "react";
import styles from "./route.module.css";
import { backendClient, type LogEntry, type LogLevel } from "~/clients/backend-client.server";
import { useLogsWebsocket, type ConnectionStatus } from "./controllers/websocket-controller";

const ALL_LEVELS: LogLevel[] = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
const DEFAULT_LEVELS: LogLevel[] = ["Information", "Warning", "Error", "Fatal"];
const INITIAL_LIMIT = 1000;
const CLIENT_MAX_ENTRIES = 5000;

export async function loader() {
    const data = await backendClient.getLogs({ limit: INITIAL_LIMIT });
    return data;
}

// Opt out of react-router revalidation. The page already fetches its own
// updates via WebSocket + on-filter-change; loader revalidation would just
// double-fetch and fight with the live stream.
export function shouldRevalidate() {
    return false;
}

export default function Logs({ loaderData }: Route.ComponentProps) {
    const initialQuery = typeof window !== "undefined"
        ? new URLSearchParams(window.location.search)
        : new URLSearchParams();

    const [entries, setEntries] = useState<LogEntry[]>(loaderData.entries);
    const [counts, setCounts] = useState<Record<string, number>>(loaderData.countsByLevel);
    const [capacity] = useState<number>(loaderData.capacity);
    const [enabledLevels, setEnabledLevels] = useState<Set<LogLevel>>(
        () => new Set(parseLevels(initialQuery.get("levels"))),
    );
    const [searchInput, setSearchInput] = useState<string>(initialQuery.get("q") ?? "");
    const [sourceInput, setSourceInput] = useState<string>(initialQuery.get("src") ?? "");
    const [search, setSearch] = useState<string>(initialQuery.get("q") ?? "");
    const [source, setSource] = useState<string>(initialQuery.get("src") ?? "");
    const [paused, setPaused] = useState<boolean>(false);
    const [pendingCount, setPendingCount] = useState<number>(0);
    const [followTail, setFollowTail] = useState<boolean>(true);
    const [expanded, setExpanded] = useState<Set<number>>(new Set());
    const [connection, setConnection] = useState<ConnectionStatus>("connecting");
    const [errorText, setErrorText] = useState<string | null>(null);

    const listRef = useRef<HTMLDivElement | null>(null);
    const searchRef = useRef<HTMLInputElement | null>(null);
    const pausedQueueRef = useRef<LogEntry[]>([]);
    const pausedRef = useRef<boolean>(paused);
    pausedRef.current = paused;
    const followTailRef = useRef<boolean>(followTail);
    followTailRef.current = followTail;
    const enabledLevelsRef = useRef<Set<LogLevel>>(enabledLevels);
    enabledLevelsRef.current = enabledLevels;
    const searchRefValue = useRef<string>(search);
    searchRefValue.current = search;
    const sourceRefValue = useRef<string>(source);
    sourceRefValue.current = source;

    // debounce search & source inputs
    useEffect(() => {
        const t = setTimeout(() => setSearch(searchInput.trim()), 220);
        return () => clearTimeout(t);
    }, [searchInput]);
    useEffect(() => {
        const t = setTimeout(() => setSource(sourceInput.trim()), 220);
        return () => clearTimeout(t);
    }, [sourceInput]);

    // sync URL state — using history.replaceState directly so we don't trigger
    // react-router navigation/revalidation (which was causing render loops).
    const didMountRef = useRef(false);
    useEffect(() => {
        if (typeof window === "undefined") return;
        const next = new URLSearchParams();
        if (enabledLevels.size > 0 && !sameLevels(enabledLevels, DEFAULT_LEVELS)) {
            next.set("levels", [...enabledLevels].join(","));
        }
        if (search) next.set("q", search);
        if (source) next.set("src", source);
        const qs = next.toString();
        const target = `${window.location.pathname}${qs ? `?${qs}` : ""}`;
        if (target !== `${window.location.pathname}${window.location.search}`) {
            window.history.replaceState(null, "", target);
        }
    }, [enabledLevels, search, source]);

    // refetch when filters change — but skip the very first render, since the
    // loader already provided the initial unfiltered set.
    useEffect(() => {
        if (!didMountRef.current) {
            didMountRef.current = true;
            return;
        }
        let cancelled = false;
        const params = new URLSearchParams();
        params.set("limit", String(INITIAL_LIMIT));
        if (enabledLevels.size > 0 && enabledLevels.size < ALL_LEVELS.length) {
            params.set("levels", [...enabledLevels].join(","));
        }
        if (search) params.set("search", search);
        if (source) params.set("source", source);
        fetch(`/api/get-logs?${params.toString()}`)
            .then(async r => {
                if (!r.ok) throw new Error(`HTTP ${r.status}`);
                return r.json();
            })
            .then(data => {
                if (cancelled) return;
                setEntries(data.entries ?? []);
                setCounts(data.countsByLevel ?? {});
                setErrorText(null);
                if (followTailRef.current) requestAnimationFrame(scrollToBottom);
            })
            .catch(e => {
                if (cancelled) return;
                setErrorText(String(e?.message ?? e));
            });
        return () => {
            cancelled = true;
        };
    }, [enabledLevels, search, source]);

    // WebSocket: live append (or queue while paused)
    const onBatch = useCallback((batch: LogEntry[]) => {
        if (pausedRef.current) {
            pausedQueueRef.current.push(...batch);
            setPendingCount(c => c + batch.length);
            return;
        }
        applyBatch(batch);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    function applyBatch(batch: LogEntry[]) {
        if (batch.length === 0) return;
        const matches = batch.filter(matchesCurrentFilters);
        setCounts(prev => {
            const next = { ...prev };
            for (const e of batch) next[e.level] = (next[e.level] ?? 0) + 1;
            return next;
        });
        if (matches.length > 0) {
            setEntries(prev => mergeAndCap(prev, matches));
            if (followTailRef.current) requestAnimationFrame(scrollToBottom);
        }
    }

    function matchesCurrentFilters(e: LogEntry): boolean {
        const levels = enabledLevelsRef.current;
        if (levels.size > 0 && !levels.has(e.level)) return false;
        const src = sourceRefValue.current;
        if (src && !(e.source ?? "").toLowerCase().includes(src.toLowerCase())) return false;
        const q = searchRefValue.current.toLowerCase();
        if (q) {
            if (!e.msg.toLowerCase().includes(q)
                && !(e.source ?? "").toLowerCase().includes(q)
                && !(e.exception ?? "").toLowerCase().includes(q)) {
                return false;
            }
        }
        return true;
    }

    useLogsWebsocket(onBatch, setConnection);

    // smart auto-scroll: detect when the user scrolls up to disengage follow.
    // Programmatic scrolls (scrollToBottom) set a suppression flag so the
    // resulting scroll event doesn't bounce followTail.
    const suppressScrollRef = useRef(false);
    const handleScroll = useCallback(() => {
        if (suppressScrollRef.current) {
            suppressScrollRef.current = false;
            return;
        }
        const el = listRef.current;
        if (!el) return;
        const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
        const near = distanceFromBottom < 48;
        setFollowTail(prev => (prev !== near ? near : prev));
    }, []);

    function scrollToBottom() {
        const el = listRef.current;
        if (!el) return;
        suppressScrollRef.current = true;
        el.scrollTop = el.scrollHeight;
    }

    // when the entries list mounts/first-loads, scroll to bottom
    useEffect(() => {
        requestAnimationFrame(scrollToBottom);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    // keyboard shortcuts
    useEffect(() => {
        const onKey = (ev: globalThis.KeyboardEvent) => {
            const target = ev.target as HTMLElement | null;
            const inField = target?.tagName === "INPUT" || target?.tagName === "TEXTAREA";
            if (ev.key === "/" && !inField) {
                ev.preventDefault();
                searchRef.current?.focus();
                searchRef.current?.select();
                return;
            }
            if (ev.key === "Escape" && inField && target === searchRef.current) {
                setSearchInput("");
                searchRef.current?.blur();
                return;
            }
            if ((ev.key === "f" || ev.key === "F") && !inField && !ev.metaKey && !ev.ctrlKey) {
                ev.preventDefault();
                setFollowTail(v => {
                    if (!v) requestAnimationFrame(scrollToBottom);
                    return !v;
                });
            }
        };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, []);

    const toggleLevel = useCallback((level: LogLevel) => {
        setEnabledLevels(prev => {
            const next = new Set(prev);
            if (next.has(level)) next.delete(level);
            else next.add(level);
            return next;
        });
    }, []);

    const toggleExpanded = useCallback((seq: number) => {
        setExpanded(prev => {
            const next = new Set(prev);
            if (next.has(seq)) next.delete(seq);
            else next.add(seq);
            return next;
        });
    }, []);

    const togglePause = useCallback(() => {
        if (pausedRef.current) {
            const queued = pausedQueueRef.current;
            pausedQueueRef.current = [];
            setPendingCount(0);
            applyBatch(queued);
            setPaused(false);
        } else {
            setPaused(true);
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const clearView = useCallback(() => {
        setEntries([]);
        setExpanded(new Set());
    }, []);

    const downloadHref = useMemo(() => {
        const params = new URLSearchParams();
        if (enabledLevels.size > 0 && enabledLevels.size < ALL_LEVELS.length) {
            params.set("levels", [...enabledLevels].join(","));
        }
        if (search) params.set("search", search);
        if (source) params.set("source", source);
        return `/api/download-logs${params.toString() ? `?${params.toString()}` : ""}`;
    }, [enabledLevels, search, source]);

    const totalInBuffer = useMemo(
        () => Object.values(counts).reduce((a, b) => a + b, 0),
        [counts],
    );

    return (
        <div className={styles.page}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>
                        <div className={styles.titleRow}>
                            <span
                                className={`${styles.connectionDot} ${connectionDotClass(connection)}`}
                                title={`WebSocket ${connection}`}
                            />
                            <h2 className={styles.title}>Logs</h2>
                        </div>
                        <div className={styles.subtitle}>
                            Live application logs from the in-memory ring buffer.
                            Last {capacity.toLocaleString()} entries are kept in RAM only, not persisted across restarts.
                        </div>
                    </div>
                    <div className={styles.controls}>
                        <button
                            type="button"
                            className={`${styles.toolbarBtn} ${followTail ? styles.toolbarBtnOn : ""}`}
                            onClick={() => {
                                setFollowTail(v => {
                                    if (!v) requestAnimationFrame(scrollToBottom);
                                    return !v;
                                });
                            }}
                            title="Auto-follow the latest entry. Shortcut: f">
                            {followTail ? "Following" : "Follow tail"}
                        </button>
                        <button
                            type="button"
                            className={`${styles.toolbarBtn} ${paused ? styles.toolbarBtnOn : ""}`}
                            onClick={togglePause}
                            title={paused ? "Stream paused. Click to resume." : "Pause live stream."}>
                            {paused ? `Paused (${pendingCount})` : "Pause"}
                        </button>
                        <button
                            type="button"
                            className={styles.toolbarBtn}
                            onClick={clearView}
                            disabled={entries.length === 0}
                            title="Clear the on-screen view. Server buffer is untouched.">
                            Clear view
                        </button>
                        <a
                            href={downloadHref}
                            className={styles.toolbarBtn}
                            title="Download current view as a .log file."
                            download>
                            Download
                        </a>
                    </div>
                </div>

                <div className={styles.levelBar}>
                    {ALL_LEVELS.map(level => (
                        <LevelChip
                            key={level}
                            level={level}
                            active={enabledLevels.has(level)}
                            count={counts[level] ?? 0}
                            onClick={() => toggleLevel(level)}
                        />
                    ))}
                </div>

                <div className={styles.searchRow}>
                    <div className={styles.searchInputWrap}>
                        <svg className={styles.searchIcon} viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
                            <path d="M11.742 10.344a6.5 6.5 0 1 0-1.397 1.398h-.001c.03.04.062.078.098.115l3.85 3.85a1 1 0 0 0 1.415-1.414l-3.85-3.85a1 1 0 0 0-.115-.099zM12 6.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0" />
                        </svg>
                        <input
                            ref={searchRef}
                            className={styles.searchInput}
                            type="search"
                            value={searchInput}
                            onChange={(e: ChangeEvent<HTMLInputElement>) => setSearchInput(e.target.value)}
                            onKeyDown={(e: KeyboardEvent<HTMLInputElement>) => {
                                if (e.key === "Escape") { setSearchInput(""); e.currentTarget.blur(); }
                            }}
                            placeholder="Search messages, sources, stack traces…  ( / to focus )"
                            spellCheck={false}
                            autoComplete="off"
                        />
                    </div>
                    <input
                        className={styles.sourceInput}
                        type="text"
                        value={sourceInput}
                        onChange={(e: ChangeEvent<HTMLInputElement>) => setSourceInput(e.target.value)}
                        placeholder="Source filter (e.g. NzbWebDAV.Queue)"
                        spellCheck={false}
                        autoComplete="off"
                    />
                    <span className={styles.bufferMeter}>
                        {entries.length.toLocaleString()} shown · {totalInBuffer.toLocaleString()}/{capacity.toLocaleString()} in buffer
                    </span>
                </div>

                {errorText && <div className={styles.errorBox}>Couldn't load logs: {errorText}</div>}
            </div>

            <div className={styles.listWrap}>
                {entries.length === 0 ? (
                    <div className={styles.emptyState}>
                        <div className={styles.emptyHeadline}>No log entries to show.</div>
                        <div>
                            {totalInBuffer === 0
                                ? "Nothing has been logged yet."
                                : "Try widening your filters."}
                        </div>
                    </div>
                ) : (
                    <div ref={listRef} className={styles.list} onScroll={handleScroll}>
                        {entries.map(entry => (
                            <LogRow
                                key={entry.seq}
                                entry={entry}
                                expanded={expanded.has(entry.seq)}
                                onToggle={() => toggleExpanded(entry.seq)}
                            />
                        ))}
                    </div>
                )}
                {!followTail && entries.length > 0 && (
                    <button
                        type="button"
                        className={styles.jumpBtn}
                        onClick={() => { setFollowTail(true); requestAnimationFrame(scrollToBottom); }}>
                        <svg className={styles.jumpBtnArrow} viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
                            <path d="M8 12.5a.5.5 0 0 1-.354-.146l-5-5a.5.5 0 1 1 .708-.708L8 11.293l4.646-4.647a.5.5 0 0 1 .708.708l-5 5A.5.5 0 0 1 8 12.5z" />
                        </svg>
                        Jump to live
                    </button>
                )}
            </div>
        </div>
    );
}

function LogRow({ entry, expanded, onToggle }: {
    entry: LogEntry,
    expanded: boolean,
    onToggle: () => void,
}) {
    const rowClass = `${styles.row} ${levelRowClass(entry.level)}`;
    return (
        <div className={rowClass} onClick={onToggle} title={entry.exception ? "Click to toggle stack trace" : undefined}>
            <span className={styles.rowTime}>{formatTime(entry.ts)}</span>
            <span className={styles.rowLevel}>{shortLevel(entry.level)}</span>
            <span className={styles.rowBody}>
                <span className={styles.rowMessage}>{entry.msg}</span>
                {entry.source && <span className={styles.rowSource}>{entry.source}</span>}
                {entry.exception && !expanded && (
                    <span className={styles.rowExceptionHint}>▸ click to view stack trace</span>
                )}
                {entry.exception && expanded && (
                    <pre className={styles.rowException}>{entry.exception}</pre>
                )}
            </span>
        </div>
    );
}

function LevelChip({ level, active, count, onClick }: {
    level: LogLevel,
    active: boolean,
    count: number,
    onClick: () => void,
}) {
    const activeClass = !active ? "" :
        level === "Information" ? styles.levelChipActiveInfo :
        level === "Warning" ? styles.levelChipActiveWarning :
        level === "Error" ? styles.levelChipActiveError :
        level === "Fatal" ? styles.levelChipActiveFatal :
        level === "Debug" ? styles.levelChipActiveDebug :
        styles.levelChipActiveVerbose;
    return (
        <button
            type="button"
            className={`${styles.levelChip} ${active ? styles.levelChipActive : ""} ${activeClass}`}
            onClick={onClick}>
            <span>{shortLevel(level)}</span>
            <span className={styles.levelChipCount}>{count}</span>
        </button>
    );
}

function levelRowClass(level: LogLevel): string {
    switch (level) {
        case "Verbose": return styles.rowVerbose;
        case "Debug": return styles.rowDebug;
        case "Information": return styles.rowInformation;
        case "Warning": return styles.rowWarning;
        case "Error": return styles.rowError;
        case "Fatal": return styles.rowFatal;
    }
}

function shortLevel(level: LogLevel): string {
    switch (level) {
        case "Verbose": return "trace";
        case "Debug": return "debug";
        case "Information": return "info";
        case "Warning": return "warn";
        case "Error": return "error";
        case "Fatal": return "fatal";
    }
}

function connectionDotClass(s: ConnectionStatus): string {
    switch (s) {
        case "live": return styles.connLive;
        case "reconnecting":
        case "connecting": return styles.connReconnecting;
        case "disconnected": return styles.connDisconnected;
    }
}

function formatTime(unixMs: number): string {
    const d = new Date(unixMs);
    const hh = String(d.getHours()).padStart(2, "0");
    const mm = String(d.getMinutes()).padStart(2, "0");
    const ss = String(d.getSeconds()).padStart(2, "0");
    const ms = String(d.getMilliseconds()).padStart(3, "0");
    return `${hh}:${mm}:${ss}.${ms}`;
}

function parseLevels(raw: string | null): LogLevel[] {
    if (!raw) return [...DEFAULT_LEVELS];
    const parts = raw.split(",").map(s => s.trim()).filter(Boolean);
    const known = parts.filter((s): s is LogLevel => ALL_LEVELS.includes(s as LogLevel));
    return known.length > 0 ? known : [...DEFAULT_LEVELS];
}

function sameLevels(set: Set<LogLevel>, list: LogLevel[]): boolean {
    if (set.size !== list.length) return false;
    for (const l of list) if (!set.has(l)) return false;
    return true;
}

function mergeAndCap(prev: LogEntry[], incoming: LogEntry[]): LogEntry[] {
    if (incoming.length === 0) return prev;
    // Newest live entries always have higher sequence numbers, so append + dedupe.
    const lastSeq = prev.length > 0 ? prev[prev.length - 1].seq : 0;
    const fresh = incoming.filter(e => e.seq > lastSeq);
    if (fresh.length === 0) return prev;
    const next = prev.concat(fresh);
    if (next.length <= CLIENT_MAX_ENTRIES) return next;
    return next.slice(next.length - CLIENT_MAX_ENTRIES);
}
