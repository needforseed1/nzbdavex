import { Button, Form, Card } from "react-bootstrap";
import styles from "./profiles.module.css";
import { type Dispatch, type ReactNode, type SetStateAction, useCallback, useMemo, useState } from "react";
import { MultiCheckboxInput } from "~/components/multi-checkbox-input/multi-checkbox-input";
import { randomHex } from "~/utils/random";

type ProfilesSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

type FallbackMode = "Off" | "Title" | "Broad";

interface Profile {
    Token: string;
    Name: string;
    IndexerIds: string[];
    EnabledAdapters?: string[] | null;
    MovieFallback?: FallbackMode;
    TvFallback?: FallbackMode;
    MovieFallbackMinResults?: number;
    TvFallbackMinResults?: number;
    QueryFallbackMinResults?: number;
}

interface ProfileConfig {
    Profiles: Profile[];
}

interface IndexerSummary {
    Id: string;
    Name: string;
    Enabled: boolean;
}

type AdapterKey = "json" | "addon" | "newznab";

const ADAPTERS: { key: AdapterKey; name: string; description: string; buildUrl: (origin: string, token: string) => string }[] = [
    {
        key: "json",
        name: "JSON Search API",
        description: "Vendor-neutral JSON results. Use from custom clients or scripts.",
        buildUrl: (origin, token) => `${origin}/api/search/${token}/lookup?type=movie&id=tt0111161`,
    },
    {
        key: "newznab",
        name: "Newznab",
        description: "Newznab-protocol meta-indexer endpoint. Add to Prowlarr / Sonarr / Radarr.",
        buildUrl: (origin, token) => `${origin}/adapters/newznab/${token}/api`,
    },
    {
        key: "addon",
        name: "Addon",
        description: "Manifest-based addon endpoint. Install the URL in a compatible client to query this Search Profile.",
        buildUrl: (origin, token) => `${origin}/adapters/addon/${token}/manifest.json`,
    },
];

const ALL_ADAPTER_KEYS: AdapterKey[] = ADAPTERS.map(a => a.key);

function parseProfileConfig(raw: string): ProfileConfig {
    try {
        const parsed = JSON.parse(raw || "{}");
        return {
            ...parsed,
            Profiles: (parsed.Profiles ?? []).map((profile: any) => ({
                ...profile,
                IndexerIds: profile.IndexerIds ?? [],
            })),
        };
    } catch {
        return { Profiles: [] };
    }
}

function isProfileConfigJsonValid(raw: string | undefined): boolean {
    try {
        const parsed = JSON.parse(raw || "");
        return parsed !== null && typeof parsed === "object"
            && (parsed.Profiles === undefined || (Array.isArray(parsed.Profiles)
                && parsed.Profiles.every((profile: any) => profile !== null
                    && typeof profile === "object"
                    && typeof profile.Token === "string"
                    && typeof profile.Name === "string"
                    && (profile.IndexerIds === undefined
                        || (Array.isArray(profile.IndexerIds)
                            && profile.IndexerIds.every((id: unknown) => typeof id === "string")))
                    && (profile.EnabledAdapters === undefined
                        || profile.EnabledAdapters === null
                        || (Array.isArray(profile.EnabledAdapters)
                            && profile.EnabledAdapters.every((adapter: unknown) => typeof adapter === "string"))))));
    } catch {
        return false;
    }
}

function parseIndexers(raw: string): IndexerSummary[] {
    try {
        const parsed = JSON.parse(raw || "{}");
        const list: IndexerSummary[] = parsed.Indexers ?? [];
        return list.filter(i => i.Enabled !== false && i.Id && i.Name?.trim());
    } catch {
        return [];
    }
}

function makeToken(): string {
    return randomHex(12);
}

function isAdapterEnabled(profile: Profile, key: AdapterKey): boolean {
    const list = profile.EnabledAdapters;
    // Missing/null is the legacy representation for "all". An explicit empty
    // list means the user intentionally disabled every adapter.
    if (list === null || list === undefined) return true;
    return list.some(x => x?.toLowerCase() === key);
}

