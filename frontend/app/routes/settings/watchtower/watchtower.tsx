import { Form } from "react-bootstrap";
import { type Dispatch, type SetStateAction } from "react";
import styles from "./watchtower.module.css";

const GB = 1024 * 1024 * 1024;

type WatchtowerSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WatchtowerSettings({ config, setNewConfig }: WatchtowerSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const enabled = (config["watchtower.enabled"] ?? "false") === "true";
    const bytesToGb = (b?: string) => { const n = Number(b ?? ""); return n > 0 ? String(+(n / GB).toFixed(2)) : ""; };
    const setGb = (key: string, gb: string) => { const n = Number(gb); set(key, n > 0 ? String(Math.round(n * GB)) : "0"); };

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionTitle}>Watchtower</div>
                <div className={styles.sectionDescription}>
                    Keeps the titles on your lists pre-resolved to a healthy release and re-verified
                    over time, so each is found and ready before you need it. Pointer-only and
                    safe-by-default: it stores segment maps (kilobytes), never video, and respects your
                    indexer caps. Manage your lists on the <b>Watchtower</b> page; tune the engine here.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Check
                    type="switch"
                    id="watchtower-enabled"
                    label="Enable Watchtower"
                    checked={enabled}
                    onChange={e => set("watchtower.enabled", String(e.target.checked))} />
                <p className={styles.hint}>
                    When on, the background engine syncs your lists, resolves the biggest healthy
                    release for each item, and keeps it verified over time. When off, nothing runs.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Search profile token (optional)</Form.Label>
                <Form.Control className={styles.input} type="text"
                    placeholder="empty = use the first configured profile"
                    disabled={!enabled}
                    value={config["watchtower.profile-token"] ?? ""}
                    onChange={e => set("watchtower.profile-token", e.target.value)} />
                <p className={styles.hint}>Which Search Profile the resolver uses. Leave blank to use the first one.</p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Release selection</Form.Label>
                <Form.Select className={styles.input}
                    disabled={!enabled}
                    value={config["watchtower.ranking"] ?? "watchdog"}
                    onChange={e => set("watchtower.ranking", e.target.value)}>
                    <option value="watchdog">Match the watchdog's pick</option>
                    <option value="largest">Largest healthy release</option>
                </Form.Select>
                <p className={styles.hint}>
                    <b>Match the watchdog</b> uses the same rank order as the watchdog, so the release
                    Watchtower readies is exactly the one the watchdog would select. <b>Largest</b>
                    always prefers the biggest healthy release (it may differ from what the watchdog
                    would have chosen).
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Junk floor (GB)</Form.Label>
                <Form.Control className={styles.input} type="number" min={0} step={0.1}
                    disabled={!enabled}
                    value={bytesToGb(config["watchtower.size-floor-bytes"])}
                    onChange={e => setGb("watchtower.size-floor-bytes", e.target.value)} />
                <p className={styles.hint}>Ignore releases smaller than this. Default 0.5 GB.</p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Bandwidth ceiling (GB)</Form.Label>
                <Form.Control className={styles.input} type="number" min={0} step={0.5}
                    disabled={!enabled}
                    placeholder="empty = no ceiling"
                    value={bytesToGb(config["watchtower.size-ceiling-bytes"])}
                    onChange={e => setGb("watchtower.size-ceiling-bytes", e.target.value)} />
                <p className={styles.hint}>Ignore releases larger than this. Empty / 0 = no ceiling.</p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Active warm-set cap</Form.Label>
                <Form.Control className={styles.input} type="number" min={1} max={100000}
                    disabled={!enabled}
                    value={config["watchtower.active-set-cap"] ?? "100"}
                    onChange={e => set("watchtower.active-set-cap", e.target.value)} />
                <p className={styles.hint}>
                    How many items the engine keeps actively ready. Beyond this, items are listed but
                    parked until they bubble up. This is what bounds load no matter how big your lists get. Default 100.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Daily resolve budget</Form.Label>
                <Form.Control className={styles.input} type="number" min={0}
                    disabled={!enabled}
                    value={config["watchtower.daily-resolve-budget"] ?? "60"}
                    onChange={e => set("watchtower.daily-resolve-budget", e.target.value)} />
                <p className={styles.hint}>
                    Soft cap on new resolves per day (0 = unlimited; your per-indexer caps always apply).
                    Drips the backlog instead of hammering indexers. Default 60.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Shortlist depth</Form.Label>
                <Form.Control className={styles.input} type="number" min={1} max={5}
                    disabled={!enabled}
                    value={config["watchtower.shortlist-depth"] ?? "2"}
                    onChange={e => set("watchtower.shortlist-depth", e.target.value)} />
                <p className={styles.hint}>One live winner + backups kept per item, for instant failover. Default 2.</p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Grab cap per resolve</Form.Label>
                <Form.Control className={styles.input} type="number" min={1} max={10}
                    disabled={!enabled}
                    value={config["watchtower.grab-cap-per-resolve"] ?? "3"}
                    onChange={e => set("watchtower.grab-cap-per-resolve", e.target.value)} />
                <p className={styles.hint}>
                    Max NZB fetches (the scarce indexer bucket) per item per pass. Keeps resolves grab-thrifty. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>List sync interval (seconds)</Form.Label>
                <Form.Control className={styles.input} type="number" min={60} max={86400}
                    disabled={!enabled}
                    value={config["watchtower.sync-interval-seconds"] ?? "3600"}
                    onChange={e => set("watchtower.sync-interval-seconds", e.target.value)} />
                <p className={styles.hint}>How often remote lists are re-fetched to catch additions/removals. Default 3600.</p>
            </Form.Group>
        </div>
    );
}

export function isWatchtowerSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return [
        "watchtower.enabled",
        "watchtower.profile-token",
        "watchtower.ranking",
        "watchtower.size-floor-bytes",
        "watchtower.size-ceiling-bytes",
        "watchtower.shortlist-depth",
        "watchtower.grab-cap-per-resolve",
        "watchtower.active-set-cap",
        "watchtower.daily-resolve-budget",
        "watchtower.sync-interval-seconds",
    ].some(k => config[k] !== newConfig[k]);
}
