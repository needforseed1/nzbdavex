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

test("adds prep and post-prep health wait without double-counting overlap", () => {
    assert.equal(selectTotalSummaryTiming(2300, 3884, 2365), 4665);
});

test("falls back to full health duration for legacy totals", () => {
    assert.equal(selectTotalSummaryTiming(2300, 3884, null), 6184);
});

test("omits total when either component is unavailable", () => {
    assert.equal(selectTotalSummaryTiming(null, 3884, 2365), null);
    assert.equal(selectTotalSummaryTiming(2300, null, null), null);
});
