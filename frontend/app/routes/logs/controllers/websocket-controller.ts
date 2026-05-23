import { useEffect } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import type { LogBroadcastMessage, LogEntry } from "~/clients/backend-client.server";

const LOG_TOPIC = "log";
const topicSubscriptions = { [LOG_TOPIC]: "event" };

export type ConnectionStatus = "connecting" | "live" | "reconnecting" | "disconnected";

export function useLogsWebsocket(
    onBatch: (entries: LogEntry[]) => void,
    onStatus: (status: ConnectionStatus) => void,
) {
    useEffect(() => {
        let ws: WebSocket | null = null;
        let disposed = false;
        let reconnectTimer: ReturnType<typeof setTimeout> | null = null;

        const connect = () => {
            onStatus("connecting");
            ws = new WebSocket(window.location.origin.replace(/^http/, "ws"));
            ws.onopen = () => {
                ws?.send(JSON.stringify(topicSubscriptions));
                onStatus("live");
            };
            ws.onmessage = receiveMessage((topic, message) => {
                if (topic !== LOG_TOPIC) return;
                try {
                    const parsed = JSON.parse(message) as LogBroadcastMessage;
                    if (parsed?.entries?.length) onBatch(parsed.entries);
                } catch {
                    // ignore malformed payloads
                }
            });
            ws.onclose = () => {
                if (disposed) return;
                onStatus("reconnecting");
                reconnectTimer = setTimeout(connect, 1000);
            };
            ws.onerror = () => ws?.close();
        };

        connect();
        return () => {
            disposed = true;
            if (reconnectTimer) clearTimeout(reconnectTimer);
            ws?.close();
            onStatus("disconnected");
        };
    }, [onBatch, onStatus]);
}