export function ProfilesSettings({ config, setNewConfig }: ProfilesSettingsProps) {
    const profileConfig = useMemo(() => parseProfileConfig(config["profiles.instances"]), [config]);
    const profileConfigJsonValid = isProfileConfigJsonValid(config["profiles.instances"]);
    const availableIndexers = useMemo(() => parseIndexers(config["indexers.instances"]), [config]);
    const configuredBaseUrl = (config["general.base-url"] ?? "").trim().replace(/\/+$/, "");

    const update = useCallback((next: ProfileConfig) => {
        setNewConfig({ ...config, "profiles.instances": JSON.stringify({ ...profileConfig, ...next }) });
    }, [config, profileConfig, setNewConfig]);

    const add = useCallback(() => {
        update({
            Profiles: [
                ...profileConfig.Profiles,
                { Token: makeToken(), Name: "", IndexerIds: [], EnabledAdapters: [...ALL_ADAPTER_KEYS], MovieFallback: "Off", TvFallback: "Off", MovieFallbackMinResults: 3, TvFallbackMinResults: 3 }
            ]
        });
    }, [profileConfig, update]);

    const remove = useCallback((index: number) => {
        update({ Profiles: profileConfig.Profiles.filter((_, i) => i !== index) });
    }, [profileConfig, update]);

    const change = useCallback((index: number, patch: Partial<Profile>) => {
        update({
            Profiles: profileConfig.Profiles.map((x, i) =>
                i === index ? { ...x, ...patch } : x
            )
        });
    }, [profileConfig, update]);

    if (!profileConfigJsonValid) {
        return <div className={styles.container}>
            <p className={styles.alertMessage} role="alert">
                Search Profile settings contain invalid JSON. This section is locked to prevent overwriting it; restore a valid <code>profiles.instances</code> value before editing.
            </p>
        </div>;
    }

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Public access</div>
                </div>
                <Form.Group>
                    <Form.Label htmlFor="base-url-input">Public Base URL</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="url"
                        id="base-url-input"
                        aria-describedby="base-url-help"
                        placeholder="https://nzbdav.example.com"
                        value={config["general.base-url"] ?? ""}
                        isInvalid={!isOptionalAbsoluteHttpUrl(config["general.base-url"] ?? "")}
                        onChange={e => setNewConfig({ ...config, "general.base-url": e.target.value })} />
                    <Form.Text id="base-url-help" muted>
                        Used in Search Profile adapter URLs, playback and sharing links, and SAB STRM files.
                        Leave blank where request-origin inference is available; STRM imports require an explicit URL.
                    </Form.Text>
                </Form.Group>
            </div>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Search Profiles</div>
                    <Button variant="primary" size="sm" onClick={add}>Add</Button>
                </div>
                {profileConfig.Profiles.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No search profiles configured. Each profile exposes a token-scoped search API over its own indexer selection. Enable one or more output adapters (JSON / Newznab / Addon) to make it consumable by external clients.
                    </p>
                ) : (
                    profileConfig.Profiles.map((profile, index) => (
                        <ProfileForm
                            key={profile.Token}
                            profile={profile}
                            index={index}
                            availableIndexers={availableIndexers}
                            configuredBaseUrl={configuredBaseUrl}
                            onChange={change}
                            onRemove={remove}
                        />
                    ))
                )}
            </div>
        </div>
    );
}

const FALLBACK_DEFAULT_THRESHOLD = 3;

function effMode(explicit: FallbackMode | undefined, legacy: number | undefined, broadDefault: boolean): FallbackMode {
    if (explicit) return explicit;
    if (legacy && legacy > 0) return broadDefault ? "Broad" : "Title";
    return "Off";
}

function effThreshold(explicit: number | undefined, legacy: number | undefined): number {
    if (explicit && explicit > 0) return explicit;
    if (legacy && legacy > 0) return legacy;
    return FALLBACK_DEFAULT_THRESHOLD;
}

