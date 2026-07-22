import assert from "node:assert/strict";
import test from "node:test";
import { presentQueueRecoveryNotice } from "./queue-recovery-notice";

test("presents restrained prep fallback states", () => {
    assert.equal(
        presentQueueRecoveryNotice({ phase: "prep", state: "searching", count: 0 }).text,
        "Prep fallback active",
    );
    assert.equal(
        presentQueueRecoveryNotice({ phase: "prep", state: "recovered", count: 3 }).text,
        "Prep fallback · 3 recovered",
    );
});

test("distinguishes unresolved, recovered, and globally missing health articles", () => {
    assert.equal(
        presentQueueRecoveryNotice({ phase: "health", state: "searching", count: 1 }).text,
        "Health fallback · 1 unresolved",
    );
    assert.equal(
        presentQueueRecoveryNotice({ phase: "health", state: "recovered", count: 1 }).text,
        "Health fallback · 1 recovered",
    );
    assert.equal(
        presentQueueRecoveryNotice({ phase: "health", state: "missing", count: 2 }).text,
        "Health · 2 missing everywhere",
    );
});
