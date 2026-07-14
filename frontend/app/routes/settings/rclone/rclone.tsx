import { Button, Form, InputGroup, Spinner } from "react-bootstrap";
import styles from "./rclone.module.css"
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect } from "react";

type RcloneSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RcloneSettings({ config, setNewConfig }: RcloneSettingsProps) {
    const [connectionState, setConnectionState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');
    const isEnabled = config["rclone.rc-enabled"] === "true";
    const isHostValid = isAbsoluteHttpUrl(config["rclone.host"] ?? "");

    useEffect(() => {
        setConnectionState('idle');
    }, [config["rclone.host"], config["rclone.user"], config["rclone.pass"]]);

    const testConnection = useCallback(async () => {
        const host = config["rclone.host"];
        if (!isAbsoluteHttpUrl(host ?? "")) {
            return;
        }

        setConnectionState('testing');

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('user', config["rclone.user"] ?? '');
            formData.append('pass', config["rclone.pass"] ?? '');

            const response = await fetch('/api/test-rclone-connection', {
                method: 'POST',
                body: formData
            });

            const result = await response.json();

            if (result.status && result.connected) {
                setConnectionState('success');
            } else {
                setConnectionState('error');
            }
        } catch (error) {
            setConnectionState('error');
        }
    }, [config]);

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="switch"
                    id="rclone-rc-enabled-checkbox"
                    aria-describedby="rclone-rc-enabled-help"
                    label={`Enable Rclone RC Server Notifications`}
                    checked={isEnabled}
                    onChange={e => setNewConfig({ ...config, "rclone.rc-enabled": "" + e.target.checked })} />
                <Form.Text id="rclone-rc-enabled-help" muted>
                    When enabled, nzbdav will automatically notify your rclone mount via the RC API whenever files are added or removed on the webdav. This allows setting a high dir-cache-time setting on Rclone.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-host-input">Rclone Server Host</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        type="text"
                        id="rclone-host-input"
                        aria-describedby="rclone-host-help"
                        placeholder="http://localhost:5572"
                        value={config["rclone.host"]}
                        isInvalid={isEnabled && !isHostValid}
                        onChange={e => setNewConfig({ ...config, "rclone.host": e.target.value })} />
                    {isHostValid && (
                        <Button
                            variant={connectionState === 'success' ? 'success' :
                                connectionState === 'error' ? 'danger' : 'secondary'}
                            onClick={testConnection}
                            disabled={connectionState === 'testing'}
                            className={styles.testButton}
                        >
                            {
                                connectionState === 'testing' ? (
                                    <Spinner animation="border" size="sm" />
                                ) : connectionState === 'success' ? (
                                    '✓'
                                ) : connectionState === 'error' ? (
                                    '✗'
                                ) : (
                                    'Test Conn'
                                )
                            }
                        </Button>
                    )}
                </InputGroup>
                <Form.Text id="rclone-host-help" muted>
                    The absolute HTTP(S) address of the rclone RC API. Required when notifications are enabled.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-user-input">Rclone Server User</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="rclone-user-input"
                    aria-describedby="rclone-user-help"
                    value={config["rclone.user"]}
                    onChange={e => setNewConfig({ ...config, "rclone.user": e.target.value })} />
                <Form.Text id="rclone-user-help" muted>
                    The username for authenticating to the rclone RC API. This field is optional.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-pass-input">Rclone Server Password</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="password"
                    id="rclone-pass-input"
                    aria-describedby="rclone-pass-help"
                    value={config["rclone.pass"]}
                    onChange={e => setNewConfig({ ...config, "rclone.pass": e.target.value })} />
                <Form.Text id="rclone-pass-help" muted>
                    Stored passwords are not shown. Leave blank to keep the current password,
                    or enter a value to replace it. Authentication is optional.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isRcloneSettingsValid(config: Record<string, string>) {
    return config["rclone.rc-enabled"] !== "true"
        || isAbsoluteHttpUrl(config["rclone.host"] ?? "");
}

function isAbsoluteHttpUrl(value: string) {
    try {
        const url = new URL(value.trim());
        return (url.protocol === "http:" || url.protocol === "https:") && url.host !== "";
    } catch {
        return false;
    }
}
