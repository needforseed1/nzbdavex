import assert from "node:assert/strict";
import test from "node:test";
import {
    historyCategoryOptions,
    historyCategorySearchParams,
    matchesHistoryCategory,
    normalizeHistoryCategory,
    STREAMING_HISTORY_CATEGORIES,
} from "./history-category-filter";

test("normalizes an empty history category to the unfiltered state", () => {
    assert.equal(normalizeHistoryCategory(null), null);
    assert.equal(normalizeHistoryCategory("   "), null);
    assert.equal(normalizeHistoryCategory(" movies "), "movies");
});

test("matches every history row when unfiltered and only exact categories when filtered", () => {
    assert.equal(matchesHistoryCategory("movies", null), true);
    assert.equal(matchesHistoryCategory("movies", "movies"), true);
    assert.equal(matchesHistoryCategory("Movies", "movies"), false);
});

test("updates the history category query and returns to the first history page", () => {
    const current = new URLSearchParams("qp=4&hp=3");
    assert.equal(historyCategorySearchParams(current, "movies").toString(), "qp=4&hp=1&hc=movies");
    assert.equal(
        historyCategorySearchParams(new URLSearchParams("hp=3&hc=movies"), null).toString(),
        "hp=1",
    );
});

test("deduplicates available categories and retains a selected legacy category", () => {
    assert.deepEqual(
        historyCategoryOptions([" movies ", "tv", "movies", ""], null),
        ["movies", "tv"],
    );
    assert.deepEqual(
        historyCategoryOptions(["movies", "tv"], "benchmark"),
        ["benchmark", "movies", "tv"],
    );
});

test("defines explicit history categories for profile playback", () => {
    assert.deepEqual(
        [...STREAMING_HISTORY_CATEGORIES],
        ["streaming-movie", "streaming-series"],
    );
});
