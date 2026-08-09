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
    attemptDurationMs?: number | null,
    prepDurationMs?: number | null,
    healthDurationMs?: number | null,
    healthWaitDurationMs?: number | null,
): number | null {
    // The attempt clock covers qualification probing, queue orchestration and
    // failure paths that are intentionally absent from the narrower phase
    // clocks. Prefer it for the user-facing end-to-end total.
    if (attemptDurationMs != null) return attemptDurationMs;

    // Compatibility fallback for data sources that predate attempt duration.
    const healthAfterPrepMs = healthWaitDurationMs ?? healthDurationMs;
    if (prepDurationMs == null || healthAfterPrepMs == null) return null;
    return prepDurationMs + healthAfterPrepMs;
}
