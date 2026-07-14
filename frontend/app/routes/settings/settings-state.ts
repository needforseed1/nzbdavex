export type SettingsSection =
    "usenet" | "indexers" | "profiles" | "watchdog" | "preflight" | "watchtower" | "warden"
    | "webdav" | "sabnzbd" | "arrs" | "repairs" | "rclone" | "maintenance";

export const SETTINGS_SECTION_ORDER: readonly SettingsSection[] = [
    "usenet", "indexers", "profiles", "watchdog", "preflight", "watchtower", "warden",
    "webdav", "sabnzbd", "arrs", "repairs", "rclone", "maintenance",
];

export function getSettingsSection(key: string): SettingsSection | null {
    if (key.startsWith("usenet.")) return "usenet";
    if (["indexers.instances", "api.user-agent", "api.search-user-agent", "search.exclude-patterns"].includes(key))
        return "indexers";
    if (key.startsWith("profiles.") || key === "general.base-url") return "profiles";
    if (key.startsWith("play.") || key.startsWith("grab.") || key.startsWith("variants.")) return "watchdog";
    if (key.startsWith("preflight.")) return "preflight";
    if (key.startsWith("watchtower.")) return "watchtower";
    if (key.startsWith("warden.")) return "warden";
    if (key.startsWith("webdav.")) return "webdav";
    if (key === "rclone.mount-dir" || key.startsWith("api.")) return "sabnzbd";
    if (key === "arr.instances") return "arrs";
    if (key.startsWith("repair.") || key === "media.library-dir") return "repairs";
    if (key.startsWith("rclone.")) return "rclone";
    if (key.startsWith("db.") || key.startsWith("maintenance.")) return "maintenance";
    return null;
}

export function getDirtySettingsSections(changedKeys: ReadonlySet<string>): ReadonlySet<SettingsSection> {
    const sections = new Set<SettingsSection>();
    for (const key of changedKeys) {
        const section = getSettingsSection(key);
        if (section) sections.add(section);
    }
    return sections;
}

export function getValidationSections(
    changedKeys: ReadonlySet<string>,
    dirtySections = getDirtySettingsSections(changedKeys),
): ReadonlySet<SettingsSection> {
    const sections = new Set(dirtySections);
    if (dirtySections.has("indexers")) sections.add("profiles");
    if (dirtySections.has("profiles")) sections.add("watchtower");
    if (dirtySections.has("arrs")) sections.add("repairs");
    if (dirtySections.has("repairs")) sections.add("maintenance");
    if (changedKeys.has("general.base-url")) sections.add("sabnzbd");
    return sections;
}
