import assert from "node:assert/strict";
import test from "node:test";
import { parseQueueStatusMessage } from "./queue-status-message";

test("parses a terminal queue failure and preserves separators in its reason", () => {
    assert.deepEqual(
        parseQueueStatusMessage("item-1|Failed|Missing articles | provider confirmed"),
        {
            nzo_id: "item-1",
            status: "Failed",
            error: "Missing articles | provider confirmed",
        },
    );
});

test("parses retry states without a failure reason", () => {
    assert.deepEqual(
        parseQueueStatusMessage("item-1|Queued"),
        { nzo_id: "item-1", status: "Queued" },
    );
});

test("rejects malformed status messages", () => {
    assert.equal(parseQueueStatusMessage("Queued"), null);
    assert.equal(parseQueueStatusMessage("item-1|"), null);
});
