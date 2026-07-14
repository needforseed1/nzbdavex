import { Form } from "react-bootstrap";
import styles from "./repairs.module.css"
import { type Dispatch, type SetStateAction } from "react";

type RepairsSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RepairsSettings({ config, setNewConfig }: RepairsSettingsProps) {
    const libraryDirConfig = config["media.library-dir"] ?? "";
    const repairsEnabled = config["repair.enable"] === "true";
    const canEnableRepairs = libraryDirConfig.trim() !== ""
        && hasValidArrInstance(config["arr.instances"]);
    const helpText = canEnableRepairs
        ? "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed. If an unhealthy item is part of your Radarr/Sonarr library, a new search will be triggered to find a replacement."
        : repairsEnabled
            ? "Background Repairs is still enabled, but its library directory or Radarr/Sonarr connection is no longer valid. Restore the prerequisite or turn Repairs off before saving."
            : "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed and replaced. This setting can only be enabled once your Library Directory and a valid Radarr/Sonarr instance are configured.";

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="switch"
                    id="enable-repairs-checkbox"
                    aria-describedby="enable-repairs-help"
                    label={`Enable Background Repairs`}
                    checked={repairsEnabled}
                    disabled={!repairsEnabled && !canEnableRepairs}
                    onChange={e => {
                        if (e.target.checked && !canEnableRepairs) return;
                        setNewConfig({ ...config, "repair.enable": "" + e.target.checked });
                    }} />
                <Form.Text id="enable-repairs-help" muted>
                    {helpText}
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="library-dir-input">Library Directory</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="library-dir-input"
                    aria-describedby="library-dir-help"
                    value={config["media.library-dir"]}
                    isInvalid={repairsEnabled && libraryDirConfig.trim() === ""}
                    onChange={e => setNewConfig({ ...config, "media.library-dir": e.target.value })} />
                <Form.Text id="library-dir-help" muted>
                    The path to your organized media library that contains all your imported symlinks or *.strm files.
                    Make sure this path is visible to your davex container.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isRepairsSettingsValid(config: Record<string, string>) {
    return config["repair.enable"] !== "true"
        || ((config["media.library-dir"] ?? "").trim() !== ""
            && hasValidArrInstance(config["arr.instances"]));
}

function hasValidArrInstance(rawConfig: string | undefined) {
    try {
        const parsed = JSON.parse(rawConfig || "{}");
        const instances = [
            ...(Array.isArray(parsed.RadarrInstances) ? parsed.RadarrInstances : []),
            ...(Array.isArray(parsed.SonarrInstances) ? parsed.SonarrInstances : []),
        ];
        return instances.some(instance =>
            isAbsoluteHttpUrl(instance?.Host)
            && typeof instance?.ApiKey === "string"
            && instance.ApiKey.trim() !== "");
    } catch {
        return false;
    }
}

function isAbsoluteHttpUrl(value: unknown) {
    if (typeof value !== "string") return false;
    try {
        const url = new URL(value.trim());
        return (url.protocol === "http:" || url.protocol === "https:") && url.host !== "";
    } catch {
        return false;
    }
}
