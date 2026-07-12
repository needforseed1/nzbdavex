function secureRandomBytes(length: number): Uint8Array {
    const cryptoApi = globalThis.crypto;
    if (!cryptoApi || typeof cryptoApi.getRandomValues !== "function") {
        throw new Error("Secure random number generation is not available in this browser.");
    }
    return cryptoApi.getRandomValues(new Uint8Array(length));
}

export function randomUuid(): string {
    const cryptoApi = globalThis.crypto;
    if (cryptoApi && typeof cryptoApi.randomUUID === "function") {
        return cryptoApi.randomUUID();
    }

    const bytes = secureRandomBytes(16);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0"));
    return `${hex.slice(0, 4).join("")}-${hex.slice(4, 6).join("")}-${hex.slice(6, 8).join("")}-${hex.slice(8, 10).join("")}-${hex.slice(10).join("")}`;
}

export function randomHex(byteLength: number): string {
    return Array.from(secureRandomBytes(byteLength), byte => byte.toString(16).padStart(2, "0")).join("");
}