function fallbackHelp(mode: FallbackMode, allowBroad: boolean, indexerCount: number): string {
    if (mode === "Off") return "";
    const what = mode === "Broad"
        ? "Title + episode, then a whole-show query that also catches untagged specials."
        : allowBroad
            ? "Title + episode; wrong episodes are filtered out."
            : "Title search.";
    if (indexerCount <= 0) return what;
    const total = (mode === "Broad" ? 2 : 1) * indexerCount;
    const q = total === 1 ? "query" : "queries";
    const idxs = indexerCount === 1 ? "indexer" : "indexers";
    return `${what} Up to ${total} extra ${q} per search across this profile's ${indexerCount} ${idxs}.`;
}

interface FallbackControlProps {
    label: ReactNode;
    mode: FallbackMode;
    threshold: number;
    allowBroad: boolean;
    indexerCount: number;
    disabled: boolean;
    onModeChange: (mode: FallbackMode) => void;
    onThresholdChange: (n: number) => void;
}

function FallbackControl({ label, mode, threshold, allowBroad, indexerCount, disabled, onModeChange, onThresholdChange }: FallbackControlProps) {
    return (
        <Form.Group>
            <Form.Label>{label}</Form.Label>
            <Form.Select
                className={styles.input}
                value={mode}
                disabled={disabled}
                onChange={e => onModeChange(e.target.value as FallbackMode)}>
                <option value="Off">Off (exact ID match only)</option>
                <option value="Title">{allowBroad ? "Title + episode (precise)" : "Also search by title"}</option>
                {allowBroad && <option value="Broad">Title + episode, then whole show (broad)</option>}
            </Form.Select>
            {disabled ? (
                <p className={styles.hint}>Add indexers in the Indexers tab to use fallback.</p>
            ) : mode !== "Off" && (
                <>
                    <div className={styles.fallbackThresholdRow}>
                        <span className={styles.fallbackThresholdLabel}>when the ID search finds fewer than</span>
                        <Form.Control
                            type="number"
                            min={1}
                            step={1}
                            className={styles.fallbackThresholdInput}
                            value={threshold}
                            onChange={e => {
                                const n = parseInt(e.target.value, 10);
                                onThresholdChange(Number.isFinite(n) && n > 0 ? n : 1);
                            }} />
                        <span className={styles.fallbackThresholdLabel}>results</span>
                    </div>
                    <p className={styles.hint}>{fallbackHelp(mode, allowBroad, indexerCount)}</p>
                </>
            )}
        </Form.Group>
    );
}

interface ProfileFormProps {
    profile: Profile;
    index: number;
    availableIndexers: IndexerSummary[];
    configuredBaseUrl: string;
    onChange: (index: number, patch: Partial<Profile>) => void;
    onRemove: (index: number) => void;
}

