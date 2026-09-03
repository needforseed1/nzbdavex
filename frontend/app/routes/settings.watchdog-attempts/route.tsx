import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const limit = Number(url.searchParams.get("limit") ?? "200");
    const entries = await backendClient.getWatchdogEntries(limit);
    return { entries };
}

export async function action({ request }: Route.ActionArgs) {
    if (request.method !== "POST") {
        return new Response("Method not allowed", { status: 405, headers: { Allow: "POST" } });
    }
    const form = await request.formData();
    const intent = String(form.get("intent") ?? "clear");
    if (intent === "resolve-retry") {
        const eventId = Number(form.get("eventId"));
        if (!Number.isSafeInteger(eventId) || eventId <= 0)
            return Response.json({ error: "Invalid event" }, { status: 400 });
        try {
            return { matches: await backendClient.resolveWatchdogRetry(eventId) };
        } catch (error: any) {
            return Response.json({ error: error?.message ?? "Could not check saved NZBs" }, { status: 502 });
        }
    }
    if (intent === "retry") {
        const eventId = Number(form.get("eventId"));
        const blobId = String(form.get("blobId") ?? "");
        if (!Number.isSafeInteger(eventId) || eventId <= 0 || !blobId)
            return Response.json({ error: "Invalid retry selection" }, { status: 400 });
        try {
            return { retry: await backendClient.retryWatchdogNzb(eventId, blobId) };
        } catch (error: any) {
            return Response.json({ error: error?.message ?? "Could not retry saved NZB" }, { status: 502 });
        }
    }
    if (intent !== "clear") return new Response("Unknown action", { status: 400 });
    const deleted = await backendClient.clearWatchdogEntries();
    return { deleted };
}
