import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const id = url.searchParams.get("id");
    // One route serves both the polling list and the lazily-expanded session
    // detail, so the page never needs a second api-key-bearing entry point.
    if (id) return { detail: await backendClient.getPlaybackSessionDetail(id) };
    const limit = Number(url.searchParams.get("limit") ?? "500");
    // Returned flat, not wrapped: the page reads plays, sampledSessions and
    // truncated off the top level of this response.
    return await backendClient.getPlaybackSessions(limit);
}

export async function action({ request }: Route.ActionArgs) {
    if (request.method !== "POST" && request.method !== "DELETE") {
        return new Response("Method not allowed", { status: 405 });
    }
    const deleted = await backendClient.clearPlaybackSessions();
    return { deleted };
}
