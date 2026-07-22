export type ProviderConnectionIdentity = {
    host: string;
    port: string | number;
    useSsl: boolean;
    user: string;
    pass: string;
};

export function getProviderConnectionSignature(details: ProviderConnectionIdentity): string {
    return JSON.stringify([
        details.host,
        String(details.port),
        details.useSsl,
        details.user,
        details.pass,
    ]);
}

export function requiresProviderConnectionTest(
    saved: ProviderConnectionIdentity | null,
    current: ProviderConnectionIdentity,
): boolean {
    return saved === null
        || getProviderConnectionSignature(saved) !== getProviderConnectionSignature(current);
}
