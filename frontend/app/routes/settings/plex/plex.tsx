import { Button, Form, InputGroup, Spinner } from "react-bootstrap";
import { type Dispatch, type SetStateAction, useCallback, useEffect, useState } from "react";
import styles from "../rclone/rclone.module.css";

type PlexSettingsProps = {
    config: Record<string, string>,
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>,
};

type ConnectionResult = {
    state: "idle" | "testing" | "success" | "error",
    detail?: string,
};

export function PlexSettings({ config, setNewConfig }: PlexSettingsProps) {
    const [result, setResult] = useState<ConnectionResult>({ state: "idle" });
    const enabled = config["plex.enabled"] === "true";
    const baseUrl = config["plex.base-url"] ?? "";
    const urlValid = isAbsoluteHttpUrl(baseUrl);

    useEffect(() => {
        setResult({ state: "idle" });
    }, [baseUrl, config["plex.token"]]);

    const testConnection = useCallback(async () => {
        if (!urlValid) return;
        setResult({ state: "testing" });
        try {
            const form = new FormData();
            form.append("baseUrl", baseUrl);
            form.append("token", config["plex.token"] ?? "");
            const response = await fetch("/api/test-plex-connection", {
                method: "POST",
                body: form,
            });
            const data = await response.json();
            if (response.ok && data.status && data.connected) {
                const identity = [data.serverName, data.serverVersion]
                    .filter(Boolean)
                    .join(" ");
                setResult({
                    state: "success",
                    detail: `${identity || "Connected"} · ${
                        data.activitiesAvailable === false
                            ? "scan detection unavailable"
                            : "scan detection available"
                    }`,
                });
            } else {
                setResult({
                    state: "error",
                    detail: data.error || "Connection failed",
                });
            }
        } catch (error) {
            setResult({
                state: "error",
                detail: error instanceof Error ? error.message : "Connection failed",
            });
        }
    }, [baseUrl, config, urlValid]);

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="switch"
                    id="plex-enabled"
                    label="Identify Plex reads through the rclone mount"
                    checked={enabled}
                    onChange={event => setNewConfig({
                        ...config,
                        "plex.enabled": String(event.target.checked),
                    })} />
                <Form.Text muted>
                    Adds source and purpose labels to new completed NzbDAVex reads.
                    It does not import Plex history or create a separate playback tracker.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="plex-base-url">Plex server URL</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        id="plex-base-url"
                        type="text"
                        placeholder="http://plex:32400"
                        value={baseUrl}
                        isInvalid={enabled && !urlValid}
                        onChange={event => setNewConfig({
                            ...config,
                            "plex.base-url": event.target.value,
                        })} />
                    {urlValid && (
                        <Button
                            variant={result.state === "success" ? "success"
                                : result.state === "error" ? "danger"
                                : "secondary"}
                            onClick={testConnection}
                            disabled={result.state === "testing"}
                            className={styles.testButton}>
                            {result.state === "testing"
                                ? <Spinner animation="border" size="sm" />
                                : result.state === "success" ? "✓"
                                : result.state === "error" ? "✗"
                                : "Test Conn"}
                        </Button>
                    )}
                </InputGroup>
                <Form.Text muted>
                    Use an address reachable from the NzbDAVex container.
                    {result.detail && <> Test result: {result.detail}.</>}
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="plex-token">Plex server token (X-Plex-Token)</Form.Label>
                <Form.Control
                    className={styles.input}
                    id="plex-token"
                    type="password"
                    value={config["plex.token"] ?? ""}
                    onChange={event => setNewConfig({
                        ...config,
                        "plex.token": event.target.value,
                    })} />
                <Form.Text muted>
                    Stored tokens are never shown. Leave blank to keep the current token.
                    The token is sent only to the configured Plex server.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="plex-path-prefix">Plex media path prefix</Form.Label>
                <Form.Control
                    className={styles.input}
                    id="plex-path-prefix"
                    type="text"
                    placeholder="/data/media"
                    value={config["plex.path-prefix"] ?? ""}
                    onChange={event => setNewConfig({
                        ...config,
                        "plex.path-prefix": event.target.value,
                    })} />
                <Form.Text muted>
                    Optional. The beginning of paths Plex reports for media files.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="plex-local-path-prefix">Local media path prefix</Form.Label>
                <Form.Control
                    className={styles.input}
                    id="plex-local-path-prefix"
                    type="text"
                    placeholder="/media"
                    value={config["plex.local-path-prefix"] ?? ""}
                    onChange={event => setNewConfig({
                        ...config,
                        "plex.local-path-prefix": event.target.value,
                    })} />
                <Form.Text muted>
                    Optional. The same directory as visible inside NzbDAVex.
                    Exact .ids symlinks or STRM targets are used; filenames are never guessed.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isPlexSettingsValid(config: Record<string, string>) {
    if (config["plex.enabled"] === "true"
        && !isAbsoluteHttpUrl(config["plex.base-url"] ?? ""))
        return false;
    const plexPrefix = (config["plex.path-prefix"] ?? "").trim();
    const localPrefix = (config["plex.local-path-prefix"] ?? "").trim();
    return (plexPrefix === "") === (localPrefix === "");
}

function isAbsoluteHttpUrl(value: string) {
    try {
        const url = new URL(value.trim());
        return (url.protocol === "http:" || url.protocol === "https:") && url.host !== "";
    } catch {
        return false;
    }
}
