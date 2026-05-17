import { redirect } from "react-router";
import type { Route } from "./+types/route";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Button, Form } from "react-bootstrap";
import styles from "./route.module.css";
import { backendClient, type PlaybackAttempt, type PlaybackAttemptOutcome } from "~/clients/backend-client.server";

const POLL_INTERVAL_MS = 3000;

export async function loader() {
    const [config, attempts] = await Promise.all([
        backendClient.getConfig(["play.watchdog-enabled"]),
        backendClient.getPlaybackAttempts(200),
    ]);
    const enabledRaw = config.find(x => x.configName === "play.watchdog-enabled")?.configValue ?? "true";
    const isEnabled = enabledRaw.toLowerCase() === "true";
    if (!isEnabled) {
        return redirect("/queue");
    }
    return { attempts };
}

type FilterKey = "all" | "live" | "resolved" | "failed";

export default function Watchdog({ loaderData }: Route.ComponentProps) {
    const [attempts, setAttempts] = useState<PlaybackAttempt[]>(loaderData.attempts);
    const [autoRefresh, setAutoRefresh] = useState(true);
    const [filter, setFilter] = useState<FilterKey>("all");
    const [hiddenBefore, setHiddenBefore] = useState<number>(0);
    const [refreshing, setRefreshing] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const lastRefreshRef = useRef<number>(Date.now());

    const refresh = useCallback(async () => {
        setRefreshing(true);
        try {
            const r = await fetch("/settings/watchdog-attempts?limit=200");
            if (!r.ok) throw new Error(`HTTP ${r.status}`);
            const data = await r.json();
            setAttempts(data.attempts ?? []);
            setError(null);
            lastRefreshRef.current = Date.now();
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            setRefreshing(false);
        }
    }, []);

    useEffect(() => {
        if (!autoRefresh) return;
        let cancelled = false;
        let timer: ReturnType<typeof setTimeout> | null = null;
        const loop = async () => {
            if (cancelled) return;
            await refresh();
            if (cancelled) return;
            timer = setTimeout(loop, POLL_INTERVAL_MS);
        };
        timer = setTimeout(loop, POLL_INTERVAL_MS);
        return () => {
            cancelled = true;
            if (timer) clearTimeout(timer);
        };
    }, [autoRefresh, refresh]);

    const groups = useMemo(() => groupByClick(attempts, hiddenBefore), [attempts, hiddenBefore]);
    const filteredGroups = useMemo(() => groups.filter(g => matchesFilter(g, filter)), [groups, filter]);

    const stats = useMemo(() => computeStats(groups), [groups]);

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div className={styles.titleRow}>
                    <div className={styles.titleBlock}>
                        <h1 className={styles.title}>
                            <span className={`${styles.dot} ${autoRefresh ? styles.dotLive : ""}`} />
                            Watchdog
                        </h1>
                        <div className={styles.subtitle}>
                            Live view of every release the playback watchdog tried. Held in memory; cleared on app restart.
                        </div>
                    </div>
                    <div className={styles.controls}>
                        <Form.Check
                            type="switch"
                            id="watchdog-autorefresh"
                            label="Auto-refresh"
                            checked={autoRefresh}
                            onChange={e => setAutoRefresh(e.target.checked)} />
                        <Button variant="secondary" size="sm" onClick={refresh} disabled={refreshing}>
                            {refreshing ? "Refreshing…" : "Refresh"}
                        </Button>
                        <Button
                            variant="outline-secondary"
                            size="sm"
                            onClick={() => setHiddenBefore(Math.floor(Date.now() / 1000))}
                            disabled={groups.length === 0}
                            title="Hide everything currently visible. New attempts still appear.">
                            Clear view
                        </Button>
                    </div>
                </div>

                <div className={styles.statsRow}>
                    <Stat label="Clicks" value={stats.total} />
                    <Stat label="Resolved" value={stats.resolved} tone="ok" />
                    <Stat label="Failed" value={stats.failed} tone="bad" />
                    <Stat label="In flight" value={stats.inFlight} tone="warn" />
                </div>

                <div className={styles.filterRow}>
                    <FilterChip active={filter === "all"} onClick={() => setFilter("all")}>All ({groups.length})</FilterChip>
                    <FilterChip active={filter === "live"} onClick={() => setFilter("live")}>Live ({stats.inFlight})</FilterChip>
                    <FilterChip active={filter === "resolved"} onClick={() => setFilter("resolved")}>Resolved ({stats.resolved})</FilterChip>
                    <FilterChip active={filter === "failed"} onClick={() => setFilter("failed")}>Failed ({stats.failed})</FilterChip>
                </div>

                {error && <div className={styles.errorBox}>Could not load: {error}</div>}
            </div>

            {filteredGroups.length === 0 ? (
                <div className={styles.emptyState}>
                    {groups.length === 0
                        ? "No playback attempts recorded yet. Click Play in your client to see live activity here."
                        : "No clicks match this filter."}
                </div>
            ) : (
                <div className={styles.clickList}>
                    {filteredGroups.map(g => <ClickCard key={g.clickId} group={g} />)}
                </div>
            )}
        </div>
    );
}

