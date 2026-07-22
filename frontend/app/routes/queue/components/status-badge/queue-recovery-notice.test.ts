import assert from "node:assert/strict";
import test from "node:test";
import { presentQueueRecoveryNotice } from "./queue-recovery-notice";

test("presents live prep fallback state", () => {
    assert.equal(
        presentQueueRecoveryNotice({ phase: "prep", count: 0 }).text,
        "Prep fallback active",
    );
});

test("presents unresolved health fallback state", () => {
    assert.equal(
        presentQueueRecoveryNotice({ phase: "health", count: 1 }).text,
        "Health fallback · 1 unresolved",
    );
});
