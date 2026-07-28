export type QueueStatusMessage = {
    nzo_id: string,
    status: string,
    error?: string,
};

export function parseQueueStatusMessage(message: string): QueueStatusMessage | null {
    const firstSeparator = message.indexOf('|');
    if (firstSeparator <= 0) return null;

    const nzo_id = message.slice(0, firstSeparator);
    const payload = message.slice(firstSeparator + 1);
    const secondSeparator = payload.indexOf('|');
    const status = secondSeparator < 0
        ? payload
        : payload.slice(0, secondSeparator);
    if (!status) return null;

    const error = secondSeparator < 0
        ? undefined
        : payload.slice(secondSeparator + 1) || undefined;
    return error === undefined
        ? { nzo_id, status }
        : { nzo_id, status, error };
}
