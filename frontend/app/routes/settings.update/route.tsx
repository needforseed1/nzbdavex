import type { Route } from "./+types/route";
import { backendClient, type ConfigItem } from "~/clients/backend-client.server";

export async function action({ request }: Route.ActionArgs) {
    // get the ConfigItems to update
    const formData = await request.formData();
    const configJson = formData.get("config")!.toString();
    const resetKeys = formData.getAll("reset").map(x => x.toString());
    const revision = Number(formData.get("revision"));
    if (!Number.isSafeInteger(revision) || revision < 0)
        return Response.json({ status: false, error: "A valid settings revision is required." }, { status: 400 });
    const config = JSON.parse(configJson);
    const configItems: ConfigItem[] = [];
    for (const [key, value] of Object.entries<string>(config)) {
        configItems.push({
            configName: key,
            configValue: value
        })
    }

    // update the config items
    const result = await backendClient.updateConfig(configItems, revision, resetKeys);
    if (result.conflict) return Response.json(result, { status: 409 });
    if (!result.status) {
        return Response.json(
            { status: false, error: result.error || "The backend did not accept the settings update." },
            { status: 502 },
        );
    }
    return Response.json(result)
}
