import type { Route } from "./+types/route";
import { backendClient, type ConfigItem } from "~/clients/backend-client.server";

export async function action({ request }: Route.ActionArgs) {
    // get the ConfigItems to update
    const formData = await request.formData();
    const configJson = formData.get("config")!.toString();
    const config = JSON.parse(configJson);
    const configItems: ConfigItem[] = [];
    for (const [key, value] of Object.entries<string>(config)) {
        configItems.push({
            configName: key,
            configValue: value
        })
    }

    // update the config items
    const status = await backendClient.updateConfig(configItems);
    if (!status) {
        return Response.json(
            { status: false, error: "The backend did not accept the settings update." },
            { status: 502 },
        );
    }
    return Response.json({ status: true })
}
