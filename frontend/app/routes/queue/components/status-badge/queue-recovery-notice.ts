import type { QueueRecoveryNotice } from "~/clients/backend-client.server";

export type QueueRecoveryNoticePresentation = {
    text: string,
    title: string,
}

export function presentQueueRecoveryNotice(
    notice: QueueRecoveryNotice,
): QueueRecoveryNoticePresentation {
    const count = Math.max(0, Math.trunc(notice.count));
    const articles = `${count} article${count === 1 ? "" : "s"}`;

    if (notice.phase === "prep") {
        return {
            text: "Prep fallback active",
            title: "Preparation is checking other providers for unresolved first segments.",
        };
    }

    return {
        text: `Health fallback · ${count} unresolved`,
        title: `Health checking is consulting fallback providers for ${articles}.`,
    };
}