function ProfileForm({ profile, index, availableIndexers, configuredBaseUrl, onChange, onRemove }: ProfileFormProps) {
    const origin = configuredBaseUrl || (typeof window === "undefined" ? "" : window.location.origin);
    const indexersCsv = profile.IndexerIds.join(", ");
    const availableIndexerKeys = new Set(availableIndexers.map(indexer => indexer.Id));
    const unavailableIndexers = profile.IndexerIds.filter(id => !availableIndexerKeys.has(id));

    const enabledKeys = useMemo(() => {
        return ADAPTERS.filter(a => isAdapterEnabled(profile, a.key)).map(a => a.key);
    }, [profile]);

    const setAdapterEnabled = useCallback((key: AdapterKey, enabled: boolean) => {
        const next = new Set<AdapterKey>(enabledKeys);
        if (enabled) next.add(key); else next.delete(key);
        const list = ALL_ADAPTER_KEYS.filter(k => next.has(k));
        onChange(index, { EnabledAdapters: list });
    }, [enabledKeys, index, onChange]);

    const movieMode = effMode(profile.MovieFallback, profile.QueryFallbackMinResults, false);
    const tvMode = effMode(profile.TvFallback, profile.QueryFallbackMinResults, true);
    const movieThreshold = effThreshold(profile.MovieFallbackMinResults, profile.QueryFallbackMinResults);
    const tvThreshold = effThreshold(profile.TvFallbackMinResults, profile.QueryFallbackMinResults);
    const indexerCount = profile.IndexerIds.length > 0 ? profile.IndexerIds.length : availableIndexers.length;
    const noIndexers = availableIndexers.length === 0;

    const writeFallback = useCallback((patch: Partial<Profile>) => {
        onChange(index, {
            MovieFallback: movieMode,
            TvFallback: tvMode,
            MovieFallbackMinResults: movieThreshold,
            TvFallbackMinResults: tvThreshold,
            QueryFallbackMinResults: undefined,
            ...patch,
        });
    }, [index, onChange, movieMode, tvMode, movieThreshold, tvThreshold]);

    return (
        <Card className={styles.instanceCard}>
            <button className={styles.closeButton} onClick={() => onRemove(index)} aria-label="Remove">×</button>
            <Card.Body>
                <Form.Group>
                    <Form.Label>Name</Form.Label>
                    <Form.Control
                        type="text"
                        className={styles.input}
                        placeholder="e.g. Movies"
                        value={profile.Name}
                        onChange={e => onChange(index, { Name: e.target.value })} />
                </Form.Group>
                <Form.Group>
                    <Form.Label>Indexers <span style={{ opacity: 0.6, fontWeight: 'normal' }}>(leave all unchecked to use every enabled indexer)</span></Form.Label>
                    {availableIndexers.length === 0 && profile.IndexerIds.length === 0 ? (
                        <p className={styles.hint}>No indexers configured yet. Add some under the Indexers tab.</p>
                    ) : (
                        <>
                            <MultiCheckboxInput
                                options={availableIndexers.map(indexer => ({ value: indexer.Id, label: indexer.Name }))}
                                value={indexersCsv}
                                onChange={v => onChange(index, {
                                    IndexerIds: v.split(",").map(s => s.trim()).filter(Boolean)
                                })}
                            />
                            {unavailableIndexers.length > 0 && (
                                <p className={styles.hint}>
                                    Unavailable selections are disabled, deleted, or renamed indexers. Uncheck them to remove the stale selection.
                                </p>
                            )}
                        </>
                    )}
                </Form.Group>
                <Form.Group>
                    <Form.Label>Output adapters <span style={{ opacity: 0.6, fontWeight: 'normal' }}>(toggle the protocols this profile exposes)</span></Form.Label>
                    <div className={styles.adapterGroup}>
                        {ADAPTERS.map(adapter => (
                            <AdapterRow
                                key={adapter.key}
                                token={profile.Token}
                                origin={origin}
                                adapter={adapter}
                                enabled={isAdapterEnabled(profile, adapter.key)}
                                onToggle={enabled => setAdapterEnabled(adapter.key, enabled)}
                            />
                        ))}
                    </div>
                </Form.Group>
                <Form.Group>
                    <Form.Label>Query fallback <span style={{ opacity: 0.6, fontWeight: 'normal' }}>(extra title searches when an ID lookup comes up short)</span></Form.Label>
                    <p className={styles.hint}>These fire extra queries to this profile's indexers and spend their hit and rate limits, which you set per indexer in the Indexers tab.</p>
                </Form.Group>
                <FallbackControl
                    label="Movies"
                    mode={movieMode}
                    threshold={movieThreshold}
                    allowBroad={false}
                    indexerCount={indexerCount}
                    disabled={noIndexers}
                    onModeChange={m => writeFallback({ MovieFallback: m })}
                    onThresholdChange={n => writeFallback({ MovieFallbackMinResults: n })} />
                <FallbackControl
                    label="TV"
                    mode={tvMode}
                    threshold={tvThreshold}
                    allowBroad={true}
                    indexerCount={indexerCount}
                    disabled={noIndexers}
                    onModeChange={m => writeFallback({ TvFallback: m })}
                    onThresholdChange={n => writeFallback({ TvFallbackMinResults: n })} />
            </Card.Body>
        </Card>
    );
}

