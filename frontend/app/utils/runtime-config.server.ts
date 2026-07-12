function requiredEnvironmentValue(name: string): string {
    const value = process.env[name];
    if (!value?.trim()) throw new Error(`${name} must be set to a non-blank value.`);
    return value;
}

function backendUrl(): string {
    const raw = requiredEnvironmentValue("BACKEND_URL").trim();
    let parsed: URL;
    try {
        parsed = new URL(raw);
    } catch {
        throw new Error("BACKEND_URL must be an absolute HTTP(S) URL.");
    }
    if (!['http:', 'https:'].includes(parsed.protocol)
        || !parsed.host
        || parsed.username
        || parsed.password
        || (parsed.pathname !== '/' && parsed.pathname !== '')
        || parsed.search
        || parsed.hash) {
        throw new Error("BACKEND_URL must be an HTTP(S) origin without credentials, a path, query, or fragment.");
    }
    return parsed.origin;
}

export const BACKEND_URL = backendUrl();
export const FRONTEND_BACKEND_API_KEY = requiredEnvironmentValue("FRONTEND_BACKEND_API_KEY");
