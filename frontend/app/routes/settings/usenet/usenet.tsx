import styles from "./usenet.module.css"
import { type Dispatch, type SetStateAction, type ReactNode, type CSSProperties, useState, useCallback, useEffect, useMemo, useRef } from "react";
import { Button } from "react-bootstrap";
import { receiveMessage } from "~/utils/websocket-util";
import {
    DndContext,
    type DragEndEvent,
    closestCenter,
    KeyboardSensor,
    PointerSensor,
    useSensor,
    useSensors,
} from "@dnd-kit/core";
import {
    SortableContext,
    arrayMove,
    rectSortingStrategy,
    sortableKeyboardCoordinates,
    useSortable,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";

const usenetConnectionsTopic = {'cxs': 'state'};
const benchmarkTopic = {'bench': 'state'};
const USAGE_POLL_INTERVAL_MS = 10_000;

// Mirrors the camelCase JSON the backend benchmark endpoint + websocket emit.
type BenchmarkLatency = { minMs: number; avgMs: number; samples: number };
type BenchmarkSweepPoint = { connections: number; mbPerSec: number };
type BenchmarkPipeliningPoint = { depth: number; mbPerSec: number };
type BenchmarkPipelining = {
    testedAtConnections: number;
    baselineMbPerSec: number;
    tested: BenchmarkPipeliningPoint[];
    recommendEnabled: boolean;
    recommendedDepth: number;
};
type BenchmarkStartupPipeliningPoint = { depth: number; firstMs: number; readyMs: number };
type BenchmarkStartup = {
    segments: number;
    nonPipelinedConnections: number;
    nonPipelinedFirstMs: number;
    nonPipelinedReadyMs: number;
    pipelined: BenchmarkStartupPipeliningPoint[];
    recommendPlaybackPipelining: boolean;
    recommendedDepth: number;
};
type BenchmarkHealthPipeliningPoint = {
    depth: number;
    completedRounds: number;
    requiredRounds: number;
    requests: number;
    responses: number;
    found: number;
    missing: number;
    timeouts: number;
    errors: number;
    averageMs: number;
    statsPerSecond: number;
    reliable: boolean;
    failure?: string | null;
};
type BenchmarkHealthPipelining = {
    articlesPerRound: number;
    rounds: number;
    knownMissingArticles: number;
    tested: BenchmarkHealthPipeliningPoint[];
    reliable: boolean;
    recommendedDepth: number;
};
type BenchmarkResult = {
    latency?: BenchmarkLatency | null;
    throughputTested: boolean;
    pipeliningOnly: boolean;
    startupOnly: boolean;
    healthOnly: boolean;
    sweep: BenchmarkSweepPoint[];
    recommendedConnections?: number | null;
    providerConnectionCap?: number | null;
    pipelining?: BenchmarkPipelining | null;
    startup?: BenchmarkStartup | null;
    healthPipelining?: BenchmarkHealthPipelining | null;
    dataUsedBytes: number;
    warnings: string[];
};
type BenchmarkProgress = {
    phase: string;
    status: string;
    percent: number;
    currentConnections?: number | null;
    dataUsedBytes: number;
    sweep: BenchmarkSweepPoint[];
};
type BenchmarkIntensity = "quick" | "thorough";
type BenchmarkMode = "speed" | "pipelining" | "startup" | "health";

type UsenetSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

enum ProviderType {
    Disabled = 0,
    Pooled = 1,
    BackupAndStats = 2,
    BackupOnly = 3,
    HealthChecksOnly = 4,
}

type ConnectionDetails = {
    Type: ProviderType;
    Host: string;
    Port: number;
    UseSsl: boolean;
    User: string;
    Pass: string;
    MaxConnections: number;
    Priority?: number;
    PipeliningDepth?: number | null;
    HealthPipeliningDepth?: number | null;
    PrepOnly?: boolean;
    PrepSpreadEnabled?: boolean;
    // Optional user-set label. Shown in the UI in place of Host when present;
    // Host stays the real NNTP target.
    Nickname?: string;
    PreviousType?: ProviderType;
    // null/0 = uncapped. Stored as bytes; the modal lets the user type a
    // friendlier MB/GB/TB value that gets converted on save.
    ByteLimit?: number | null;
    // Counter adjustment, used for "initial used" on a freshly added block
    // and zeroed on reset. Bytes.
    BytesUsedOffset?: number;
    // unix-ms cutoff. Hourly rows older than this are excluded from the live
    // usage gauge. A reset bumps this to Date.now().
    BytesUsedResetAt?: number;
};

// camelCase matches the JSON wire format — ASP.NET Core MVC defaults to
// camelCase serialization, so we mirror that here instead of fighting it.
type ProviderUsage = {
    index: number;
    host: string;
    nickname?: string | null;
    bytesUsed: number;
    byteLimit: number | null;
    overLimit: boolean;
    bytesPerDay: number;
    daysRemaining: number | null;
};

function formatDaysRemaining(days: number): string {
    // Friendlier than "0.3 days" or "847 days" — round to the unit that's
    // actually useful at this horizon.
    if (days < 1) {
        const hours = Math.max(1, Math.round(days * 24));
        return `~${hours}h left at this pace`;
    }
    if (days < 60) return `~${Math.round(days)} days left at this pace`;
    const months = days / 30;
    if (months < 24) return `~${Math.round(months)} months left at this pace`;
    return `~${Math.round(months / 12)} years left at this pace`;
}

const BYTE_UNITS = [
    { label: "MB", multiplier: 1_000_000 },
    { label: "GB", multiplier: 1_000_000_000 },
    { label: "TB", multiplier: 1_000_000_000_000 },
] as const;
type ByteUnitLabel = typeof BYTE_UNITS[number]["label"];

function bytesToValueAndUnit(bytes: number | null | undefined): { value: string; unit: ByteUnitLabel } {
    if (!bytes || bytes <= 0) return { value: "", unit: "GB" };
    // Pick the largest unit that keeps the number readable (>= 1).
    const choice = [...BYTE_UNITS].reverse().find(u => bytes >= u.multiplier) ?? BYTE_UNITS[1];
    const v = bytes / choice.multiplier;
    // Trim trailing zeros so "500" doesn't display as "500.000".
    return { value: Number(v.toFixed(3)).toString(), unit: choice.label };
}

function valueAndUnitToBytes(value: string, unit: ByteUnitLabel): number | null {
    const trimmed = value.trim();
    if (trimmed === "") return null;
    const n = Number(trimmed);
    if (!isFinite(n) || n <= 0) return null;
    const u = BYTE_UNITS.find(x => x.label === unit) ?? BYTE_UNITS[1];
    return Math.round(n * u.multiplier);
}

function formatBytes(bytes: number): string {
    if (!isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB", "PB"];
    let i = 0;
    let v = bytes;
    while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
    return v >= 100 ? `${v.toFixed(0)} ${units[i]}` : `${v.toFixed(1)} ${units[i]}`;
}

type ConnectionCounts = {
    live: number;
    active: number;
    max: number;
}

type UsenetProviderConfig = {
    Providers: ConnectionDetails[];
};

const PROVIDER_TYPE_LABELS: Record<ProviderType, string> = {
    [ProviderType.Disabled]: "Disabled",
    [ProviderType.Pooled]: "Pool Connections",
    [ProviderType.BackupAndStats]: "Backup & Health Checks",
    [ProviderType.BackupOnly]: "Backup Only",
    [ProviderType.HealthChecksOnly]: "Health Checks Only",
};

function parseProviderConfig(jsonString: string): UsenetProviderConfig {
    try {
        if (!jsonString || jsonString.trim() === "") {
            return { Providers: [] };
        }
        return JSON.parse(jsonString);
    } catch {
        return { Providers: [] };
    }
}

function serializeProviderConfig(config: UsenetProviderConfig): string {
    return JSON.stringify(config);
}

function providerKey(p: ConnectionDetails): string {
    return `${p.Host}::${p.Port}::${p.User}`;
}

type DragBits = {
    setNodeRef: (node: HTMLElement | null) => void;
    setActivatorNodeRef: (node: HTMLElement | null) => void;
    attributes: any;
    listeners: any;
    style: CSSProperties;
    isDragging: boolean;
};

function SortableItem({ id, disabled, children }: { id: string; disabled: boolean; children: (drag: DragBits) => ReactNode }) {
    const { setNodeRef, setActivatorNodeRef, attributes, listeners, transform, transition, isDragging } = useSortable({ id, disabled });
    const style: CSSProperties = {
        transform: CSS.Transform.toString(transform),
        transition,
        opacity: isDragging ? 0.6 : 1,
        zIndex: isDragging ? 2 : undefined,
    };
    return <>{children({ setNodeRef, setActivatorNodeRef, attributes, listeners, style, isDragging })}</>;
}

export function UsenetSettings({ config, setNewConfig }: UsenetSettingsProps) {
    // state
    const [showModal, setShowModal] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);
    const [connections, setConnections] = useState<{[index: number]: ConnectionCounts}>({});
    const [usage, setUsage] = useState<{[index: number]: ProviderUsage}>({});
    const providerConfig = useMemo(() => parseProviderConfig(config["usenet.providers"]), [config]);
    const cascadeEnabled = config["usenet.cascade.enabled"] === "true";

    // handlers
    const handleAddProvider = useCallback(() => {
        setEditingIndex(null);
        setShowModal(true);
    }, []);

    const handleEditProvider = useCallback((index: number) => {
        setEditingIndex(index);
        setShowModal(true);
    }, []);

    const handleDeleteProvider = useCallback((index: number) => {
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.filter((_, i) => i !== index);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleToggleProvider = useCallback((index: number) => {
        const current = providerConfig.Providers[index];
        if (!current) return;
        const isDisabled = current.Type === ProviderType.Disabled;
        const updated: ConnectionDetails = isDisabled
            ? { ...current, Type: current.PreviousType ?? ProviderType.Pooled, PreviousType: undefined }
            : { ...current, Type: ProviderType.Disabled, PreviousType: current.Type };
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.map((p, i) => i === index ? updated : p);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleResetUsage = useCallback((index: number) => {
        const current = providerConfig.Providers[index];
        if (!current) return;
        const label = current.Nickname?.trim() || current.Host;
        if (!confirm(`Reset bytes-used counter for "${label}" to zero?\n\nThis only rewinds the gauge for this provider's data cap. Historical metrics and graphs are untouched. Takes effect after you save settings.`)) return;
        const updated: ConnectionDetails = {
            ...current,
            BytesUsedOffset: 0,
            BytesUsedResetAt: Date.now(),
        };
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.map((p, i) => i === index ? updated : p);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSaveProvider = useCallback((provider: ConnectionDetails) => {
        const providers = [...providerConfig.Providers];
        if (editingIndex !== null) {
            providers[editingIndex] = provider;
        } else {
            providers.push({ ...provider, Priority: providers.length });
        }
        setNewConfig(prev => ({
            ...prev,
            "usenet.providers": serializeProviderConfig({ ...providerConfig, Providers: providers }),
        }));
        handleCloseModal();
    }, [providerConfig, editingIndex, setNewConfig, handleCloseModal]);

    const handleApplyPlaybackPipelining = useCallback((enabled: boolean) => {
        setNewConfig(prev => ({
            ...prev,
            "usenet.pipelining.playback.enabled": enabled ? "true" : "false",
        }));
    }, [setNewConfig]);

    const handleApplyHealthPipelining = useCallback((enabled: boolean) => {
        setNewConfig(prev => ({
            ...prev,
            "usenet.pipelining.health.enabled": enabled ? "true" : "false",
        }));
    }, [setNewConfig]);

    const handleReorder = useCallback((from: number, to: number) => {
        if (from === to) return;
        const providers = arrayMove(providerConfig.Providers, from, to)
            .map((p, i) => ({ ...p, Priority: i }));
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig({ ...providerConfig, Providers: providers }) });
    }, [config, providerConfig, setNewConfig]);

    const sensors = useSensors(
        useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
        useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
    );

    const handleDragEnd = useCallback((event: DragEndEvent) => {
        const { active, over } = event;
        if (!over || active.id === over.id) return;
        const ids = providerConfig.Providers.map(providerKey);
        const from = ids.indexOf(String(active.id));
        const to = ids.indexOf(String(over.id));
        if (from !== -1 && to !== -1) handleReorder(from, to);
    }, [providerConfig, handleReorder]);

    const handleConnectionsMessage = useCallback((message: string) => {
        const parts = (message || "0|0|0|0|1|0").split("|");
        const [index, live, idle, _0, _1, _2] = parts.map((x: any) => Number(x));
        if (showModal) return;
        if (index >= providerConfig.Providers.length) return;
        setConnections(prev => ({...prev, [index]: {
            active: live - idle,
            live: live,
            max: providerConfig.Providers[index]?.MaxConnections || 1
        }}));
    }, [setConnections]);

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => handleConnectionsMessage(message));
            ws.onopen = () => ws.send(JSON.stringify(usenetConnectionsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            !disposed && setTimeout(() => connect(), 1000);
            setConnections({});
        }
        return connect();
    }, [setConnections, handleConnectionsMessage]);

    // Poll provider usage. Backend computes "bytes since reset + offset" from
    // the persisted hourly rollup plus the in-memory tracker; cheap enough to
    // hit on a 10s tick. We skip while the edit modal is open since the user
    // may be mid-edit and we don't want the card behind the modal flickering.
    useEffect(() => {
        let disposed = false;
        async function fetchUsage() {
            try {
                const response = await fetch('/api/get-provider-usage');
                if (!response.ok || disposed) return;
                const data: { providers?: ProviderUsage[] } = await response.json();
                if (disposed || !data.providers) return;
                const next: {[index: number]: ProviderUsage} = {};
                for (const p of data.providers) next[p.index] = p;
                setUsage(next);
            } catch {
                // network blips are fine — next tick retries.
            }
        }
        fetchUsage();
        if (showModal) return () => { disposed = true; };
        const id = setInterval(fetchUsage, USAGE_POLL_INTERVAL_MS);
        return () => { disposed = true; clearInterval(id); };
    }, [showModal, providerConfig.Providers.length]);

    // view
    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Usenet Providers</div>
                    <Button variant="primary" size="sm" onClick={handleAddProvider}>
                        Add
                    </Button>
                </div>
                {providerConfig.Providers.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No Usenet providers configured.
                        Click on the "Add" button to get started.
                    </p>
                ) : (
                    <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
                    <SortableContext items={providerConfig.Providers.map(providerKey)} strategy={rectSortingStrategy}>
                    <div className={styles["providers-grid"]}>
                        {providerConfig.Providers.map((provider, index) => {
                            const isDisabled = provider.Type === ProviderType.Disabled;
                            return (
                            <SortableItem key={providerKey(provider)} id={providerKey(provider)} disabled={!cascadeEnabled}>
                            {({ setNodeRef, setActivatorNodeRef, attributes, listeners, style, isDragging }) => (
                            <div ref={setNodeRef} style={style} className={`${styles["provider-card"]} ${isDisabled ? styles["provider-card-disabled"] : ""}`}>
                                <div className={styles["provider-card-inner"]}>
                                    <div className={styles["provider-header"]}>
                                        <div className={styles["provider-header-content"]}>
                                            <div className={styles["provider-host"]}>
                                                {cascadeEnabled && !isDisabled && (
                                                    <span style={{ display: "inline-block", marginRight: 8, padding: "2px 8px", fontSize: 9, fontWeight: 600, letterSpacing: "0.08em", color: "var(--text-muted)", background: "var(--bg-surface-2)", border: "1px solid var(--border-subtle)", borderRadius: 6, verticalAlign: "middle" }}>
                                                        #{index + 1}
                                                    </span>
                                                )}
                                                {provider.Nickname?.trim() || provider.Host}
                                                {isDisabled && <span className={styles["provider-disabled-badge"]}>Disabled</span>}
                                                {provider.PrepOnly && !isDisabled && <span className={styles["provider-prep-badge"]}>Prep only</span>}
                                                {provider.Type === ProviderType.HealthChecksOnly && <span className={styles["provider-prep-badge"]}>STAT only</span>}
                                                {provider.Type === ProviderType.Pooled && provider.PrepSpreadEnabled === false && !isDisabled && <span className={styles["provider-prep-badge"]}>No prep spread</span>}
                                            </div>
                                            {provider.Nickname?.trim() && (
                                                <div className={styles["provider-host-secondary"]}>
                                                    {provider.Host}
                                                </div>
                                            )}
                                            <div className={styles["provider-port"]}>
                                                Port {provider.Port}
                                            </div>
                                        </div>
                                        <div className={styles["provider-header-actions"]}>
                                            {cascadeEnabled && (
                                                <button
                                                    type="button"
                                                    ref={setActivatorNodeRef}
                                                    className={styles["header-action-button"]}
                                                    style={{ cursor: isDragging ? "grabbing" : "grab", touchAction: "none" }}
                                                    title="Drag to reorder"
                                                    aria-label="Drag to reorder"
                                                    {...attributes}
                                                    {...listeners}
                                                >
                                                    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                                                        <circle cx="9" cy="5" r="1.6" /><circle cx="15" cy="5" r="1.6" />
                                                        <circle cx="9" cy="12" r="1.6" /><circle cx="15" cy="12" r="1.6" />
                                                        <circle cx="9" cy="19" r="1.6" /><circle cx="15" cy="19" r="1.6" />
                                                    </svg>
                                                </button>
                                            )}
                                            <button
                                                className={`${styles["header-action-button"]} ${styles["toggle"]} ${isDisabled ? styles["toggle-off"] : styles["toggle-on"]}`}
                                                onClick={() => handleToggleProvider(index)}
                                                title={isDisabled ? "Enable Provider" : "Disable Provider"}
                                                aria-pressed={!isDisabled}
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                                    <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                                                    <line x1="12" y1="2" x2="12" y2="12" />
                                                </svg>
                                            </button>
                                            <button
                                                className={styles["header-action-button"]}
                                                onClick={() => handleEditProvider(index)}
                                                title="Edit Provider"
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                    <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                                                    <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                                                </svg>
                                            </button>
                                            <button
                                                className={`${styles["header-action-button"]} ${styles["delete"]}`}
                                                onClick={() => handleDeleteProvider(index)}
                                                title="Delete Provider"
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                    <polyline points="3 6 5 6 21 6" />
                                                    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                                                </svg>
                                            </button>
                                        </div>
                                    </div>

                                    <div className={styles["provider-details"]}>
                                        <div className={styles["provider-detail-row"]}>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
                                                        <circle cx="12" cy="7" r="4" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Username</span>
                                                    <span className={styles["provider-detail-value"]}>{provider.User}</span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Connections</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {connections[index]
                                                            ? `${connections[index].live} / ${provider.MaxConnections} max`
                                                            : `${provider.MaxConnections} max`}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    {provider.UseSsl ? (
                                                        // Closed lock icon
                                                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                            <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                                            <path d="M7 11V7a5 5 0 0 1 10 0v4" />
                                                            <circle cx="12" cy="16" r="1" fill="currentColor" />
                                                        </svg>
                                                    ) : (
                                                        // Open lock icon
                                                        <svg width="13" height="13" viewBox="0 -2 24 26" fill="none" stroke="currentColor" strokeWidth="2">
                                                            <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                                            <path d="M7 11V4a5 5 0 0 1 9.9 1" />
                                                            <circle cx="12" cy="16" r="1" fill="currentColor" />
                                                        </svg>
                                                    )}
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Security</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {provider.UseSsl ? "SSL Enabled" : "No SSL"}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.3">
                                                        <text x="12" y="9" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">1</text>
                                                        <text x="6" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">2</text>
                                                        <text x="18" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">3</text>
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Behavior</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {PROVIDER_TYPE_LABELS[provider.Type]}
                                                    </span>
                                                </div>
                                            </div>

                                        </div>

                                        <UsageRow
                                            provider={provider}
                                            usage={usage[index]}
                                            onReset={() => handleResetUsage(index)}
                                        />
                                    </div>
                                </div>
                            </div>
                            )}
                            </SortableItem>
                            );
                        })}
                    </div>
                    </SortableContext>
                    </DndContext>
                )}
            </div>

            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Cascade (Optional)</div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="cascade-enabled"
                            className={`${styles["form-checkbox"]} toggle-switch`}
                            checked={cascadeEnabled}
                            onChange={(e) => {
                                const enabling = e.target.checked;
                                const needsSeed = enabling && providerConfig.Providers.every(p => !p.Priority);
                                const providers = needsSeed
                                    ? providerConfig.Providers.map((p, i) => ({ ...p, Priority: i }))
                                    : providerConfig.Providers;
                                setNewConfig({
                                    ...config,
                                    "usenet.cascade.enabled": enabling ? "true" : "false",
                                    "usenet.providers": serializeProviderConfig({ ...providerConfig, Providers: providers }),
                                });
                            }}
                        />
                        <label htmlFor="cascade-enabled" className={styles["form-checkbox-label"]}>
                            Enable cascade routing
                        </label>
                    </div>
                    <div className={styles["form-hint"]}>
                        Sets the order your providers are used. Drag the cards to arrange them. While this is off, all
                        providers are used together.
                    </div>
                </div>
            </div>

            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Queue / Prep Routing</div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="prep-spread-enabled"
                            className={`${styles["form-checkbox"]} toggle-switch`}
                            checked={config["usenet.prep-spread.enabled"] === "true"}
                            onChange={(e) => setNewConfig({
                                ...config,
                                "usenet.prep-spread.enabled": e.target.checked ? "true" : "false",
                            })}
                        />
                        <label htmlFor="prep-spread-enabled" className={styles["form-checkbox-label"]}>
                            Spread queue/prep across pooled providers
                        </label>
                    </div>
                    <div className={styles["form-hint"]}>
                        Queue imports and preflight will fan out across eligible Pool Connections providers by open capacity.
                        Health Checks Only providers can issue STAT requests but are blocked from HEAD, BODY and ARTICLE traffic.
                        Playback keeps fastest-provider routing across download-capable providers.
                    </div>
                </div>
            </div>

            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>NNTP Pipelining (Experimental)</div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="health-pipelining-enabled"
                            className={`${styles["form-checkbox"]} toggle-switch`}
                            checked={config["usenet.pipelining.health.enabled"] !== "false"}
                            onChange={(e) => setNewConfig({
                                ...config,
                                "usenet.pipelining.health.enabled": e.target.checked ? "true" : "false",
                            })}
                        />
                        <label htmlFor="health-pipelining-enabled" className={styles["form-checkbox-label"]}>
                            Enable health-check STAT pipelining
                        </label>
                    </div>
                    <div className={styles["form-hint"]}>
                        Uses pipelined NNTP STAT checks for SAB article health checks. STAT responses are tiny, so this is the safest pipelining mode.
                    </div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <div className={styles["form-checkbox-wrapper"]}>
                        <input
                            type="checkbox"
                            id="playback-pipelining-enabled"
                            className={`${styles["form-checkbox"]} toggle-switch`}
                            checked={config["usenet.pipelining.playback.enabled"] === "true"}
                            onChange={(e) => setNewConfig({
                                ...config,
                                "usenet.pipelining.playback.enabled": e.target.checked ? "true" : "false",
                            })}
                        />
                        <label htmlFor="playback-pipelining-enabled" className={styles["form-checkbox-label"]}>
                            Enable playback pipelining
                        </label>
                    </div>
                    <div className={styles["form-hint"]}>
                        Uses pipelining while filling the playback buffer. Keep this off unless the startup
                        benchmark or real playback tests show a clear win.
                    </div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <label htmlFor="pipelining-depth" className={styles["form-label"]}>
                        Default pipeline depth
                    </label>
                    <input
                        type="text"
                        id="pipelining-depth"
                        className={`${styles["form-input"]} ${config["usenet.pipelining.depth"] !== undefined && config["usenet.pipelining.depth"] !== "" && !isPositiveInteger(config["usenet.pipelining.depth"]) ? styles.error : ""}`}
                        placeholder="8"
                        value={config["usenet.pipelining.depth"] ?? ""}
                        onChange={(e) => setNewConfig({ ...config, "usenet.pipelining.depth": e.target.value })}
                    />
                    <div className={styles["form-hint"]}>
                        Requests kept in flight per connection (1–64). 8 is a good default. Each
                        provider can override this in its own settings.
                    </div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <label htmlFor="health-pipelining-depth" className={styles["form-label"]}>
                        Health-check pipeline depth
                    </label>
                    <input
                        type="text"
                        id="health-pipelining-depth"
                        className={`${styles["form-input"]} ${config["usenet.pipelining.health.depth"] !== undefined && config["usenet.pipelining.health.depth"] !== "" && !isPositiveInteger(config["usenet.pipelining.health.depth"]) ? styles.error : ""}`}
                        placeholder="32"
                        value={config["usenet.pipelining.health.depth"] ?? ""}
                        onChange={(e) => setNewConfig({ ...config, "usenet.pipelining.health.depth": e.target.value })}
                    />
                    <div className={styles["form-hint"]}>
                        STAT requests kept in flight per connection for article health checks (1–64). 32 is the default.
                    </div>
                </div>
                <div className={styles["form-group"]} style={{ marginTop: 12 }}>
                    <label htmlFor="health-pipelining-lanes" className={styles["form-label"]}>
                        Health-check pipeline lanes
                    </label>
                    <input
                        type="text"
                        id="health-pipelining-lanes"
                        className={`${styles["form-input"]} ${config["usenet.pipelining.health.lanes"] !== undefined && config["usenet.pipelining.health.lanes"] !== "" && !isPositiveInteger(config["usenet.pipelining.health.lanes"]) ? styles.error : ""}`}
                        placeholder="64"
                        value={config["usenet.pipelining.health.lanes"] ?? ""}
                        onChange={(e) => setNewConfig({ ...config, "usenet.pipelining.health.lanes": e.target.value })}
                    />
                    <div className={styles["form-hint"]}>
                        Parallel pipelined STAT connections for article health checks. 64 is the default; raise it to use more provider connections or lower it if a provider throttles checks.
                    </div>
                </div>
            </div>

            <ProviderModal
                show={showModal}
                provider={editingIndex !== null ? providerConfig.Providers[editingIndex] : null}
                onClose={handleCloseModal}
                onSave={handleSaveProvider}
                onApplyPlaybackPipelining={handleApplyPlaybackPipelining}
                onApplyHealthPipelining={handleApplyHealthPipelining}
                defaultPipeliningDepth={config["usenet.pipelining.depth"] || "8"}
                defaultHealthPipeliningDepth={config["usenet.pipelining.health.depth"] || "32"}
            />
        </div>
    );
}

type UsageRowProps = {
    provider: ConnectionDetails;
    usage: ProviderUsage | undefined;
    onReset: () => void;
};

function UsageRow({ provider, usage, onReset }: UsageRowProps) {
    const limit = provider.ByteLimit ?? null;
    const used = usage?.bytesUsed ?? 0;
    const hasLimit = limit !== null && limit > 0;
    const pct = hasLimit ? Math.min(100, (used / (limit as number)) * 100) : 0;
    // Thresholds match the soft-warning levels the backend would alert on if
    // we wired notifications. Keeping the same numbers here means the colors
    // tell the same story as any future alert email or webhook.
    const tone = hasLimit
        ? (pct >= 100 ? "danger" : pct >= 95 ? "danger" : pct >= 80 ? "warn" : "ok")
        : "neutral";

    const showAnything = hasLimit || used > 0 || usage !== undefined;
    if (!showAnything) return null;

    return (
        <div className={styles["usage-row"]}>
            <div className={styles["usage-header"]}>
                <span className={styles["usage-label"]}>
                    {hasLimit ? "Data Cap" : "Data Used"}
                </span>
                <span className={styles[`usage-value-${tone}`]}>
                    {hasLimit
                        ? `${formatBytes(used)} / ${formatBytes(limit as number)}  ·  ${pct.toFixed(1)}%`
                        : formatBytes(used)}
                </span>
                <button
                    type="button"
                    className={styles["usage-reset"]}
                    onClick={onReset}
                    title="Reset the counter to zero (e.g. after buying a new block)"
                >
                    Reset
                </button>
            </div>
            {hasLimit && (
                <div className={styles["usage-bar-track"]}>
                    <div
                        className={`${styles["usage-bar-fill"]} ${styles[`usage-bar-${tone}`]}`}
                        style={{ width: `${pct}%` }}
                    />
                </div>
            )}
            {usage && usage.daysRemaining !== null && usage.daysRemaining !== undefined && !usage.overLimit && (
                <div className={styles["usage-hint"]}>
                    {formatDaysRemaining(usage.daysRemaining)}
                </div>
            )}
            {usage?.overLimit && (
                <div className={styles["usage-warning"]}>
                    Data cap reached. This provider is paused to keep in-flight fetches from overshooting. Reset the counter or raise the cap to resume.
                </div>
            )}
        </div>
    );
}

type ProviderModalProps = {
    show: boolean;
    provider: ConnectionDetails | null;
    onClose: () => void;
    onSave: (provider: ConnectionDetails) => void;
    onApplyPlaybackPipelining: (enabled: boolean) => void;
    onApplyHealthPipelining: (enabled: boolean) => void;
    defaultPipeliningDepth: string;
    defaultHealthPipeliningDepth: string;
};

function ProviderModal({
    show, provider, onClose, onSave, onApplyPlaybackPipelining,
    onApplyHealthPipelining, defaultPipeliningDepth, defaultHealthPipeliningDepth,
}: ProviderModalProps) {
    const isEditing = provider !== null;
    const initialLimit = bytesToValueAndUnit(provider?.ByteLimit);
    const initialUsed = bytesToValueAndUnit(provider?.BytesUsedOffset);

    const [nickname, setNickname] = useState(provider?.Nickname || "");
    const [host, setHost] = useState(provider?.Host || "");
    const [port, setPort] = useState(provider?.Port?.toString() || "");
    const [useSsl, setUseSsl] = useState(provider?.UseSsl ?? true);
    const [user, setUser] = useState(provider?.User || "");
    const [pass, setPass] = useState(provider?.Pass || "");
    const [maxConnections, setMaxConnections] = useState(provider?.MaxConnections?.toString() || "");
    const [pipeliningDepth, setPipeliningDepth] = useState(provider?.PipeliningDepth?.toString() || "");
    const [healthPipeliningDepth, setHealthPipeliningDepth] = useState(provider?.HealthPipeliningDepth?.toString() || "");
    const [prepOnly, setPrepOnly] = useState(provider?.PrepOnly ?? false);
    const [prepSpreadEnabled, setPrepSpreadEnabled] = useState(provider?.PrepSpreadEnabled ?? true);
    const [type, setType] = useState<ProviderType>(provider?.Type ?? ProviderType.Pooled);
    const [limitValue, setLimitValue] = useState(initialLimit.value);
    const [limitUnit, setLimitUnit] = useState<ByteUnitLabel>(initialLimit.unit);
    const [initialUsedValue, setInitialUsedValue] = useState(initialUsed.value);
    const [initialUsedUnit, setInitialUsedUnit] = useState<ByteUnitLabel>(initialUsed.unit);
    const [isTestingConnection, setIsTestingConnection] = useState(false);
    const [connectionTested, setConnectionTested] = useState(false);
    const [testError, setTestError] = useState<string | null>(null);
    const [intensity, setIntensity] = useState<BenchmarkIntensity>("quick");
    const [isBenchmarking, setIsBenchmarking] = useState(false);
    const [benchmarkProgress, setBenchmarkProgress] = useState<BenchmarkProgress | null>(null);
    const [benchmarkResult, setBenchmarkResult] = useState<BenchmarkResult | null>(null);
    const [benchmarkError, setBenchmarkError] = useState<string | null>(null);
    const [benchmarkMode, setBenchmarkMode] = useState<BenchmarkMode>("speed");
    const benchmarkAbortRef = useRef<AbortController | null>(null);

    // Reset form when modal opens or provider changes
    useEffect(() => {
        if (show) {
            const lim = bytesToValueAndUnit(provider?.ByteLimit);
            const used = bytesToValueAndUnit(provider?.BytesUsedOffset);
            setNickname(provider?.Nickname || "");
            setHost(provider?.Host || "");
            setPort(provider?.Port?.toString() || "");
            setUseSsl(provider?.UseSsl ?? true);
            setUser(provider?.User || "");
            setPass(provider?.Pass || "");
            setMaxConnections(provider?.MaxConnections?.toString() || "");
            setPipeliningDepth(provider?.PipeliningDepth?.toString() || "");
            setHealthPipeliningDepth(provider?.HealthPipeliningDepth?.toString() || "");
            setPrepOnly(provider?.PrepOnly ?? false);
            setPrepSpreadEnabled(provider?.PrepSpreadEnabled ?? true);
            setType(provider?.Type ?? ProviderType.Pooled);
            setLimitValue(lim.value);
            setLimitUnit(lim.unit);
            setInitialUsedValue(used.value);
            setInitialUsedUnit(used.unit);
            setConnectionTested(false);
            setTestError(null);
            setIntensity("quick");
            setIsBenchmarking(false);
            setBenchmarkProgress(null);
            setBenchmarkResult(null);
            setBenchmarkError(null);
            setBenchmarkMode("speed");
        }
    }, [show, provider]);

    // Stop any in-flight speed test when the modal closes or unmounts so it
    // aborts on the backend and frees its connections immediately.
    useEffect(() => {
        if (!show) benchmarkAbortRef.current?.abort();
    }, [show]);
    useEffect(() => () => benchmarkAbortRef.current?.abort(), []);

    useEffect(() => {
        if (type === ProviderType.HealthChecksOnly) setBenchmarkMode("health");
    }, [type]);

    // Handle Escape key to close modal
    useEffect(() => {
        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && show) {
                onClose();
            }
        };

        if (show) {
            document.addEventListener('keydown', handleEscape);
            return () => document.removeEventListener('keydown', handleEscape);
        }
    }, [show, onClose]);

    const handleTestConnection = useCallback(async () => {
        setIsTestingConnection(true);
        setTestError(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);

            const response = await fetch('/api/test-usenet-connection', {
                method: 'POST',
                body: formData,
            });

            if (response.ok) {
                const data = await response.json();
                if (data.connected) {
                    setConnectionTested(true);
                    setTestError(null);
                } else {
                    setTestError("Connection test failed");
                }
            } else {
                setTestError("Failed to test connection");
            }
        } catch (error) {
            setTestError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
        } finally {
            setIsTestingConnection(false);
        }
    }, [host, port, useSsl, user, pass]);

    const handleAutoTune = useCallback(async () => {
        const pipeliningOnly = benchmarkMode === "pipelining";
        const startupOnly = benchmarkMode === "startup";
        const healthOnly = benchmarkMode === "health";
        // Abort any previous run still in flight before starting a new one.
        benchmarkAbortRef.current?.abort();
        const controller = new AbortController();
        benchmarkAbortRef.current = controller;

        setIsBenchmarking(true);
        setBenchmarkError(null);
        setBenchmarkResult(null);
        setBenchmarkProgress({ phase: "latency", status: "Starting speed test…", percent: 0, dataUsedBytes: 0, sweep: [] });

        // Live progress over the websocket — best-effort eye-candy; the POST
        // below returns the authoritative result regardless.
        let ws: WebSocket | null = null;
        try {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onopen = () => ws?.send(JSON.stringify(benchmarkTopic));
            ws.onmessage = receiveMessage((topic, message) => {
                if (topic !== 'bench') return;
                try {
                    const update = JSON.parse(message) as BenchmarkProgress;
                    // Ignore the terminal "done" frame (incl. any replayed from a
                    // previous run) so the bar doesn't flash to 100% then restart.
                    if (update.phase !== 'done') setBenchmarkProgress(update);
                } catch { /* ignore malformed progress */ }
            });
            ws.onerror = () => ws?.close();
        } catch { /* progress is optional */ }

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);
            formData.append('max-connections', maxConnections || "10");
            formData.append('intensity', intensity);
            formData.append('pipelining-only', pipeliningOnly ? 'true' : 'false');
            formData.append('startup-only', startupOnly ? 'true' : 'false');
            formData.append('health-only', healthOnly ? 'true' : 'false');
            formData.append('provider-type', String(type));

            const response = await fetch('/api/benchmark-usenet-connection', {
                method: 'POST', body: formData, signal: controller.signal,
            });
            if (!response.ok) {
                setBenchmarkError("The speed test couldn't run. Please try again.");
                return;
            }
            const data = await response.json();
            if (!data.status || !data.result) {
                setBenchmarkError(data.error || "The speed test couldn't run.");
                return;
            }
            setBenchmarkResult(data.result as BenchmarkResult);
            setConnectionTested(true); // a successful benchmark also proves the connection
        } catch (error) {
            if (error instanceof DOMException && error.name === 'AbortError') {
                // Cancelled by the user (Cancel button or closing the modal) — not an error.
                setBenchmarkProgress(null);
            } else {
                setBenchmarkError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
            }
        } finally {
            setIsBenchmarking(false);
            if (benchmarkAbortRef.current === controller) benchmarkAbortRef.current = null;
            ws?.close();
        }
    }, [host, port, useSsl, user, pass, maxConnections, intensity, benchmarkMode, type]);

    const handleApplyRecommendation = useCallback(() => {
        if (!benchmarkResult) return;
        // In pipelining-only mode there's no connection recommendation, so this
        // leaves Max Connections untouched and only applies pipelining.
        if (benchmarkResult.recommendedConnections && benchmarkResult.recommendedConnections > 0) {
            setMaxConnections(String(benchmarkResult.recommendedConnections));
        }
        if (benchmarkResult.pipelining) {
            setPipeliningDepth(String(benchmarkResult.pipelining.recommendedDepth));
        }
        if (benchmarkResult.startup) {
            setPipeliningDepth(String(benchmarkResult.startup.recommendedDepth));
            onApplyPlaybackPipelining(benchmarkResult.startup.recommendPlaybackPipelining);
        }
        if (benchmarkResult.healthPipelining?.reliable) {
            setHealthPipeliningDepth(String(benchmarkResult.healthPipelining.recommendedDepth));
            onApplyHealthPipelining(true);
        }
    }, [benchmarkResult, onApplyPlaybackPipelining, onApplyHealthPipelining]);

    const handleCancelBenchmark = useCallback(() => {
        benchmarkAbortRef.current?.abort();
    }, []);

    const handleSave = useCallback(() => {
        const byteLimit = valueAndUnitToBytes(limitValue, limitUnit);
        const initialUsedBytes = valueAndUnitToBytes(initialUsedValue, initialUsedUnit);

        // On a brand-new provider, an initial-used value also sets ResetAt to
        // now — otherwise the metrics rollup would count any pre-existing
        // history for the same hostname twice. On edit, leave ResetAt alone
        // (the dedicated Reset button is the right surface for that).
        const isNew = !isEditing;
        const offsetToPersist = isNew
            ? (initialUsedBytes ?? 0)
            : (provider?.BytesUsedOffset ?? 0);
        const resetAtToPersist = isNew && initialUsedBytes !== null
            ? Date.now()
            : (provider?.BytesUsedResetAt ?? 0);

        const trimmedNickname = nickname.trim();
        onSave({
            Type: type,
            Host: host,
            Port: parseInt(port, 10),
            UseSsl: useSsl,
            User: user,
            Pass: pass,
            MaxConnections: parseInt(maxConnections, 10),
            PipeliningDepth: pipeliningDepth.trim() === "" ? null : parseInt(pipeliningDepth, 10),
            HealthPipeliningDepth: healthPipeliningDepth.trim() === "" ? null : parseInt(healthPipeliningDepth, 10),
            PrepOnly: type === ProviderType.HealthChecksOnly ? false : prepOnly,
            PrepSpreadEnabled: type === ProviderType.HealthChecksOnly ? false : prepSpreadEnabled,
            Priority: provider?.Priority ?? 0,
            Nickname: trimmedNickname === "" ? undefined : trimmedNickname,
            PreviousType: type === ProviderType.Disabled ? provider?.PreviousType : undefined,
            ByteLimit: byteLimit,
            BytesUsedOffset: offsetToPersist,
            BytesUsedResetAt: resetAtToPersist,
        });
    }, [type, host, port, useSsl, user, pass, maxConnections, pipeliningDepth, healthPipeliningDepth, prepOnly, prepSpreadEnabled, nickname, provider, isEditing, limitValue, limitUnit, initialUsedValue, initialUsedUnit, onSave]);

    const handleOverlayClick = useCallback((e: React.MouseEvent) => {
        if (e.target === e.currentTarget) {
            onClose();
        }
    }, [onClose]);

    const isPipeliningDepthValid = pipeliningDepth.trim() === ""
        || (isPositiveInteger(pipeliningDepth) && Number(pipeliningDepth) <= 64);
    const isHealthPipeliningDepthValid = healthPipeliningDepth.trim() === ""
        || (isPositiveInteger(healthPipeliningDepth) && Number(healthPipeliningDepth) <= 64);

    const isFormValid = host.trim() !== ""
        && isPositiveInteger(port)
        && user.trim() !== ""
        && pass.trim() !== ""
        && isPositiveInteger(maxConnections)
        && isPipeliningDepthValid
        && isHealthPipeliningDepthValid;

    // The speed test doesn't need Max Connections (it can recommend one), just
    // a reachable provider.
    const canBenchmark = host.trim() !== ""
        && isPositiveInteger(port)
        && user.trim() !== ""
        && pass.trim() !== "";

    const canSave = isFormValid && (connectionTested || type == ProviderType.Disabled);

    if (!show) return null;

    return (
        <div className={styles["modal-overlay"]} onClick={handleOverlayClick}>
            <div className={styles["modal-container"]}>
                <div className={styles["modal-header"]}>
                    <h2 className={styles["modal-title"]}>
                        {provider ? "Edit Provider" : "Add Provider"}
                    </h2>
                    <button className={styles["modal-close"]} onClick={onClose} aria-label="Close">
                        ×
                    </button>
                </div>

                <div className={styles["modal-body"]}>
                    <div className={styles["form-grid"]}>
                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="provider-nickname" className={styles["form-label"]}>
                                Nickname (optional)
                            </label>
                            <input
                                type="text"
                                id="provider-nickname"
                                className={styles["form-input"]}
                                placeholder="e.g. Main provider"
                                value={nickname}
                                onChange={(e) => setNickname(e.target.value)}
                            />
                            <div className={styles["form-hint"]}>
                                Friendly label shown in the UI in place of the hostname.
                            </div>
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-host" className={styles["form-label"]}>
                                Host
                            </label>
                            <input
                                type="text"
                                id="provider-host"
                                className={styles["form-input"]}
                                placeholder="news.provider.com"
                                value={host}
                                onChange={(e) => {
                                    setHost(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-port" className={styles["form-label"]}>
                                Port
                            </label>
                            <input
                                type="text"
                                id="provider-port"
                                className={`${styles["form-input"]} ${!isPositiveInteger(port) && port !== "" ? styles.error : ""}`}
                                placeholder="563"
                                value={port}
                                onChange={(e) => {
                                    setPort(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-user" className={styles["form-label"]}>
                                Username
                            </label>
                            <input
                                type="text"
                                id="provider-user"
                                className={styles["form-input"]}
                                placeholder="username"
                                value={user}
                                onChange={(e) => {
                                    setUser(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-pass" className={styles["form-label"]}>
                                Password
                            </label>
                            <input
                                type="password"
                                id="provider-pass"
                                className={styles["form-input"]}
                                placeholder="password"
                                value={pass}
                                onChange={(e) => {
                                    setPass(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-max-connections" className={styles["form-label"]}>
                                Max Connections
                            </label>
                            <input
                                type="text"
                                id="provider-max-connections"
                                className={`${styles["form-input"]} ${!isPositiveInteger(maxConnections) && maxConnections !== "" ? styles.error : ""}`}
                                placeholder="20"
                                value={maxConnections}
                                onChange={(e) => setMaxConnections(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-pipelining-depth" className={styles["form-label"]}>
                                BODY pipeline depth
                            </label>
                            <input
                                type="text"
                                id="provider-pipelining-depth"
                                className={`${styles["form-input"]} ${!isPipeliningDepthValid ? styles.error : ""}`}
                                placeholder={defaultPipeliningDepth || "8"}
                                value={pipeliningDepth}
                                onChange={(e) => setPipeliningDepth(e.target.value)}
                            />
                            <div className={styles["form-hint"]}>
                                Requests kept in flight per connection (1–64) when NNTP pipelining is
                                enabled. Leave blank to use the global default.
                            </div>
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-health-pipelining-depth" className={styles["form-label"]}>
                                Health pipeline depth
                            </label>
                            <input
                                type="text"
                                id="provider-health-pipelining-depth"
                                className={`${styles["form-input"]} ${!isHealthPipeliningDepthValid ? styles.error : ""}`}
                                placeholder={defaultHealthPipeliningDepth || "32"}
                                value={healthPipeliningDepth}
                                onChange={(e) => setHealthPipeliningDepth(e.target.value)}
                            />
                            <div className={styles["form-hint"]}>
                                STAT requests kept in flight per connection (1–64) during health checks.
                                Leave blank to use the global health depth.
                            </div>
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-type" className={styles["form-label"]}>
                                Type
                            </label>
                            <select
                                id="provider-type"
                                className={styles["form-select"]}
                                value={type}
                                onChange={(e) => setType(parseInt(e.target.value, 10) as ProviderType)}
                            >
                                <option value={ProviderType.Disabled}>Disabled</option>
                                <option value={ProviderType.Pooled}>Pool Connections</option>
                                <option value={ProviderType.BackupOnly}>Backup Only</option>
                                <option value={ProviderType.HealthChecksOnly}>Health Checks Only</option>
                            </select>
                            <div className={styles["form-hint"]}>
                                Health Checks Only permits STAT requests and blocks all article and header downloads.
                            </div>
                        </div>


                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-ssl"
                                    className={`${styles["form-checkbox"]} toggle-switch`}
                                    checked={useSsl}
                                    onChange={(e) => {
                                        setUseSsl(e.target.checked);
                                        setConnectionTested(false);
                                    }}
                                />
                                <label htmlFor="provider-ssl" className={styles["form-checkbox-label"]}>
                                    Use SSL
                                </label>
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-prep-only"
                                    className={`${styles["form-checkbox"]} toggle-switch`}
                                    checked={type !== ProviderType.HealthChecksOnly && prepOnly}
                                    disabled={type === ProviderType.HealthChecksOnly}
                                    onChange={(e) => setPrepOnly(e.target.checked)}
                                />
                                <label htmlFor="provider-prep-only" className={styles["form-checkbox-label"]}>
                                    Use for prep only
                                </label>
                            </div>
                            <div className={styles["form-hint"]}>
                                Allows imports, fast checks, preflight and startup prep to use this provider, but excludes it from playback streams.
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-prep-spread"
                                    className={`${styles["form-checkbox"]} toggle-switch`}
                                    checked={type !== ProviderType.HealthChecksOnly && prepSpreadEnabled}
                                    disabled={type !== ProviderType.Pooled}
                                    onChange={(e) => setPrepSpreadEnabled(e.target.checked)}
                                />
                                <label htmlFor="provider-prep-spread" className={styles["form-checkbox-label"]}>
                                    Include in queue/prep spread
                                </label>
                            </div>
                            <div className={styles["form-hint"]}>
                                When global prep spreading is enabled, this pooled provider can receive first-choice queue/import, preflight and health-check work.
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label className={styles["form-label"]}>
                                Data Cap (optional)
                            </label>
                            <div className={styles["form-paired-input"]}>
                                <input
                                    type="text"
                                    inputMode="decimal"
                                    className={styles["form-input"]}
                                    placeholder="Leave blank for no cap"
                                    value={limitValue}
                                    onChange={(e) => setLimitValue(e.target.value)}
                                />
                                <select
                                    className={styles["form-select"]}
                                    value={limitUnit}
                                    onChange={(e) => setLimitUnit(e.target.value as ByteUnitLabel)}
                                >
                                    {BYTE_UNITS.map(u => (
                                        <option key={u.label} value={u.label}>{u.label}</option>
                                    ))}
                                </select>
                            </div>
                            <div className={styles["form-hint"]}>
                                For block accounts: total bytes you've purchased. The provider auto-pauses at ~95% of this value to absorb in-flight requests, so set the cap to your full block size. The 5% headroom keeps you from overshooting.
                            </div>
                        </div>

                        {!isEditing && (
                            <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                                <label className={styles["form-label"]}>
                                    Already Used (optional)
                                </label>
                                <div className={styles["form-paired-input"]}>
                                    <input
                                        type="text"
                                        inputMode="decimal"
                                        className={styles["form-input"]}
                                        placeholder="0"
                                        value={initialUsedValue}
                                        onChange={(e) => setInitialUsedValue(e.target.value)}
                                    />
                                    <select
                                        className={styles["form-select"]}
                                        value={initialUsedUnit}
                                        onChange={(e) => setInitialUsedUnit(e.target.value as ByteUnitLabel)}
                                    >
                                        {BYTE_UNITS.map(u => (
                                            <option key={u.label} value={u.label}>{u.label}</option>
                                        ))}
                                    </select>
                                </div>
                                <div className={styles["form-hint"]}>
                                    Seed the counter when migrating a partially-used block from another client. Leave empty for a fresh block.
                                </div>
                            </div>
                        )}
                    </div>

                    {testError && (
                        <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: '16px' }}>
                            {testError}
                        </div>
                    )}

                    {connectionTested && (
                        <div className={`${styles.alert} ${styles["alert-success"]}`} style={{ marginTop: '16px' }}>
                            Connection test successful!
                        </div>
                    )}

                    <BenchmarkPanel
                        canBenchmark={canBenchmark}
                        isBenchmarking={isBenchmarking}
                        intensity={intensity}
                        setIntensity={setIntensity}
                        mode={benchmarkMode}
                        setMode={setBenchmarkMode}
                        healthChecksOnly={type === ProviderType.HealthChecksOnly}
                        progress={benchmarkProgress}
                        result={benchmarkResult}
                        error={benchmarkError}
                        onRun={handleAutoTune}
                        onCancel={handleCancelBenchmark}
                        onApply={handleApplyRecommendation}
                    />
                </div>

                <div className={styles["modal-footer"]}>
                    <div className={styles["modal-footer-left"]}></div>
                    <div className={styles["modal-footer-right"]}>
                        <Button variant="secondary" onClick={onClose}>
                            Cancel
                        </Button>
                        {!canSave ? (
                            <Button
                                variant="primary"
                                onClick={handleTestConnection}
                                disabled={!isFormValid || isTestingConnection}
                            >
                                {isTestingConnection ? "Testing..." : "Test Connection"}
                            </Button>
                        ) : (
                            <Button variant="primary" onClick={handleSave} disabled={!canSave}>
                                Save Provider
                            </Button>
                        )}
                    </div>
                </div>
            </div>
        </div>
    );
}

type BenchmarkPanelProps = {
    canBenchmark: boolean;
    isBenchmarking: boolean;
    intensity: BenchmarkIntensity;
    setIntensity: (value: BenchmarkIntensity) => void;
    mode: BenchmarkMode;
    setMode: (value: BenchmarkMode) => void;
    healthChecksOnly: boolean;
    progress: BenchmarkProgress | null;
    result: BenchmarkResult | null;
    error: string | null;
    onRun: () => void;
    onCancel: () => void;
    onApply: () => void;
};

function BenchmarkPanel(props: BenchmarkPanelProps) {
    const {
        canBenchmark, isBenchmarking, intensity, setIntensity,
        mode, setMode, healthChecksOnly, progress, result, error, onRun, onCancel, onApply,
    } = props;
    const pipeliningOnly = mode === "pipelining";
    const startupOnly = mode === "startup";
    const healthOnly = mode === "health";
    const [applied, setApplied] = useState(false);
    // A fresh result means the previous "Applied" state no longer holds.
    useEffect(() => { setApplied(false); }, [result]);

    const recommended = result?.recommendedConnections ?? null;
    const livePoints = isBenchmarking ? (progress?.sweep ?? []) : (result?.sweep ?? []);
    const bestSpeed = result?.throughputTested && result.sweep.length > 0
        ? Math.max(...result.sweep.map(p => p.mbPerSec))
        : null;
    const pipe = result?.pipelining ?? null;
    const startup = result?.startup ?? null;
    const health = result?.healthPipelining ?? null;
    const startupRecommended = startup ? getStartupPoint(startup, startup.recommendedDepth) : null;
    const startupFirstGain = startup && startupRecommended ? percentImprovement(startup.nonPipelinedFirstMs, startupRecommended.firstMs) : null;
    const startupReadyGain = startup && startupRecommended ? percentImprovement(startup.nonPipelinedReadyMs, startupRecommended.readyMs) : null;
    const pipeBest = pipe && pipe.tested.length > 0 ? Math.max(...pipe.tested.map(t => t.mbPerSec)) : (pipe?.baselineMbPerSec ?? 0);
    const pipeGainPct = pipe && pipe.baselineMbPerSec > 0 ? Math.round((pipeBest / pipe.baselineMbPerSec - 1) * 100) : 0;
    const canApply = !!result && result.throughputTested && (
        recommended != null
        || (result.pipeliningOnly && !!pipe)
        || (result.startupOnly && !!startup)
        || (result.healthOnly && health?.reliable === true)
    );

    return (
        <div className={styles["bench-panel"]}>
            <div className={styles["bench-head"]}>
                <div className={styles["bench-heading"]}>
                    <div className={styles["bench-title"]}>
                        {healthOnly ? "Health STAT pipeline" : "Auto-tune connections"}
                    </div>
                    <div className={styles["form-hint"]} style={{ marginTop: 0 }}>
                        {pipeliningOnly
                            ? "Keeps your Max Connections and just measures the best NNTP pipelining depth at that count."
                            : startupOnly
                                ? "Measures first-buffer playback startup with normal parallel fetching vs NNTP pipelining."
                                : healthOnly
                                    ? "Repeats the real health-check STAT workload and rejects fast but unreliable pipeline depths."
                                    : "Runs a real speed & latency test, then recommends the best connection count and pipelining settings."}
                    </div>
                </div>
                <div className={styles["bench-controls"]}>
                    <div className={styles["bench-intensity"]} role="group" aria-label="Test intensity">
                        <Button
                            variant={intensity === "quick" ? "primary" : "secondary"}
                            onClick={() => setIntensity("quick")}
                            disabled={isBenchmarking}
                            aria-pressed={intensity === "quick"}
                        >
                            Quick
                        </Button>
                        <Button
                            variant={intensity === "thorough" ? "primary" : "secondary"}
                            onClick={() => setIntensity("thorough")}
                            disabled={isBenchmarking}
                            aria-pressed={intensity === "thorough"}
                        >
                            Thorough
                        </Button>
                    </div>
                    <Button variant="primary" onClick={onRun} disabled={!canBenchmark || isBenchmarking}>
                        {isBenchmarking ? "Testing…" : (
                            healthOnly ? "Test health STAT" : startupOnly ? "Test startup" : pipeliningOnly ? "Test pipelining" : "Run speed test"
                        )}
                    </Button>
                    {isBenchmarking && (
                        <Button variant="secondary" onClick={onCancel}>Cancel</Button>
                    )}
                </div>
            </div>

            <div
                className={styles["bench-intensity"]}
                style={{ marginTop: 12 }}
                role="group"
                aria-label="Benchmark mode"
            >
                {([
                    ["speed", "Speed"],
                    ["pipelining", "BODY pipeline"],
                    ["startup", "Playback startup"],
                    ["health", "Health STAT"],
                ] as const).map(([value, label]) => (
                    <Button
                        key={value}
                        variant={mode === value ? "primary" : "secondary"}
                        onClick={() => setMode(value)}
                        disabled={isBenchmarking || (healthChecksOnly && value !== "health")}
                        aria-pressed={mode === value}
                    >
                        {label}
                    </Button>
                ))}
            </div>

            <div className={styles["form-hint"]}>
                {pipeliningOnly
                    ? "Won't change your connection count — it tests pipelining depth at the Max Connections you've set. Run it idle for the cleanest read."
                    : startupOnly
                        ? "Compares time to the first decoded article and a small startup buffer. This is the best benchmark for first-frame speed."
                        : healthOnly
                            ? "Runs repeated mixed present/missing STAT batches at depths 1, 4, 8, 16, 32 and 64. No article bodies are downloaded."
                            : (intensity === "quick"
                                ? "Quick downloads roughly 100 MB of real data — light on metered / block accounts."
                                : "Thorough downloads roughly 400 MB for steadier numbers on fast connections.")}
            </div>

            {error && (
                <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: 12 }}>{error}</div>
            )}

            {isBenchmarking && progress && (
                <div className={styles["bench-progress"]}>
                    <div className={styles["bench-progress-head"]}>
                        <span>{progress.status}</span>
                        <span>{healthOnly ? "STAT only" : `${formatBytes(progress.dataUsedBytes)} used`}</span>
                    </div>
                    <div className={styles["usage-bar-track"]}>
                        <div
                            className={`${styles["usage-bar-fill"]} ${styles["bench-progress-fill"]}`}
                            style={{ width: `${Math.max(2, Math.min(100, progress.percent))}%` }}
                        />
                    </div>
                </div>
            )}

            {livePoints.length > 0 && !(isBenchmarking ? (pipeliningOnly || startupOnly || healthOnly) : (result?.pipeliningOnly || result?.startupOnly || result?.healthOnly)) && (
                <SweepChart points={livePoints} recommended={recommended} />
            )}

            {result && !isBenchmarking && (
                <>
                    {result.healthOnly ? (
                        health ? (
                            <>
                                <HealthStatTable health={health} />
                                <div className={styles["bench-stats"]}>
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Recommendation</span>
                                        <span className={`${styles["bench-stat-value"]} ${styles["bench-stat-strong"]}`}>
                                            {health.reliable ? `Depth ${health.recommendedDepth}` : "Unavailable"}
                                        </span>
                                        <span className={styles["bench-stat-sub"]}>
                                            {health.reliable ? "all reliability checks passed" : "no reliable tested depth"}
                                        </span>
                                    </div>
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Test shape</span>
                                        <span className={styles["bench-stat-value"]}>
                                            {health.articlesPerRound} × {health.rounds}
                                        </span>
                                        <span className={styles["bench-stat-sub"]}>
                                            STATs per depth · {health.knownMissingArticles} known missing
                                        </span>
                                    </div>
                                    {result.latency && (
                                        <div className={styles["bench-stat"]}>
                                            <span className={styles["bench-stat-label"]}>Latency</span>
                                            <span className={styles["bench-stat-value"]}>{result.latency.avgMs} ms</span>
                                            <span className={styles["bench-stat-sub"]}>{result.latency.minMs} ms min</span>
                                        </div>
                                    )}
                                </div>
                                <div className={styles["bench-pipe"]}>
                                    {health.reliable
                                        ? <>Use provider health depth <strong>{health.recommendedDepth}</strong>. Every repeated batch completed and matched the reference results.</>
                                        : <>Health STAT pipelining was not reliable on this provider. Leave its health-depth override blank and consider disabling it for health checks.</>}
                                </div>
                            </>
                        ) : (
                            <div className={styles["bench-note"]}>Couldn’t measure health STAT pipelining. Try again when idle.</div>
                        )
                    ) : result.startupOnly ? (
                        startup ? (
                            <>
                                <div className={styles["bench-stats"]}>
	                                    <div className={styles["bench-stat"]}>
	                                        <span className={styles["bench-stat-label"]}>Normal buffer</span>
	                                        <span className={`${styles["bench-stat-value"]} ${styles["bench-stat-strong"]}`}>
	                                            {startup.nonPipelinedReadyMs} ms
	                                        </span>
	                                        <span className={styles["bench-stat-sub"]}>
	                                            {startup.nonPipelinedFirstMs} ms first segment
	                                        </span>
	                                    </div>
	                                    <div className={styles["bench-stat"]}>
	                                        <span className={styles["bench-stat-label"]}>Playback pipelining</span>
	                                        <span className={`${styles["bench-stat-value"]} ${styles["bench-stat-strong"]}`}>
	                                            {startup.recommendPlaybackPipelining ? `Depth ${startup.recommendedDepth}` : "Off"}
	                                        </span>
	                                        <span className={styles["bench-stat-sub"]}>
	                                            {startup.recommendPlaybackPipelining && startupReadyGain != null
	                                                ? `${startupReadyGain}% faster buffer`
	                                                : "no safe startup gain"}
	                                        </span>
	                                    </div>
	                                    {startupRecommended && (
	                                        <div className={styles["bench-stat"]}>
	                                            <span className={styles["bench-stat-label"]}>Best tested depth</span>
	                                            <span className={styles["bench-stat-value"]}>
	                                                D{startupRecommended.depth} · {startupRecommended.readyMs} ms
	                                            </span>
	                                            <span className={styles["bench-stat-sub"]}>
	                                                {startupFirstGain != null
	                                                    ? `${startupRecommended.firstMs} ms first · ${startupFirstGain}%`
	                                                    : `${startupRecommended.firstMs} ms first`}
	                                            </span>
	                                        </div>
	                                    )}
	                                    <div className={styles["bench-stat"]}>
	                                        <span className={styles["bench-stat-label"]}>Test shape</span>
	                                        <span className={styles["bench-stat-value"]}>{startup.segments}</span>
                                        <span className={styles["bench-stat-sub"]}>
                                            articles · {startup.nonPipelinedConnections} normal conns
                                        </span>
                                    </div>
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Data used</span>
                                        <span className={styles["bench-stat-value"]}>{formatBytes(result.dataUsedBytes)}</span>
                                    </div>
                                </div>
	                                <StartupChart startup={startup} />
	                                <div className={styles["bench-pipe"]}>
	                                    {startup.recommendPlaybackPipelining
	                                        ? <>Enable <strong>playback pipelining</strong> at depth <strong>{startup.recommendedDepth}</strong>. This benchmark measures full first-segment readiness and the initial startup buffer, not long-run download speed.</>
	                                        : <>Playback pipelining did not improve startup enough without delaying the first segment. Leave it off for playback.</>}
	                                </div>
	                            </>
                        ) : (
                            <div className={styles["bench-note"]}>
                                Couldn’t measure playback startup{result.latency ? ` (latency ${result.latency.avgMs} ms)` : ""}. Try again when idle.
                            </div>
                        )
                    ) : result.pipeliningOnly ? (
                        pipe ? (
                            <>
                                <DepthChart pipe={pipe} />
                                <div className={styles["bench-stats"]}>
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Pipelining</span>
                                        <span className={`${styles["bench-stat-value"]} ${styles["bench-stat-strong"]}`}>
                                            {pipe.recommendEnabled ? `Depth ${pipe.recommendedDepth}` : "Off"}
                                        </span>
                                        <span className={styles["bench-stat-sub"]}>
                                            {pipe.recommendEnabled ? `≈ +${pipeGainPct}% vs off` : "no real gain"}
                                        </span>
                                    </div>
                                    {result.latency && (
                                        <div className={styles["bench-stat"]}>
                                            <span className={styles["bench-stat-label"]}>Latency</span>
                                            <span className={styles["bench-stat-value"]}>{result.latency.avgMs} ms</span>
                                            <span className={styles["bench-stat-sub"]}>{result.latency.minMs} ms min</span>
                                        </div>
                                    )}
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Tested at</span>
                                        <span className={styles["bench-stat-value"]}>{pipe.testedAtConnections}</span>
                                        <span className={styles["bench-stat-sub"]}>connections</span>
                                    </div>
                                    <div className={styles["bench-stat"]}>
                                        <span className={styles["bench-stat-label"]}>Data used</span>
                                        <span className={styles["bench-stat-value"]}>{formatBytes(result.dataUsedBytes)}</span>
                                    </div>
                                </div>
                                <div className={styles["bench-pipe"]}>
                                    {pipe.recommendEnabled
                                        ? <>Turn on <strong>NNTP pipelining</strong> at depth <strong>{pipe.recommendedDepth}</strong> — measurably faster at your {pipe.testedAtConnections} connections.</>
                                        : <>NNTP pipelining didn’t help at your {pipe.testedAtConnections} connections — leave it off.</>}
                                </div>
                            </>
                        ) : (
                            <div className={styles["bench-note"]}>
                                Couldn’t measure pipelining{result.latency ? ` (latency ${result.latency.avgMs} ms)` : ""}. Try again when idle.
                            </div>
                        )
                    ) : result.throughputTested && recommended ? (
                        <div className={styles["bench-stats"]}>
                            <div className={styles["bench-stat"]}>
                                <span className={styles["bench-stat-label"]}>Recommended</span>
                                <span className={`${styles["bench-stat-value"]} ${styles["bench-stat-strong"]}`}>{recommended}</span>
                                <span className={styles["bench-stat-sub"]}>
                                    connection{recommended === 1 ? "" : "s"}{bestSpeed != null ? ` · ≈ ${bestSpeed.toFixed(1)} MB/s` : ""}
                                </span>
                            </div>
                            {result.latency && (
                                <div className={styles["bench-stat"]}>
                                    <span className={styles["bench-stat-label"]}>Latency</span>
                                    <span className={styles["bench-stat-value"]}>{result.latency.avgMs} ms</span>
                                    <span className={styles["bench-stat-sub"]}>{result.latency.minMs} ms min</span>
                                </div>
                            )}
                            {result.providerConnectionCap != null && (
                                <div className={styles["bench-stat"]}>
                                    <span className={styles["bench-stat-label"]}>Provider cap</span>
                                    <span className={styles["bench-stat-value"]}>{result.providerConnectionCap}</span>
                                    <span className={styles["bench-stat-sub"]}>max at once</span>
                                </div>
                            )}
                            <div className={styles["bench-stat"]}>
                                <span className={styles["bench-stat-label"]}>Data used</span>
                                <span className={styles["bench-stat-value"]}>{formatBytes(result.dataUsedBytes)}</span>
                            </div>
                        </div>
                    ) : (
                        <div className={styles["bench-note"]}>
                            Latency measured{result.latency ? ` — ${result.latency.avgMs} ms avg` : ""}. Download something first to get a connection recommendation.
                        </div>
                    )}

                    {!result.pipeliningOnly && pipe && (
                        <div className={styles["bench-pipe"]}>
                            {pipe.recommendEnabled
                                ? <>Turn on <strong>NNTP pipelining</strong> at depth <strong>{pipe.recommendedDepth}</strong> — measurably faster on this connection.</>
                                : <>NNTP pipelining didn’t help here — leave it off.</>}
                        </div>
                    )}

                    {result.warnings.length > 0 && (
                        <ul className={styles["bench-warnings"]}>
                            {result.warnings.map((w, i) => <li key={i}>{w}</li>)}
                        </ul>
                    )}

                    {canApply && (
                        <div className={styles["bench-actions"]}>
                            <Button variant={applied ? "secondary" : "primary"} onClick={() => { onApply(); setApplied(true); }}>
                                {applied ? "Applied ✓ — review & save" : (
                                    result.healthOnly ? "Apply health depth"
                                        : result.startupOnly ? "Apply playback recommendation"
                                            : result.pipeliningOnly ? "Apply pipelining"
                                                : "Apply recommendation"
                                )}
                            </Button>
                        </div>
                    )}
                </>
            )}
        </div>
    );
}

function HealthStatTable({ health }: { health: BenchmarkHealthPipelining }) {
    return (
        <div className={styles["bench-startup-table"]}>
            <div className={`${styles["bench-startup-row"]} ${styles["bench-startup-head"]}`}>
                <span>Depth</span>
                <span>STAT rate</span>
                <span>Average batch</span>
                <span>Reliability</span>
            </div>
            {health.tested.map(point => {
                const recommended = health.reliable && point.reliable && point.depth === health.recommendedDepth;
                const failure = point.failure
                    ?? (point.reliable ? `${point.completedRounds}/${point.requiredRounds} rounds` : "failed");
                return (
                    <div
                        className={`${styles["bench-startup-row"]} ${recommended ? styles["bench-startup-row-rec"] : ""}`}
                        key={point.depth}
                        title={point.failure ?? undefined}
                    >
                        <span>D{point.depth}</span>
                        <span>{point.statsPerSecond.toFixed(0)} /s</span>
                        <span>{point.averageMs.toFixed(0)} ms</span>
                        <span>{recommended ? "recommended" : point.reliable ? "reliable" : failure}</span>
                    </div>
                );
            })}
        </div>
    );
}

function StartupChart({ startup }: { startup: BenchmarkStartup }) {
    const rows = [
        {
            label: "Normal",
            firstMs: startup.nonPipelinedFirstMs,
            readyMs: startup.nonPipelinedReadyMs,
            note: `${startup.nonPipelinedConnections} connection${startup.nonPipelinedConnections === 1 ? "" : "s"}`,
            recommended: !startup.recommendPlaybackPipelining,
        },
        ...startup.pipelined.map(p => ({
            label: `D${p.depth}`,
            firstMs: p.firstMs,
            readyMs: p.readyMs,
            note: p.depth === startup.recommendedDepth && startup.recommendPlaybackPipelining ? "recommended" : "",
            recommended: p.depth === startup.recommendedDepth && startup.recommendPlaybackPipelining,
        })),
    ];
    return (
        <div className={styles["bench-startup-table"]}>
            <div className={`${styles["bench-startup-row"]} ${styles["bench-startup-head"]}`}>
                <span>Mode</span>
                <span>First segment</span>
                <span>Startup buffer</span>
                <span>Result</span>
            </div>
            {rows.map(row => (
                <div
                    className={`${styles["bench-startup-row"]} ${row.recommended ? styles["bench-startup-row-rec"] : ""}`}
                    key={row.label}
                >
                    <span>{row.label}</span>
                    <span>{row.firstMs.toFixed(0)} ms</span>
                    <span>{row.readyMs.toFixed(0)} ms</span>
                    <span>{row.note || formatStartupGain(startup.nonPipelinedReadyMs, row.readyMs)}</span>
                </div>
            ))}
        </div>
    );
}

function getStartupPoint(startup: BenchmarkStartup, depth: number) {
    return startup.pipelined.find(p => p.depth === depth) ?? null;
}

function percentImprovement(baseline: number, measured: number) {
    if (baseline <= 0 || measured <= 0) return null;
    return Math.round((1 - measured / baseline) * 100);
}

function formatStartupGain(baseline: number, measured: number) {
    const gain = percentImprovement(baseline, measured);
    if (gain == null) return "";
    if (gain > 0) return `${gain}% faster`;
    if (gain < 0) return `${Math.abs(gain)}% slower`;
    return "same";
}

function SweepChart({ points, recommended }: { points: BenchmarkSweepPoint[]; recommended: number | null }) {
    const max = Math.max(...points.map(p => p.mbPerSec), 0.0001);
    return (
        <div className={styles["bench-chart"]}>
            <div className={styles["bench-chart-bars"]}>
                {points.map((p, i) => {
                    const isRec = recommended != null && p.connections === recommended;
                    const height = Math.max(4, Math.round((p.mbPerSec / max) * 104));
                    return (
                        <div key={i} className={`${styles["bench-chart-col"]} ${isRec ? styles["bench-chart-col-rec"] : ""}`}>
                            <span className={styles["bench-chart-val"]}>
                                {p.mbPerSec >= 10 ? p.mbPerSec.toFixed(0) : p.mbPerSec.toFixed(1)}
                            </span>
                            <div
                                className={styles["bench-chart-bar"]}
                                style={{ height: `${height}px` }}
                                title={`${p.connections} connections → ${p.mbPerSec.toFixed(1)} MB/s`}
                            />
                            <span className={styles["bench-chart-label"]}>{p.connections}</span>
                        </div>
                    );
                })}
            </div>
            <div className={styles["bench-chart-foot"]}>
                <span className={styles["form-hint"]} style={{ margin: 0 }}>MB/s by connection count</span>
                {recommended != null && <span className={styles["form-hint"]} style={{ margin: 0 }}>recommended: {recommended}</span>}
            </div>
        </div>
    );
}

function DepthChart({ pipe }: { pipe: BenchmarkPipelining }) {
    const points = [
        { label: "Off", mbPerSec: pipe.baselineMbPerSec, rec: !pipe.recommendEnabled },
        ...pipe.tested.map(t => ({
            label: String(t.depth),
            mbPerSec: t.mbPerSec,
            rec: pipe.recommendEnabled && t.depth === pipe.recommendedDepth,
        })),
    ];
    const max = Math.max(...points.map(p => p.mbPerSec), 0.0001);
    return (
        <div className={styles["bench-chart"]}>
            <div className={styles["bench-chart-bars"]}>
                {points.map((p, i) => {
                    const height = Math.max(4, Math.round((p.mbPerSec / max) * 104));
                    return (
                        <div key={i} className={`${styles["bench-chart-col"]} ${p.rec ? styles["bench-chart-col-rec"] : ""}`}>
                            <span className={styles["bench-chart-val"]}>
                                {p.mbPerSec >= 10 ? p.mbPerSec.toFixed(0) : p.mbPerSec.toFixed(1)}
                            </span>
                            <div
                                className={styles["bench-chart-bar"]}
                                style={{ height: `${height}px` }}
                                title={`${p.label} → ${p.mbPerSec.toFixed(1)} MB/s`}
                            />
                            <span className={styles["bench-chart-label"]}>{p.label}</span>
                        </div>
                    );
                })}
            </div>
            <div className={styles["bench-chart-foot"]}>
                <span className={styles["form-hint"]} style={{ margin: 0 }}>MB/s by pipeline depth</span>
                <span className={styles["form-hint"]} style={{ margin: 0 }}>
                    {pipe.recommendEnabled ? `best: depth ${pipe.recommendedDepth}` : "best: off"}
                </span>
            </div>
        </div>
    );
}

export function isUsenetSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["usenet.providers"] !== newConfig["usenet.providers"]
        || config["usenet.pipelining.playback.enabled"] !== newConfig["usenet.pipelining.playback.enabled"]
        || config["usenet.pipelining.health.enabled"] !== newConfig["usenet.pipelining.health.enabled"]
        || config["usenet.pipelining.health.depth"] !== newConfig["usenet.pipelining.health.depth"]
        || config["usenet.pipelining.health.lanes"] !== newConfig["usenet.pipelining.health.lanes"]
        || config["usenet.pipelining.depth"] !== newConfig["usenet.pipelining.depth"]
        || config["usenet.cascade.enabled"] !== newConfig["usenet.cascade.enabled"]
        || config["usenet.prep-spread.enabled"] !== newConfig["usenet.prep-spread.enabled"]
}

export function isPositiveInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num > 0 && value.trim() === num.toString();
}
