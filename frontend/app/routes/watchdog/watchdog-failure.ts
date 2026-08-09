import type {
    WatchdogEntry,
    WatchdogPrepStats,
} from "~/clients/backend-client.server";

export function selectFailedDetailsAttempt(attempts: WatchdogEntry[]): WatchdogEntry | undefined {
    return attempts.reduce<WatchdogEntry | undefined>((best, attempt) => {
        if (!best) return attempt;
        const score = failureDetailScore(attempt);
        const bestScore = failureDetailScore(best);
        if (score !== bestScore) return score > bestScore ? attempt : best;
        return attempt.rankIndex >= best.rankIndex ? attempt : best;
    }, undefined);
}

function failureDetailScore(attempt: WatchdogEntry): number {
    return (attempt.outcome === "QueueFailed" ? 1_000 : 0)
        + (attempt.healthStats ? 500 : 0)
        + (attempt.prepStats ? 250 : 0)
        + (attempt.healthDurationMs != null ? 100 : 0)
        + (attempt.prepDurationMs != null ? 50 : 0);
}

export function formatPrepFailures(provider: WatchdogPrepStats["providers"][number]): string {
    const formatCount = (value: number) => Math.max(0, value).toLocaleString();
    const parts: string[] = [];
    if (provider.missing > 0) parts.push(`${formatCount(provider.missing)} missing`);
    if (provider.timeouts > 0) {
        const count = formatCount(provider.timeouts);
        parts.push(`${count} ${provider.timeouts === 1 ? "no response" : "no responses"}`);
    }
    if (provider.errors > 0) {
        const count = formatCount(provider.errors);
        parts.push(`${count} ${provider.errors === 1 ? "error" : "errors"}`);
    }
    return parts.length > 0 ? parts.join(" · ") : "—";
}

export function deriveFailurePhase(prepStats?: WatchdogPrepStats | null): string | null {
    switch (prepStats?.lastStage) {
        case "probing": return "Provider probing";
        case "first-segments": return "Prep · first segments";
        case "par2": return "Prep · PAR2 metadata";
        case "rar": return "Prep · archive metadata";
        case "processors": return "Prep · file processing";
        case "health": return "Health check";
        case "import": return "Import";
        default: return null;
    }
}
