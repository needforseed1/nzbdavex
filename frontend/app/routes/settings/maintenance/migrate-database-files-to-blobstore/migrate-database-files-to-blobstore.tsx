import { Form } from "react-bootstrap";
import styles from "./migrate-database-files-to-blobstore.module.css";
import { useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";

const TaskTopic = { 'uftbmp': 'state' };

type MigrateDatabaseFilesToBlobstoreProps = {
    onComplete: () => void;
};

export function MigrateDatabaseFilesToBlobstore({ onComplete }: MigrateDatabaseFilesToBlobstoreProps) {
    // stateful variables
    const [progress, setProgress] = useState<string | null>(null);

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => {
                setProgress(message);
                if (message.startsWith("Done!")) onComplete();
            });
            ws.onopen = () => { ws.send(JSON.stringify(TaskTopic)); }
            ws.onclose = () => { !disposed && setTimeout(() => connect(), 1000); setProgress(null) };
            ws.onerror = () => { ws.close() };
            return () => { disposed = true; ws.close(); }
        }
        return connect();
    }, [setProgress, onComplete]);

    return (
        <div className={styles.task}>
            <Form.Group>
                <Form.Text id="blob-task-progress-help" muted>
                    <br />
                    This task runs automatically in the background to optimize the database. No action is required on your part.
                    You can simply track the progress here. For context, the sqlite database used by the backend is slow at reading and writing large data blobs.
                    It is better to store those externally in the filesystem directly, as documented <a href="https://sqlite.org/intern-v-extern-blob.html">here</a>.
                    However, as of now, all blobs have been stored in the database directly. This task migrates those blobs to the filesystem, for better performance.
                    <br />
                    <br />
                    <code>
                        {progress || "The task has not started."}
                    </code>
                </Form.Text>
            </Form.Group>
        </div>
    );
}