interface AdapterRowProps {
    token: string;
    origin: string;
    adapter: typeof ADAPTERS[number];
    enabled: boolean;
    onToggle: (enabled: boolean) => void;
}

function AdapterRow({ token, origin, adapter, enabled, onToggle }: AdapterRowProps) {
    const [copied, setCopied] = useState(false);
    const url = useMemo(() => adapter.buildUrl(origin, token), [origin, token, adapter]);

    const onCopy = useCallback(async () => {
        try {
            await navigator.clipboard.writeText(url);
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
        } catch {}
    }, [url]);

    return (
        <div className={`${styles.adapterRow} ${enabled ? "" : styles.disabled}`}>
            <div className={styles.adapterHeader}>
                <div className={styles.adapterTitle}>
                    <span className={styles.adapterName}>{adapter.name}</span>
                    <span className={styles.adapterDescription}>{adapter.description}</span>
                </div>
                <Form.Check
                    type="switch"
                    id={`adapter-${token}-${adapter.key}`}
                    checked={enabled}
                    onChange={e => onToggle(e.target.checked)} />
            </div>
            {enabled && (
                <div className={styles.urlBox}>
                    <Form.Control
                        className={styles.urlInput}
                        type="text"
                        readOnly
                        value={url}
                        onFocus={e => e.currentTarget.select()}
                    />
                    <Button variant={copied ? "success" : "secondary"} size="sm" onClick={onCopy}>
                        {copied ? "Copied" : "Copy"}
                    </Button>
                </div>
            )}
        </div>
    );
}

export function isProfilesSettingsValid(newConfig: Record<string, string>) {
    try {
        if (!isProfileConfigJsonValid(newConfig["profiles.instances"])) return false;
        const c = parseProfileConfig(newConfig["profiles.instances"]);
        const availableIndexers = new Set(parseIndexers(newConfig["indexers.instances"]).map(indexer => indexer.Id));
        const allowedAdapters = new Set(ALL_ADAPTER_KEYS);
        const tokens = new Set<string>();
        for (const p of c.Profiles) {
            if (!p.Token?.trim()) return false;
            if (tokens.has(p.Token)) return false;
            tokens.add(p.Token);
            if (!p.Name?.trim()) return false;
            if (!Array.isArray(p.IndexerIds)
                || p.IndexerIds.some(id => !availableIndexers.has(id))) return false;
            if (p.EnabledAdapters !== null && p.EnabledAdapters !== undefined
                && (!Array.isArray(p.EnabledAdapters)
                    || p.EnabledAdapters.some(adapter => !allowedAdapters.has(adapter.toLowerCase() as AdapterKey)))) return false;
            if (p.MovieFallback !== undefined && !["Off", "Title"].includes(p.MovieFallback)) return false;
            if (p.TvFallback !== undefined && !["Off", "Title", "Broad"].includes(p.TvFallback)) return false;
            for (const threshold of [p.MovieFallbackMinResults, p.TvFallbackMinResults, p.QueryFallbackMinResults]) {
                if (threshold !== undefined
                    && (!Number.isInteger(threshold) || threshold < 1 || threshold > 5000)) return false;
            }
        }
        const baseUrl = (newConfig["general.base-url"] ?? "").trim();
        if (baseUrl !== "") {
            const url = new URL(baseUrl);
            if ((url.protocol !== "http:" && url.protocol !== "https:") || !url.host) return false;
        }
        return true;
    } catch {
        return false;
    }
}

function isOptionalAbsoluteHttpUrl(value: string): boolean {
    if (!value.trim()) return true;
    try {
        const url = new URL(value.trim());
        return (url.protocol === "http:" || url.protocol === "https:") && url.host !== "";
    } catch {
        return false;
    }
}