function ClickCard({ group }: { group: ClickGroup }) {
    const status = group.hasWinner ? "win" : group.allResolved ? "loss" : "inflight";
    const fastest = group.attempts.find(a => a.isWinner);

    return (
        <div className={`${styles.clickCard} ${styles[`clickCard_${status}`]}`}>
            <div className={styles.clickHeader}>
                <div className={styles.clickHeaderLeft}>
                    <span className={`${styles.statusPill} ${styles[`statusPill_${status}`]}`}>
                        {status === "win" ? "✓ Resolved" : status === "loss" ? "✗ Failed" : "● Live"}
                    </span>
                    <span className={styles.clickTitle} title={group.requestedTitle}>{group.requestedTitle}</span>
                </div>
                <div className={styles.clickHeaderRight}>
                    <span className={styles.metaPill}>{group.contentType}</span>
                    <span className={styles.metaPill}>{group.attempts.length} attempt{group.attempts.length === 1 ? "" : "s"}</span>
                    <span className={styles.timestamp} title={new Date(group.firstAt * 1000).toLocaleString()}>
                        {formatAge(group.firstAt)}
                    </span>
                </div>
            </div>

            {fastest && (
                <div className={styles.winnerSummary}>
                    Resolved by <strong>{fastest.indexerName}</strong> in <strong>{fastest.durationMs}ms</strong>
                    {fastest.size > 0 && <> · {formatBytes(fastest.size)}</>}
                </div>
            )}

            <div className={styles.attemptsWrap}>
                <table className={styles.attemptTable}>
                    <thead>
                        <tr>
                            <th className={styles.colRank}>#</th>
                            <th className={styles.colCandidate}>Candidate</th>
                            <th className={styles.colIndexer}>Indexer</th>
                            <th className={styles.colSize}>Size</th>
                            <th className={styles.colOutcome}>Outcome</th>
                            <th className={styles.colReason}>Reason</th>
                            <th className={styles.colDuration}>Took</th>
                        </tr>
                    </thead>
                    <tbody>
                        {group.attempts.map((a, i) => (
                            <tr key={i} className={a.isWinner ? styles.winnerRow : undefined}>
                                <td className={styles.colRank}>{a.rankIndex + 1}</td>
                                <td className={styles.colCandidate} title={a.candidateTitle}>{a.candidateTitle || "—"}</td>
                                <td className={styles.colIndexer}>{a.indexerName || "—"}</td>
                                <td className={styles.colSize}>{formatBytes(a.size)}</td>
                                <td className={styles.colOutcome}>
                                    <OutcomeBadge outcome={a.outcome} winner={a.isWinner} />
                                </td>
                                <td className={styles.colReason} title={a.failReason ?? undefined}>{a.failReason ?? "—"}</td>
                                <td className={styles.colDuration}>{a.durationMs}ms</td>
                            </tr>
                        ))}
                    </tbody>
                </table>

                <div className={styles.attemptCards}>
                    {group.attempts.map((a, i) => (
                        <div key={i} className={`${styles.attemptCard} ${a.isWinner ? styles.attemptCardWinner : ""}`}>
                            <div className={styles.attemptCardTop}>
                                <span className={styles.attemptRank}>#{a.rankIndex + 1}</span>
                                <span className={styles.attemptIndexer}>{a.indexerName || "—"}</span>
                                <OutcomeBadge outcome={a.outcome} winner={a.isWinner} />
                            </div>
                            <div className={styles.attemptCardTitle} title={a.candidateTitle}>{a.candidateTitle || "—"}</div>
                            <div className={styles.attemptCardMeta}>
                                <span>{formatBytes(a.size)}</span>
                                <span>·</span>
                                <span>{a.durationMs}ms</span>
                            </div>
                            {a.failReason && (
                                <div className={styles.attemptCardReason}>{a.failReason}</div>
                            )}
                        </div>
                    ))}
                </div>
            </div>
        </div>
    );
}

