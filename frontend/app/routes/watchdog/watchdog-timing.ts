export type HealthSummaryTiming = {
    label: "Health",
    durationMs: number,
};

export function selectHealthSummaryTiming(
    healthDurationMs?: number | null,
    healthWaitDurationMs?: number | null,
): HealthSummaryTiming | null {
    if (healthDurationMs != null) {
        return { label: "Health", durationMs: healthDurationMs };
    }
    if (healthWaitDurationMs != null) {
        return { label: "Health", durationMs: healthWaitDurationMs };
    }
    return null;
}

export function selectTotalSummaryTiming(
    prepDurationMs?: number | null,
    healthDurationMs?: number | null,
    healthWaitDurationMs?: number | null,
): number | null {
    const healthAfterPrepMs = healthWaitDurationMs ?? healthDurationMs;
    if (prepDurationMs == null || healthAfterPrepMs == null) return null;
    return prepDurationMs + healthAfterPrepMs;
}
