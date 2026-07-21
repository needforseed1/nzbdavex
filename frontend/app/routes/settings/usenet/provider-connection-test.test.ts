import assert from "node:assert/strict";
import test from "node:test";
import {
    getProviderConnectionSignature,
    requiresProviderConnectionTest,
    type ProviderConnectionIdentity,
} from "./provider-connection-test";

const saved: ProviderConnectionIdentity = {
    host: "news.example.com",
    port: 563,
    useSsl: true,
    user: "reader",
    pass: "secret",
};

test("does not require retesting unchanged connection details", () => {
    assert.equal(requiresProviderConnectionTest(saved, { ...saved, port: "563" }), false);
    assert.equal(
        getProviderConnectionSignature(saved),
        getProviderConnectionSignature({ ...saved, port: "563" }),
    );
});

test("requires a test for new providers and changed connection details", () => {
    assert.equal(requiresProviderConnectionTest(null, saved), true);

    for (const changed of [
        { ...saved, host: "news2.example.com" },
        { ...saved, port: 119 },
        { ...saved, useSsl: false },
        { ...saved, user: "another-reader" },
        { ...saved, pass: "another-secret" },
    ]) {
        assert.equal(requiresProviderConnectionTest(saved, changed), true);
    }
});
