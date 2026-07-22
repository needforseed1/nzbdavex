export type HealthSummaryTiming = {
    label: "Health",
    durationMs: number,
};

export function selectHealthSummaryTiming(
    healthDurationMs?: number | null,
    healthWaitDurationMs?: number | null,
): HealthSummaryTiming | null {
    if (healthWaitDurationMs != null) {
        return { label: "Health", durationMs: healthWaitDurationMs };
    }
    if (healthDurationMs != null) {
        return { label: "Health", durationMs: healthDurationMs };
    }
    return null;
}
