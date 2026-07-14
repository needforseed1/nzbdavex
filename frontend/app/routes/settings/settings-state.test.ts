import assert from "node:assert/strict";
import test from "node:test";
import {
    getDirtySettingsSections, getSettingsSection, getValidationSections, SETTINGS_SECTION_ORDER,
} from "./settings-state";

test("keeps the canonical GUI and YAML section order", () => {
    assert.deepEqual(SETTINGS_SECTION_ORDER, [
        "usenet", "indexers", "profiles", "watchdog", "preflight", "watchtower", "warden",
        "webdav", "sabnzbd", "arrs", "repairs", "rclone", "maintenance",
    ]);
});

test("assigns representative keys to every settings section", () => {
    const expected = new Map([
        ["usenet.providers", "usenet"],
        ["indexers.instances", "indexers"],
        ["profiles.instances", "profiles"],
        ["play.watchdog-enabled", "watchdog"],
        ["preflight.mode", "preflight"],
        ["watchtower.enabled", "watchtower"],
        ["warden.quorum", "warden"],
        ["webdav.user", "webdav"],
        ["api.categories", "sabnzbd"],
        ["arr.instances", "arrs"],
        ["media.library-dir", "repairs"],
        ["rclone.host", "rclone"],
        ["maintenance.remove-orphaned-schedule-time", "maintenance"],
    ] as const);

    for (const [key, section] of expected)
        assert.equal(getSettingsSection(key), section, key);
});

test("keeps shared-prefix exceptions with their visible owners", () => {
    assert.equal(getSettingsSection("api.user-agent"), "indexers");
    assert.equal(getSettingsSection("api.search-user-agent"), "indexers");
    assert.equal(getSettingsSection("rclone.mount-dir"), "sabnzbd");
    assert.equal(getSettingsSection("general.base-url"), "profiles");
    assert.equal(getSettingsSection("unknown.setting"), null);
});

test("derives cross-section validation dependencies", () => {
    const changedKeys = new Set([
        "indexers.instances",
        "profiles.instances",
        "arr.instances",
        "repair.enable",
        "general.base-url",
    ]);
    const dirty = getDirtySettingsSections(changedKeys);
    const validation = getValidationSections(changedKeys, dirty);

    assert.deepEqual([...dirty].sort(), ["arrs", "indexers", "profiles", "repairs"].sort());
    assert.deepEqual([...validation].sort(), [
        "arrs", "indexers", "profiles", "repairs", "maintenance", "watchtower", "sabnzbd",
    ].sort());
});
