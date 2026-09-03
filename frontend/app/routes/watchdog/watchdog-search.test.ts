import test from "node:test";
import assert from "node:assert/strict";
import { matchesWatchdogSearch } from "./watchdog-search";

const attempts = [{
    requestedTitle: "Requested Movie",
    candidateTitle: "Candidate.Release.2160p",
    indexerName: "Local Indexer",
    failReason: "Missing articles",
}];

test("searches requested title, candidate title, indexer, and failure reason", () => {
    for (const query of ["requested", "2160P", "local index", "missing articles"])
        assert.equal(matchesWatchdogSearch(attempts, query), true, query);
    assert.equal(matchesWatchdogSearch(attempts, "unrelated"), false);
});

test("empty search composes with any active status filter", () => {
    assert.equal(matchesWatchdogSearch(attempts, "   "), true);
});
