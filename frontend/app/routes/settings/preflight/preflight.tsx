import { Form } from "react-bootstrap";
import { type Dispatch, type SetStateAction } from "react";
import styles from "./preflight.module.css";

type PreflightSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

function isIntegerInRange(raw: string, min: number, max: number): boolean {
    if (!/^-?\d+$/.test(raw.trim())) return false;
    const value = Number(raw);
    return Number.isSafeInteger(value) && value >= min && value <= max;
}

export function PreflightSettings({ config, setNewConfig }: PreflightSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const mode = config["preflight.mode"] ?? "off";
    const enabled = mode !== "off";

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionTitle}>Preflight</div>
                <div className={styles.sectionDescription}>
                    When a client asks for the list of available articles, nzbdav can quietly
                    do upfront work on the top-ranked ones so the next request reuses that warm
                    state instead of redoing everything from scratch. The harder the mode, the
                    more it does.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Label>Mode</Form.Label>
                <Form.Select
                    className={styles.input}
                    value={mode}
                    onChange={e => set("preflight.mode", e.target.value)}>
                    <option value="off">off — no background work</option>
                    <option value="light">light — quick existence check on the top results</option>
                    <option value="standard">standard — light + cache NZB bytes</option>
                    <option value="full">full — standard + resolve archive layout for previously completed items</option>
                </Form.Select>
                <p className={styles.hint}>
                    <b>light</b> performs a cheap existence check against your provider, so missing
                    articles are skipped without re-asking the indexer.
                    <b> standard</b> additionally keeps the fetched NZB bytes locally so the next
                    request can skip the indexer download.
                    <b> full</b> additionally resolves trailing-archive metadata for any top result
                    that maps to a previously completed item — useful when re-opening something.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Max candidates to try</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={50}
                    disabled={!enabled}
                    value={config["preflight.max-attempts"] ?? "20"}
                    onChange={e => set("preflight.max-attempts", e.target.value)} />
                <p className={styles.hint}>
                    Walks the top-ranked results one at a time and stops on the first one that
                    passes the check. So a missing top result automatically falls through to
                    the next one — same idea as the watchdog at click time, but in the
                    background. Default 20.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Verify sample count</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={10}
                    disabled={!enabled}
                    value={config["preflight.verify-sample-count"] ?? "3"}
                    onChange={e => set("preflight.verify-sample-count", e.target.value)} />
                <p className={styles.hint}>
                    Number of segments sampled with cheap STAT checks before a candidate is
                    considered warm. More samples catch more bad releases but cost more startup
                    prep. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Keep preflight state for (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={10}
                    max={1800}
                    disabled={!enabled}
                    value={config["preflight.ttl-seconds"] ?? "120"}
                    onChange={e => set("preflight.ttl-seconds", e.target.value)} />
                <p className={styles.hint}>
                    How long a preflighted result and shared NZB fetch cache stay warm before
                    they're discarded. Long enough to scroll through and pick something, short
                    enough not to hold stale state. Default 120.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Skip if indexer wait exceeds (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={0}
                    max={120}
                    disabled={!enabled}
                    value={config["preflight.indexer-max-wait-seconds"] ?? "5"}
                    onChange={e => set("preflight.indexer-max-wait-seconds", e.target.value)} />
                <p className={styles.hint}>
                    Preflight is best-effort: if an indexer's rate limit would force it to wait
                    longer than this before a request can fire, preflight on that result is
                    skipped. Keeps real requests from being queued behind speculative work.
                    Default 5.
                </p>
            </Form.Group>
        </div>
    );
}

export function isPreflightSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["preflight.mode"] !== newConfig["preflight.mode"]
        || config["preflight.max-attempts"] !== newConfig["preflight.max-attempts"]
        || config["preflight.verify-sample-count"] !== newConfig["preflight.verify-sample-count"]
        || config["preflight.ttl-seconds"] !== newConfig["preflight.ttl-seconds"]
        || config["preflight.indexer-max-wait-seconds"] !== newConfig["preflight.indexer-max-wait-seconds"];
}

export function isPreflightSettingsValid(config: Record<string, string>) {
    const value = (key: string, fallback: string) => config[key] ?? fallback;
    if (!["off", "light", "standard", "full"].includes(value("preflight.mode", "off"))) return false;

    return isIntegerInRange(value("preflight.max-attempts", "20"), 1, 50)
        && isIntegerInRange(value("preflight.verify-sample-count", "3"), 1, 10)
        && isIntegerInRange(value("preflight.ttl-seconds", "120"), 10, 1800)
        && isIntegerInRange(value("preflight.indexer-max-wait-seconds", "5"), 0, 120);
}