function Stat({ label, value, tone }: { label: string, value: number, tone?: "ok" | "bad" | "warn" }) {
    const toneClass = tone === "ok" ? styles.statOk : tone === "bad" ? styles.statBad : tone === "warn" ? styles.statWarn : "";
    return (
        <div className={styles.stat}>
            <div className={`${styles.statValue} ${toneClass}`}>{value}</div>
            <div className={styles.statLabel}>{label}</div>
        </div>
    );
}

function FilterChip({ active, onClick, children }: { active: boolean, onClick: () => void, children: React.ReactNode }) {
    return (
        <button
            type="button"
            className={`${styles.filterChip} ${active ? styles.filterChipActive : ""}`}
            onClick={onClick}>
            {children}
        </button>
    );
}

function OutcomeBadge({ outcome, winner }: { outcome: PlaybackAttemptOutcome, winner: boolean }) {
    if (winner) return <span className={`${styles.outcomeBadge} ${styles.outcomeWin}`}>winner</span>;
    const cls =
        outcome === "QueueCompleted" ? styles.outcomeOk
        : outcome === "PreVerifyAvailable" ? styles.outcomeOk
        : outcome === "BudgetTimeout" ? styles.outcomeWarn
        : outcome === "Cancelled" ? styles.outcomeWarn
        : styles.outcomeBad;
    return <span className={`${styles.outcomeBadge} ${cls}`}>{shortOutcome(outcome)}</span>;
}

function shortOutcome(o: PlaybackAttemptOutcome): string {
    switch (o) {
        case "QueueCompleted": return "completed";
        case "QueueFailed": return "queue failed";
        case "EnqueueFailed": return "enqueue failed";
        case "PreVerifyDead": return "verify: dead";
        case "PreVerifyTimeout": return "verify: timeout";
        case "PreVerifyAvailable": return "verify: ok";
        case "BudgetTimeout": return "budget timeout";
        case "Cancelled": return "cancelled";
        default: return o;
    }
}

type ClickGroup = {
    clickId: string,
    firstAt: number,
    requestedTitle: string,
    contentType: string,
    hasWinner: boolean,
    allResolved: boolean,
    attempts: PlaybackAttempt[],
};

function groupByClick(list: PlaybackAttempt[], hiddenBefore: number): ClickGroup[] {
    const map = new Map<string, ClickGroup>();
    for (const a of list) {
        if (a.attemptedAtUnix < hiddenBefore) continue;
        const g = map.get(a.clickId);
        if (g) {
            g.attempts.push(a);
            if (a.attemptedAtUnix > g.firstAt) g.firstAt = a.attemptedAtUnix;
            if (a.isWinner) g.hasWinner = true;
        } else {
            map.set(a.clickId, {
                clickId: a.clickId,
                firstAt: a.attemptedAtUnix,
                requestedTitle: a.requestedTitle,
                contentType: a.contentType,
                hasWinner: a.isWinner,
                allResolved: false,
                attempts: [a],
            });
        }
    }
    const arr = Array.from(map.values());
    for (const g of arr) {
        g.attempts.sort((x, y) => x.rankIndex - y.rankIndex);
        g.allResolved = g.attempts.every(isTerminal);
    }
    arr.sort((x, y) => y.firstAt - x.firstAt);
    return arr;
}

function isTerminal(a: PlaybackAttempt): boolean {
    switch (a.outcome) {
        case "QueueCompleted":
        case "QueueFailed":
        case "EnqueueFailed":
        case "PreVerifyDead":
        case "PreVerifyTimeout":
        case "Cancelled":
        case "BudgetTimeout":
            return true;
        case "PreVerifyAvailable":
            return false;
        default:
            return false;
    }
}

function matchesFilter(g: ClickGroup, f: FilterKey): boolean {
    switch (f) {
        case "all": return true;
        case "live": return !g.hasWinner && !g.allResolved;
        case "resolved": return g.hasWinner;
        case "failed": return !g.hasWinner && g.allResolved;
    }
}

function computeStats(groups: ClickGroup[]) {
    let resolved = 0, failed = 0, inFlight = 0;
    for (const g of groups) {
        if (g.hasWinner) resolved++;
        else if (g.allResolved) failed++;
        else inFlight++;
    }
    return { total: groups.length, resolved, failed, inFlight };
}

function formatBytes(bytes: number): string {
    if (bytes <= 0) return "—";
    const u = ["B", "KB", "MB", "GB", "TB"];
    let i = 0;
    let v = bytes;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2)} ${u[i]}`;
}

function formatAge(unixSeconds: number): string {
    const age = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (age < 5) return "just now";
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}
