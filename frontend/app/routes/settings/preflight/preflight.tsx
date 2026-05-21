import { Form } from "react-bootstrap";
import { type Dispatch, type SetStateAction } from "react";
import styles from "./preflight.module.css";

type PreflightSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

type ModeOption = {
    value: string;
    label: string;
    description: string;
    cost: string;
    recommended?: boolean;
};

const MODES: ModeOption[] = [
    {
        value: "off",
        label: "Off",
        description: "Nothing happens in the background. Every Play click pays every cost cold.",
        cost: "No background work.",
    },
    {
        value: "light",
        label: "Light",
        description: "Quickly checks that the top results are reachable. Skips dead releases on the next click without re-asking the indexer.",
        cost: "One STAT round-trip per top result (~80 ms each).",
    },
    {
        value: "standard",
        label: "Standard",
        description: "Light + keeps the article descriptor cached locally so the next Play skips the indexer round-trip entirely.",
        cost: "One NZB fetch per top result + the cost of Light. ~50 KB – 1 MB cached per result.",
        recommended: true,
    },
    {
        value: "full",
        label: "Full",
        description: "Standard + pre-resolves trailing-volume metadata for any top result that maps to a previously completed download. Re-watching gets near-instant.",
        cost: "Standard + a few segment fetches per matched result. Only acts when an existing match is found.",
    },
];

export function PreflightSettings({ config, setNewConfig }: PreflightSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const mode = config["preflight.mode"] ?? "standard";
    const candidateCount = config["preflight.candidate-count"] ?? "3";
    const ttlSeconds = config["preflight.ttl-seconds"] ?? "120";
    const maxWaitSeconds = config["preflight.indexer-max-wait-seconds"] ?? "5";

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionTitle}>Preflight</div>
                <div className={styles.sectionDescription}>
                    When a player asks for the list of available articles, nzbdav can quietly do
                    upfront work on the top-ranked ones so the next Play click reuses that warm
                    state instead of redoing everything from scratch. The harder the mode, the
                    more it does — and the faster Play feels.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Label>Mode</Form.Label>
                <div className={styles.modeGrid}>
                    {MODES.map(opt => (
                        <label
                            key={opt.value}
                            className={`${styles.modeCard} ${mode === opt.value ? styles.modeCardSelected : ""}`}>
                            <input
                                type="radio"
                                name="preflight-mode"
                                className={styles.modeRadio}
                                value={opt.value}
                                checked={mode === opt.value}
                                onChange={() => set("preflight.mode", opt.value)} />
                            <div className={styles.modeBody}>
                                <div className={styles.modeName}>
                                    {opt.label}
                                    {opt.recommended && (
                                        <span className={styles.modeRecommended}>Recommended</span>
                                    )}
                                </div>
                                <div className={styles.modeDescription}>{opt.description}</div>
                                <div className={styles.modeCost}>{opt.cost}</div>
                            </div>
                        </label>
                    ))}
                </div>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Top results to preflight</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={10}
                    disabled={mode === "off"}
                    value={candidateCount}
                    onChange={e => set("preflight.candidate-count", e.target.value)} />
                <p className={styles.hint}>
                    How many of the top-ranked results from each stream list get preflighted.
                    Higher values cover more fallback positions if the first one fails, but use
                    more indexer quota. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Keep preflight state for (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={10}
                    max={1800}
                    disabled={mode === "off"}
                    value={ttlSeconds}
                    onChange={e => set("preflight.ttl-seconds", e.target.value)} />
                <p className={styles.hint}>
                    How long a preflighted result stays warm before it's discarded. Long enough
                    for the user to scroll through and pick something, short enough not to hold
                    stale state. Default 120.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Skip if indexer wait exceeds (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={0}
                    max={120}
                    disabled={mode === "off"}
                    value={maxWaitSeconds}
                    onChange={e => set("preflight.indexer-max-wait-seconds", e.target.value)} />
                <p className={styles.hint}>
                    Preflight is best-effort: if an indexer's rate limit would force it to wait
                    longer than this before a request can fire, preflight on that result is
                    skipped. Keeps real Play clicks from being queued behind speculative work.
                    Default 5.
                </p>
            </Form.Group>
        </div>
    );
}

export function isPreflightSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["preflight.mode"] !== newConfig["preflight.mode"]
        || config["preflight.candidate-count"] !== newConfig["preflight.candidate-count"]
        || config["preflight.ttl-seconds"] !== newConfig["preflight.ttl-seconds"]
        || config["preflight.indexer-max-wait-seconds"] !== newConfig["preflight.indexer-max-wait-seconds"];
}
