import assert from "node:assert/strict";
import test from "node:test";
import { selectHealthSummaryTiming, selectTotalSummaryTiming } from "./watchdog-timing";

test("uses blocking health wait for new watchdog entries", () => {
    assert.deepEqual(selectHealthSummaryTiming(3884, 2365), {
        label: "Health",
        durationMs: 2365,
    });
});

test("falls back to the recorded full health duration for legacy entries", () => {
    assert.deepEqual(selectHealthSummaryTiming(3884, null), {
        label: "Health",
        durationMs: 3884,
    });
});

test("omits health timing when health did not run", () => {
    assert.equal(selectHealthSummaryTiming(null, null), null);
});

test("adds prep and visible health wait for the playback total", () => {
    const health = selectHealthSummaryTiming(3884, 2365);
    assert.equal(selectTotalSummaryTiming(2300, health), 4665);
});

test("omits total when either visible component is unavailable", () => {
    assert.equal(selectTotalSummaryTiming(null, { label: "Health", durationMs: 2365 }), null);
    assert.equal(selectTotalSummaryTiming(2300, null), null);
});
