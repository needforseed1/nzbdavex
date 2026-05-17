import { useCallback, useEffect, useState } from "react";
import styles from "./watchdog-activity.module.css";
import type { PlaybackAttempt, PlaybackAttemptOutcome } from "~/clients/backend-client.server";

const POLL_INTERVAL_MS = 3000;
const RECENT_WINDOW_SEC = 10 * 60;
const MAX_CLICKS = 6;

export function WatchdogActivity() {
    const [attempts, setAttempts] = useState<PlaybackAttempt[]>([]);

    const fetchAttempts = useCallback(async () => {
        try {
            const r = await fetch("/settings/watchdog-attempts?limit=200");
            if (!r.ok) return;
            const data = await r.json();
            setAttempts(data.attempts ?? []);
        } catch { /* ignore network errors */ }
    }, []);

    useEffect(() => {
        let cancelled = false;
        let timer: ReturnType<typeof setTimeout> | null = null;

        const loop = async () => {
            if (cancelled) return;
            await fetchAttempts();
            if (cancelled) return;
            timer = setTimeout(loop, POLL_INTERVAL_MS);
        };
        loop();

        return () => {
            cancelled = true;
            if (timer) clearTimeout(timer);
        };
    }, [fetchAttempts]);

    const recentGroups = groupByClick(attempts, RECENT_WINDOW_SEC).slice(0, MAX_CLICKS);
    if (recentGroups.length === 0) return null;

    return (
        <div className={styles.panel}>
            <div className={styles.panelHeader}>
                <div className={styles.dot} />
                <div className={styles.title}>Watchdog activity</div>
                <div className={styles.count}>{recentGroups.length} recent click{recentGroups.length === 1 ? "" : "s"}</div>
            </div>
            {recentGroups.map(g => (
                <div key={g.clickId} className={styles.clickRow}>
                    <div className={styles.clickHeader}>
                        <span className={styles.clickTitle} title={g.requestedTitle}>{g.requestedTitle}</span>
                        <span className={styles.clickMeta}>{g.contentType}</span>
                        <span className={styles.clickMeta}>{formatAge(g.lastAt)}</span>
                        {g.hasWinner && <span className={styles.winnerLabel}>✓ playing</span>}
                    </div>
                    <div className={styles.attemptList}>
                        {g.attempts.map((a, i) => (
                            <span key={i} className={`${styles.chip} ${chipClass(a.outcome, a.isWinner)}`}
                                  title={a.failReason ?? undefined}>
                                <span className={styles.chipRank}>#{a.rankIndex + 1}</span>
                                <span className={styles.chipName}>{a.indexerName || "?"}</span>
                                <span className={styles.outcome}>{shortOutcome(a.outcome, a.isWinner)}</span>
                            </span>
                        ))}
                    </div>
                </div>
            ))}
        </div>
    );
}

function chipClass(o: PlaybackAttemptOutcome, winner: boolean): string {
    if (winner) return styles.chipOk;
    switch (o) {
        case "QueueCompleted":
        case "PreVerifyAvailable":
            return styles.chipOk;
        case "BudgetTimeout":
        case "Cancelled":
            return styles.chipWarn;
        case "PreVerifyDead":
        case "QueueFailed":
        case "EnqueueFailed":
        case "PreVerifyTimeout":
            return styles.chipBad;
        default:
            return styles.chipPending;
    }
}

function shortOutcome(o: PlaybackAttemptOutcome, winner: boolean): string {
    if (winner) return "winner";
    switch (o) {
        case "QueueCompleted":      return "done";
        case "QueueFailed":         return "queue fail";
        case "EnqueueFailed":       return "enqueue fail";
        case "PreVerifyDead":       return "dead";
        case "PreVerifyTimeout":    return "verify timeout";
        case "PreVerifyAvailable":  return "verified";
        case "BudgetTimeout":       return "timeout";
        case "Cancelled":           return "cancelled";
        default:                    return String(o);
    }
}

type ClickGroup = {
    clickId: string,
    lastAt: number,
    requestedTitle: string,
    contentType: string,
    hasWinner: boolean,
    attempts: PlaybackAttempt[],
};

function groupByClick(list: PlaybackAttempt[], recentWindowSec: number): ClickGroup[] {
    const now = Math.floor(Date.now() / 1000);
    const cutoff = now - recentWindowSec;
    const map = new Map<string, ClickGroup>();
    for (const a of list) {
        if (a.attemptedAtUnix < cutoff) continue;
        const g = map.get(a.clickId);
        if (g) {
            g.attempts.push(a);
            if (a.attemptedAtUnix > g.lastAt) g.lastAt = a.attemptedAtUnix;
            if (a.isWinner) g.hasWinner = true;
        } else {
            map.set(a.clickId, {
                clickId: a.clickId,
                lastAt: a.attemptedAtUnix,
                requestedTitle: a.requestedTitle,
                contentType: a.contentType,
                hasWinner: a.isWinner,
                attempts: [a],
            });
        }
    }
    const arr = Array.from(map.values());
    arr.sort((x, y) => y.lastAt - x.lastAt);
    for (const g of arr) g.attempts.sort((x, y) => x.rankIndex - y.rankIndex);
    return arr;
}

function formatAge(unixSeconds: number): string {
    const age = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (age < 5) return "just now";
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    return `${Math.floor(age / 3600)}h ago`;
}
