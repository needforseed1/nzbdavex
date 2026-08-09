import assert from "node:assert/strict";
import test from "node:test";
import { selectHealthSummaryTiming, selectTotalSummaryTiming } from "./watchdog-timing";

test("uses full health duration for new watchdog entries", () => {
    assert.deepEqual(selectHealthSummaryTiming(3884, 2365), {
        label: "Health",
        durationMs: 3884,
    });
});

test("falls back to the blocking health wait when full duration is unavailable", () => {
    assert.deepEqual(selectHealthSummaryTiming(null, 2365), {
        label: "Health",
        durationMs: 2365,
    });
});

test("omits health timing when health did not run", () => {
    assert.equal(selectHealthSummaryTiming(null, null), null);
});

test("uses end-to-end attempt duration so provider probing is included", () => {
    assert.equal(selectTotalSummaryTiming(7421, 2300, 3884, 2365), 7421);
});

test("falls back to prep plus post-prep health wait for legacy totals", () => {
    assert.equal(selectTotalSummaryTiming(null, 2300, 3884, 2365), 4665);
});

test("falls back to full health duration when legacy wait timing is unavailable", () => {
    assert.equal(selectTotalSummaryTiming(null, 2300, 3884, null), 6184);
});

test("omits a legacy total when either phase component is unavailable", () => {
    assert.equal(selectTotalSummaryTiming(null, null, 3884, 2365), null);
    assert.equal(selectTotalSummaryTiming(null, 2300, null, null), null);
});
