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

export function summarizeFailure(reason?: string | null): string {
    const value = reason?.trim() ?? "";
    const lower = value.toLowerCase();
    if (lower.includes("article with message-id") || lower.includes("missing article") ||
        lower.includes("reported article missing")) return "Missing articles";
    if (lower.includes("timed out") || lower.includes("timeout")) return "Provider or operation timeout";
    if (lower.includes("no viable") || lower.includes("no provider") || lower.includes("connection issue"))
        return "Provider unavailable";
    if (lower.includes("duplicate nzb")) return "Duplicate NZB";
    if (lower.includes("nzb file could not be found")) return "NZB file missing";
    return value || "Run failed";
}

export function deriveFailurePhase(prepStats?: WatchdogPrepStats | null): string | null {
    switch (prepStats?.lastStage) {
        case "first-segments": return "Prep · first segments";
        case "par2": return "Prep · PAR2 metadata";
        case "rar": return "Prep · archive metadata";
        case "processors": return "Prep · file processing";
        case "health": return "Health check";
        case "import": return "Import";
        default: return null;
    }
}
