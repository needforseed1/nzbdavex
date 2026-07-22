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
    const firstSegments = `${count} first segment${count === 1 ? "" : "s"}`;

    if (notice.phase === "prep") {
        if (notice.state === "searching") return {
            text: "Prep fallback active",
            title: "Preparation is checking other providers for unresolved first segments.",
        };
        if (notice.state === "recovered") return {
            text: `Prep fallback · ${count} recovered`,
            title: `${firstSegments} recovered through provider fallback.`,
        };
    }

    if (notice.state === "searching") return {
        text: `Health fallback · ${count} unresolved`,
        title: `Health checking is consulting fallback providers for ${articles}.`,
    };
    if (notice.state === "recovered") return {
        text: `Health fallback · ${count} recovered`,
        title: `${articles} recovered through provider fallback.`,
    };
    if (notice.state === "missing") return {
        text: `Health · ${count} missing everywhere`,
        title: `${articles} missing from every eligible provider.`,
    };
    return {
        text: `Health · ${count} not verified`,
        title: `${articles} could not be verified because one or more providers were unavailable.`,
    };
}
