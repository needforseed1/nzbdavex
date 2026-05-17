import { Form } from "react-bootstrap";
import { Link } from "react-router";
import { type Dispatch, type SetStateAction, useMemo } from "react";
import styles from "./watchdog.module.css";

type WatchdogSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

type PatternIssue = { line: number, pattern: string, error: string };

function validateExcludePatterns(raw: string): PatternIssue[] {
    const issues: PatternIssue[] = [];
    const lines = raw.split("\n");
    for (let i = 0; i < lines.length; i++) {
        const trimmed = lines[i].trim();
        if (trimmed.length === 0 || trimmed.startsWith("#")) continue;
        try {
            new RegExp(trimmed, "i");
        } catch (e: any) {
            issues.push({ line: i + 1, pattern: trimmed, error: e?.message ?? "invalid regex" });
        }
    }
    return issues;
}

export function WatchdogSettings({ config, setNewConfig }: WatchdogSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const verifyMode = config["play.verify-mode"] ?? "none";
    const enabled = (config["play.watchdog-enabled"] ?? "true") === "true";
    const excludePatterns = config["play.exclude-patterns"] ?? "";
    const patternIssues = useMemo(() => validateExcludePatterns(excludePatterns), [excludePatterns]);

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionTitle}>Playback fast-fail</div>
                <div className={styles.sectionDescription}>
                    When a user clicks Play, nzbdav tries the top-ranked release first; if it can't deliver
                    fast enough, alternatives are tried automatically. These knobs control how aggressive
                    that fallback is, so the player never hangs on a dead release.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Check
                    type="switch"
                    id="play-watchdog-enabled"
                    label="Enable playback watchdog"
                    checked={enabled}
                    onChange={e => set("play.watchdog-enabled", String(e.target.checked))} />
                <p className={styles.hint}>
                    When off, a Play click just processes the single chosen release (legacy behavior).
                    When on, the watchdog tries alternative releases on failure and dedupes in-flight queue items.
                    {enabled && <> Live reports appear in the <Link to="/watchdog">Watchdog</Link> tab in the sidebar.</>}
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Total budget (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={3}
                    max={180}
                    value={config["play.total-budget-seconds"] ?? "30"}
                    onChange={e => set("play.total-budget-seconds", e.target.value)} />
                <p className={styles.hint}>
                    Hard ceiling for a Play click. Big UHD releases need ~15–30s for the queue to extract
                    file metadata. If exceeded, the player gets a retry-able error; the queue item keeps
                    processing in the background and a re-click resolves it. Default 30.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Hedge delay (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={30}
                    disabled={!enabled}
                    value={config["play.hedge-delay-seconds"] ?? "3"}
                    onChange={e => set("play.hedge-delay-seconds", e.target.value)} />
                <p className={styles.hint}>
                    If the primary candidate hasn't passed verification by this many seconds, backup
                    candidates start in parallel. Lower = more eager fallback, slightly higher provider load. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Parallel candidates per batch</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={10}
                    disabled={!enabled}
                    value={config["play.max-candidates"] ?? "3"}
                    onChange={e => set("play.max-candidates", e.target.value)} />
                <p className={styles.hint}>
                    How many candidates run at the same time in one round. Higher means faster
                    failover when a candidate fails, but more simultaneous load. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Total candidates per click</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={200}
                    disabled={!enabled}
                    value={config["play.max-attempts"] ?? "10"}
                    onChange={e => set("play.max-attempts", e.target.value)} />
                <p className={styles.hint}>
                    The most candidates one attempt will try in total before giving up. With the
                    defaults (3 per batch, 10 total) it tries up to 3 + 3 + 3 + 1 candidates over
                    four rounds, then stops. Also stops sooner if the total budget runs out. Default 10.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Verify mode</Form.Label>
                <Form.Select
                    className={styles.input}
                    disabled={!enabled}
                    value={verifyMode}
                    onChange={e => set("play.verify-mode", e.target.value)}>
                    <option value="none">none — no pre-check, enqueue right away (recommended)</option>
                    <option value="stat">stat — STAT first segment (~0.2s; skips candidates flagged dead)</option>
                    <option value="body">body — strict, downloads first article (~1–2s)</option>
                </Form.Select>
                <p className={styles.hint}>
                    `none` is safest: every candidate gets enqueued and falls back to the next on confirmed
                    failure. `stat` adds a pre-filter but can drop legit releases when providers are slow.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Negative-cache TTL (minutes)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={1440}
                    disabled={!enabled}
                    value={config["play.candidate-negative-cache-minutes"] ?? "5"}
                    onChange={e => set("play.candidate-negative-cache-minutes", e.target.value)} />
                <p className={styles.hint}>
                    How long a recently-failed release is skipped on subsequent clicks, so we don't hammer
                    the same dead release. Default 5.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Exclude result patterns</Form.Label>
                <Form.Control
                    as="textarea"
                    rows={6}
                    spellCheck={false}
                    className={`${styles.input} ${styles.patternInput} ${patternIssues.length > 0 ? styles.patternInputInvalid : ""}`}
                    placeholder={"# one regex per line\n# lines starting with # are comments"}
                    value={excludePatterns}
                    onChange={e => set("play.exclude-patterns", e.target.value)} />
                {patternIssues.length > 0 && (
                    <div className={styles.patternErrors}>
                        {patternIssues.map((iss, i) => (
                            <div key={i} className={styles.patternError}>
                                <span className={styles.patternErrorLine}>Line {iss.line}</span>
                                <code className={styles.patternErrorPattern}>{iss.pattern}</code>
                                <span className={styles.patternErrorMessage}>— {iss.error}</span>
                            </div>
                        ))}
                    </div>
                )}
                <p className={styles.hint}>
                    One JavaScript-style regex per line. Candidates whose title matches any pattern are
                    skipped before NZB fetch and appear in the <Link to="/watchdog">Watchdog</Link> log as
                    "excluded". Case-insensitive by default — use <code>(?-i:Foo)</code> for case-sensitive.
                    Lines starting with <code>#</code> are comments. Use this to skip releases your setup
                    can't handle, whatever the reason.
                </p>
            </Form.Group>
        </div>
    );
}

export function isWatchdogSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["play.watchdog-enabled"] !== newConfig["play.watchdog-enabled"]
        || config["play.total-budget-seconds"] !== newConfig["play.total-budget-seconds"]
        || config["play.hedge-delay-seconds"] !== newConfig["play.hedge-delay-seconds"]
        || config["play.max-candidates"] !== newConfig["play.max-candidates"]
        || config["play.max-attempts"] !== newConfig["play.max-attempts"]
        || config["play.verify-mode"] !== newConfig["play.verify-mode"]
        || config["play.candidate-negative-cache-minutes"] !== newConfig["play.candidate-negative-cache-minutes"]
        || (config["play.exclude-patterns"] ?? "") !== (newConfig["play.exclude-patterns"] ?? "");
}

export function isWatchdogSettingsValid(config: Record<string, string>) {
    return validateExcludePatterns(config["play.exclude-patterns"] ?? "").length === 0;
}
