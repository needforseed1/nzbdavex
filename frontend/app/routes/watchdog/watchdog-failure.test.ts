import assert from "node:assert/strict";
import test from "node:test";
import type {
    WatchdogEntry,
    WatchdogPrepStats,
} from "../../clients/backend-client.server";
import {
    deriveFailurePhase,
    formatPrepFailures,
    selectFailedDetailsAttempt,
    summarizeFailure,
} from "./watchdog-failure";

test("selects the failed attempt with the most useful captured statistics", () => {
    const earlyFailure = entry({
        rankIndex: 0,
        outcome: "EnqueueFailed",
        failReason: "Queue did not return an item ID",
    });
    const queueFailure = entry({
        rankIndex: 1,
        outcome: "QueueFailed",
        prepStats: prepStats("health"),
        prepDurationMs: 750,
        healthDurationMs: 1_250,
        healthStats: {
            totalArticles: 1_000,
            foundArticles: null,
            missingArticles: 1,
            providers: [],
        },
    });

    assert.equal(selectFailedDetailsAttempt([earlyFailure, queueFailure]), queueFailure);
});

test("summarizes common failures while preserving useful unknown messages", () => {
    assert.equal(summarizeFailure("Article with message-id x was not found"), "Missing articles");
    assert.equal(
        summarizeFailure("Preparation could not verify a required article: provider checks returned no result"),
        "Article availability unverified",
    );
    assert.equal(summarizeFailure("Provider timed out after 5 seconds"), "Provider or operation timeout");
    assert.equal(summarizeFailure("No viable provider connections"), "Provider unavailable");
    assert.equal(summarizeFailure("Unexpected archive structure"), "Unexpected archive structure");
    assert.equal(summarizeFailure(null), "Run failed");
});

test("presents prep timeouts as unanswered checks while retaining other counts", () => {
    const provider: WatchdogPrepStats["providers"][number] = {
        providerId: "provider",
        host: "news.example.test",
        articles: 0,
        attempts: 5,
        missing: 2,
        timeouts: 1,
        errors: 2,
        workMs: 5_000,
        bytes: 0,
    };

    assert.equal(formatPrepFailures(provider), "2 missing · 1 no response · 2 errors");
    assert.equal(formatPrepFailures({ ...provider, missing: 0, timeouts: 2, errors: 0 }),
        "2 no responses");
});

test("shows only explicitly captured failure phases", () => {
    assert.equal(deriveFailurePhase(prepStats("first-segments")), "Prep · first segments");
    assert.equal(deriveFailurePhase(prepStats("health")), "Health check");
    assert.equal(deriveFailurePhase(prepStats("import")), "Import");
    assert.equal(deriveFailurePhase(prepStats(null)), null);
    assert.equal(deriveFailurePhase(null), null);
});

function entry(overrides: Partial<WatchdogEntry>): WatchdogEntry {
    return {
        clickId: "click",
        attemptedAtUnix: 1,
        contentType: "movie",
        requestedTitle: "Movie",
        candidateTitle: "Movie.Release",
        indexerName: "Indexer",
        size: 1,
        rankIndex: 0,
        outcome: "QueueFailed",
        failReason: "Run failed",
        durationMs: 1,
        isWinner: false,
        ...overrides,
    };
}

function prepStats(lastStage: string | null): WatchdogPrepStats {
    return {
        fileCount: 1,
        connections: 5,
        queueWaitMs: 1,
        firstSegmentsMs: 2,
        par2Ms: 3,
        rarMs: 4,
        processorsMs: 5,
        lazyRarMounted: false,
        firstSegmentFallbacks: 0,
        lastStage,
        providers: [],
    };
}
