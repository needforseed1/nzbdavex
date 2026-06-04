import type { Route } from "./+types/route";
import { useEffect, useState } from "react";
import { useFetcher, useRevalidator } from "react-router";
import { Form, Button } from "react-bootstrap";
import styles from "./route.module.css";
import { backendClient, type WatchtowerItem, type WatchtowerSource } from "~/clients/backend-client.server";

const POLL_INTERVAL_MS = 5000;

const SCOPE_OPTIONS: { value: string; label: string }[] = [
    { value: "", label: "Default scope" },
    { value: "latest-season", label: "Latest season" },
    { value: "first-season", label: "First season" },
    { value: "all-aired", label: "All aired seasons" },
    { value: "recent", label: "Recent episodes" },
    { value: "off", label: "Don't expand" },
];

export async function loader() {
    return await backendClient.getWatchtower();
}

export async function action({ request }: Route.ActionArgs) {
    const form = await request.formData();
    const fields: Record<string, string> = {};
    for (const [k, v] of form.entries()) fields[k] = String(v);
    try {
        if (fields.action === "discover-catalogs") {
            const discovered = await backendClient.discoverStremioCatalogs(fields.url ?? "");
            return { ok: true as const, discovered };
        }
        await backendClient.watchtowerMutate(fields);
        return { ok: true as const };
    } catch (e: any) {
        return { ok: false as const, error: e?.message ?? String(e) };
    }
}

export default function Watchtower({ loaderData }: Route.ComponentProps) {
    const { enabled, sources, items, stats } = loaderData;
    const addFetcher = useFetcher<typeof action>();
    const discoverFetcher = useFetcher<typeof action>();
    const bulkFetcher = useFetcher<typeof action>();
    const revalidator = useRevalidator();

    const discovered = discoverFetcher.data?.ok && "discovered" in discoverFetcher.data
        ? discoverFetcher.data.discovered
        : undefined;
    const discoverError = discoverFetcher.data && discoverFetcher.data.ok === false
        ? discoverFetcher.data.error : undefined;

    const [selected, setSelected] = useState<Set<string>>(new Set());
    const [discoveryDismissed, setDiscoveryDismissed] = useState(false);
    const [query, setQuery] = useState("");
    const [stateFilter, setStateFilter] = useState<string | null>(null);
    const toggle = (s: string) => setStateFilter(cur => (cur === s ? null : s));

    useEffect(() => {
        if (discovered) {
            setSelected(new Set(discovered.catalogs.map(c => c.url)));
            setDiscoveryDismissed(false);
        }
    }, [discovered]);

    useEffect(() => {
        if (bulkFetcher.state === "idle" && bulkFetcher.data?.ok) {
            setDiscoveryDismissed(true);
        }
    }, [bulkFetcher.state, bulkFetcher.data]);

    const chosenCatalogs = (discovered?.catalogs ?? []).filter(c => selected.has(c.url));
    const sourcesJson = JSON.stringify(chosenCatalogs.map(c => ({
        url: c.url,
        name: discovered?.addonName ? `${discovered.addonName}: ${c.name}` : c.name,
    })));

    const expanders = items.filter(it => it.state === "expander");
    const childrenByExpander = new Map<string, WatchtowerItem[]>();
    for (const it of items) {
        if (!it.expanderKey) continue;
        const arr = childrenByExpander.get(it.expanderKey);
        if (arr) arr.push(it); else childrenByExpander.set(it.expanderKey, [it]);
    }
    const orphans = items.filter(it => it.state !== "expander" && !it.expanderKey);

    const q = query.trim().toLowerCase();
    const textMatch = (it: WatchtowerItem) =>
        !q || it.title.toLowerCase().includes(q) || it.contentId.toLowerCase().includes(q);
    const childStateOk = (c: WatchtowerItem) =>
        !stateFilter || stateFilter === "expander" || c.state === stateFilter;
    const showAsShow = !stateFilter || stateFilter === "expander";
    const visibleExpanders = expanders
        .map(ex => ({
            ex,
            kids: (childrenByExpander.get(ex.key) ?? [])
                .filter(c => (textMatch(c) || textMatch(ex)) && childStateOk(c)),
        }))
        .filter(g => g.kids.length > 0 || (showAsShow && textMatch(g.ex)));
    const visibleOrphans = orphans.filter(it =>
        textMatch(it) && (!stateFilter || it.state === stateFilter));
    const filtering = q !== "" || stateFilter !== null;
    const nothingShown = visibleExpanders.length === 0 && visibleOrphans.length === 0;

    useEffect(() => {
        const t = setInterval(() => revalidator.revalidate(), POLL_INTERVAL_MS);
        return () => clearInterval(t);
    }, [revalidator]);

    return (
        <div className={styles.page}>
            <div className={styles.header}>
                <div>
                    <h2 className={styles.title}>Watchtower</h2>
                    <div className={styles.subtitle}>
                        Keeps your lists ready. Each title is pre-resolved to a healthy release and
                        re-verified over time, so it's found and ready before you need it. Pointer-only:
                        it stores segment maps, never video.
                    </div>
                </div>
                <div className={styles.stats}>
                    <Stat label="Ready" value={stats.ready} tone="ok" active={stateFilter === "ready"} onClick={() => toggle("ready")} />
                    <Stat label="Scouting" value={stats.scouting} tone="warn" active={stateFilter === "scouting"} onClick={() => toggle("scouting")} />
                    <Stat label="Unavailable" value={stats.unavailable} tone="bad" active={stateFilter === "unavailable"} onClick={() => toggle("unavailable")} />
                    <Stat label="Shows" value={stats.expanders} active={stateFilter === "expander"} onClick={() => toggle("expander")} />
                    <Stat label="Total" value={stats.total} active={stateFilter === null} onClick={() => setStateFilter(null)} />
                </div>
            </div>

            {!enabled && (
                <div className="alert alert-warning" role="alert">
                    Watchtower is off. Enable it under Settings, Watchtower to start readying these items.
                    You can still add lists and items now.
                </div>
            )}

            {addFetcher.data && addFetcher.data.ok === false && (
                <div className="alert alert-danger" role="alert">Action failed: {addFetcher.data.error}</div>
            )}

            <section className={styles.panel}>
                <div className={styles.panelHead}>
                    <div className={styles.panelTitle}>Lists</div>
                    <div className={styles.panelHint}>
                        Any list that yields content ids: a Stremio catalog URL, a plain list URL, or
                        manual additions. They merge into one deduped wanted-set.
                    </div>
                </div>

                {sources.length === 0
                    ? <div className={styles.empty}>No lists yet. Add one below.</div>
                    : <div className={styles.list}>{sources.map(s => <SourceRow key={s.id} source={s} />)}</div>}

                <addFetcher.Form method="post" className={styles.addRow}>
                    <input type="hidden" name="action" value="add-source" />
                    <Form.Select name="kind" defaultValue="stremio-catalog" className={styles.selectSm}>
                        <option value="stremio-catalog">Stremio catalog</option>
                        <option value="url-list">URL list</option>
                    </Form.Select>
                    <Form.Control name="name" placeholder="Name (optional)" className={styles.selectSm} />
                    <Form.Control name="url" placeholder="https://addon/catalog/movie/xyz.json" className={styles.inputWide} />
                    <Form.Control name="cap" type="number" min={0} placeholder="cap" className={styles.inputSm} title="Per-list active cap (0 = use default)" />
                    <Form.Select name="seriesScope" defaultValue="" className={styles.selectSm} title="Series scope for this list">
                        {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </Form.Select>
                    <Button type="submit" variant="primary" disabled={addFetcher.state !== "idle"}>Add list</Button>
                </addFetcher.Form>

                <div className={styles.discover}>
                    <div className={styles.panelHint}>
                        Or paste a Stremio addon's <code>manifest.json</code> URL to see its catalogs and pick the ones you want.
                        Each catalog you add becomes its own list.
                    </div>
                    <discoverFetcher.Form method="post" className={styles.addRow}>
                        <input type="hidden" name="action" value="discover-catalogs" />
                        <Form.Control
                            name="url"
                            placeholder="https://addon.example.com/.../manifest.json"
                            className={styles.inputWide}
                        />
                        <Button type="submit" variant="outline-secondary" disabled={discoverFetcher.state !== "idle"}>
                            {discoverFetcher.state !== "idle" ? "Loading…" : "Discover catalogs"}
                        </Button>
                    </discoverFetcher.Form>

                    {discoverError && <div className="alert alert-danger" role="alert">{discoverError}</div>}

                    {discovered && !discoveryDismissed && (
                        <div className={styles.discoverResult}>
                            <div className={styles.discoverHead}>
                                <div className={styles.discoverTitle}>
                                    {discovered.addonName ? `${discovered.addonName} · ` : ""}
                                    {discovered.catalogs.length} catalog{discovered.catalogs.length === 1 ? "" : "s"} found
                                </div>
                                <div className={styles.discoverActions}>
                                    <button type="button" className={styles.linkBtn}
                                        onClick={() => setSelected(new Set(discovered.catalogs.map(c => c.url)))}>select all</button>
                                    <button type="button" className={styles.linkBtn}
                                        onClick={() => setSelected(new Set())}>select none</button>
                                    <button type="button" className={styles.linkBtn}
                                        onClick={() => setDiscoveryDismissed(true)}>close</button>
                                </div>
                            </div>

                            <div className={styles.catList}>
                                {discovered.catalogs.map(cat => (
                                    <label key={cat.url} className={styles.catRow}>
                                        <input
                                            type="checkbox"
                                            checked={selected.has(cat.url)}
                                            onChange={(e) => setSelected(prev => {
                                                const next = new Set(prev);
                                                if (e.target.checked) next.add(cat.url); else next.delete(cat.url);
                                                return next;
                                            })}
                                        />
                                        <span className={styles.kind}>{cat.type}</span>
                                        <span className={styles.catName}>{cat.name}</span>
                                        {cat.extraRequired && (
                                            <span className={styles.metaBad}
                                                title={`This catalog requires "${cat.extraRequired}"; the basic endpoint may return nothing.`}>
                                                needs {cat.extraRequired}
                                            </span>
                                        )}
                                        <span className={`${styles.url} ${styles.catUrl}`} title={cat.url}>{cat.url}</span>
                                    </label>
                                ))}
                            </div>

                            <div className={styles.discoverFoot}>
                                <bulkFetcher.Form method="post" className={styles.addRow}>
                                    <input type="hidden" name="action" value="add-sources" />
                                    <input type="hidden" name="sources" value={sourcesJson} readOnly />
                                    <Form.Select name="seriesScope" defaultValue="" className={styles.selectSm} title="Series scope for these lists">
                                        {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                                    </Form.Select>
                                    <Button type="submit" variant="primary"
                                        disabled={bulkFetcher.state !== "idle" || selected.size === 0}>
                                        {bulkFetcher.state !== "idle" ? "Adding…" : `Add ${selected.size} selected`}
                                    </Button>
                                </bulkFetcher.Form>
                                {bulkFetcher.data && bulkFetcher.data.ok === false && (
                                    <span className={styles.metaBad}>{bulkFetcher.data.error}</span>
                                )}
                            </div>
                        </div>
                    )}
                </div>
            </section>

            <section className={styles.panel}>
                <div className={styles.panelHead}>
                    <div className={styles.panelTitle}>Wanted</div>
                    <div className={styles.panelHint}>
                        Each item is searched once, the biggest healthy release is verified, then
                        re-checked over time. Add one manually by imdb id, or let your lists fill it.
                    </div>
                </div>

                <addFetcher.Form method="post" className={styles.addRow}>
                    <input type="hidden" name="action" value="add-item" />
                    <Form.Select name="type" defaultValue="movie" className={styles.selectSm}>
                        <option value="movie">movie</option>
                        <option value="series">series</option>
                    </Form.Select>
                    <Form.Control name="id" placeholder="tt0111161  (or tt0903747:1:2 for an episode)" className={styles.inputWide} />
                    <Form.Control name="title" placeholder="Title (optional)" className={styles.selectSm} />
                    <Button type="submit" variant="primary" disabled={addFetcher.state !== "idle"}>Add item</Button>
                </addFetcher.Form>

                {items.length > 0 && (
                    <div className={styles.toolbar}>
                        <Form.Control
                            value={query}
                            onChange={e => setQuery(e.target.value)}
                            placeholder="Search title or id…"
                            className={styles.search}
                        />
                        {filtering && (
                            <button type="button" className={styles.linkBtn}
                                onClick={() => { setQuery(""); setStateFilter(null); }}>clear</button>
                        )}
                    </div>
                )}

                {items.length === 0
                    ? <div className={styles.empty}>Nothing wanted yet.</div>
                    : nothingShown
                        ? <div className={styles.empty}>No items match.</div>
                        : <div className={styles.list}>
                            {visibleExpanders.map(({ ex, kids }) => (
                                <ExpanderGroup key={ex.key} expander={ex} episodes={kids} />
                            ))}
                            {visibleOrphans.map(it => <ItemRow key={it.key} item={it} />)}
                          </div>}
            </section>
        </div>
    );
}

function SourceRow({ source }: { source: WatchtowerSource }) {
    const fetcher = useFetcher();
    return (
        <div className={`${styles.row} ${source.enabled ? "" : styles.dimmed}`}>
            <div className={styles.rowMain}>
                <span className={styles.kind}>{source.kind}</span>
                <span className={styles.name}>{source.name}</span>
                {source.url && <span className={styles.url} title={source.url}>{source.url}</span>}
            </div>
            <div className={styles.rowActions}>
                {source.cap > 0 && <span className={styles.meta}>cap {source.cap}</span>}
                {source.lastSyncError
                    ? <span className={styles.metaBad} title={source.lastSyncError}>sync error</span>
                    : source.lastSyncedAtUnix
                        ? <span className={styles.metaOk}>synced {formatAge(source.lastSyncedAtUnix)}</span>
                        : <span className={styles.meta}>not synced yet</span>}
                {source.url && (
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="sync-source" />
                        <input type="hidden" name="id" value={source.id} />
                        <button type="submit" className={styles.linkBtn} disabled={fetcher.state !== "idle"}>sync now</button>
                    </fetcher.Form>
                )}
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="set-source-scope" />
                    <input type="hidden" name="id" value={source.id} />
                    <Form.Select
                        name="seriesScope"
                        defaultValue={source.seriesScope ?? ""}
                        className={styles.selectSm}
                        title="Series scope for this list"
                        onChange={(e) => e.currentTarget.form?.requestSubmit()}
                    >
                        {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </Form.Select>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="toggle-source" />
                    <input type="hidden" name="id" value={source.id} />
                    <input type="hidden" name="enabled" value={String(!source.enabled)} />
                    <button type="submit" className={styles.linkBtn}>{source.enabled ? "disable" : "enable"}</button>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="remove-source" />
                    <input type="hidden" name="id" value={source.id} />
                    <button type="submit" className={`${styles.linkBtn} ${styles.linkDanger}`}>remove</button>
                </fetcher.Form>
            </div>
        </div>
    );
}

function ItemRow({ item }: { item: WatchtowerItem }) {
    const fetcher = useFetcher<typeof action>();
    const pending = fetcher.formData?.get("action");
    const removing = pending === "remove-item";
    const checking = pending === "recheck-item";
    const error = fetcher.data && fetcher.data.ok === false ? fetcher.data.error : null;
    return (
        <div className={`${styles.row} ${removing ? styles.dimmed : ""}`}>
            <div className={styles.rowMain}>
                <StateChip state={item.state} />
                <div className={styles.itemTitleWrap}>
                    <div className={styles.itemTitle} title={item.title}>{item.title}</div>
                    <div className={styles.itemSub}>
                        <span className={styles.kind}>{item.type === "season" ? "season pack" : item.type}</span>
                        <span className={styles.mono}>{item.contentId}</span>
                        {item.provenanceCount > 1 && <span>on {item.provenanceCount} lists</span>}
                        {item.state === "ready" && <>
                            {item.winnerTitle && <span className={styles.rel} title={item.winnerTitle}>{item.winnerTitle}</span>}
                            <span>{formatBytes(item.winnerSize)} · {item.shortlistCount} pointer{item.shortlistCount === 1 ? "" : "s"}</span>
                            {item.lastVerifiedAtUnix && <span>verified {formatAge(item.lastVerifiedAtUnix)}</span>}
                            {item.nextCheckAtUnix && <span>re-checks {formatWhen(item.nextCheckAtUnix)}</span>}
                        </>}
                        {item.state === "unavailable" && <>
                            {item.failReason && <span>{item.failReason}</span>}
                            {item.nextCheckAtUnix && <span>retries {formatWhen(item.nextCheckAtUnix)}</span>}
                        </>}
                        {item.state === "scouting" && <span>searching…</span>}
                    </div>
                </div>
            </div>
            <div className={styles.rowActions}>
                {error && <span className={styles.metaBad} title={error}>failed — retry</span>}
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="recheck-item" />
                    <input type="hidden" name="key" value={item.key} />
                    <button type="submit" className={styles.linkBtn} disabled={fetcher.state !== "idle"}>{checking ? "checking…" : "check now"}</button>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="remove-item" />
                    <input type="hidden" name="key" value={item.key} />
                    <button type="submit" className={`${styles.linkBtn} ${styles.linkDanger}`} disabled={fetcher.state !== "idle"}>{removing ? "removing…" : "remove"}</button>
                </fetcher.Form>
            </div>
        </div>
    );
}

function ExpanderGroup({ expander, episodes }: { expander: WatchtowerItem, episodes: WatchtowerItem[] }) {
    const fetcher = useFetcher<typeof action>();
    const pending = fetcher.formData?.get("action");
    const removing = pending === "remove-item";
    const checking = pending === "recheck-item";
    const error = fetcher.data && fetcher.data.ok === false ? fetcher.data.error : null;
    const ready = episodes.filter(c => c.state === "ready").length;
    const sorted = [...episodes].sort((a, b) => a.contentId.localeCompare(b.contentId, undefined, { numeric: true }));
    return (
        <div className={styles.group}>
            <div className={`${styles.row} ${removing ? styles.dimmed : ""}`}>
                <div className={styles.rowMain}>
                    <span className={`${styles.chip} ${styles.chipShow}`}>Show</span>
                    <div className={styles.itemTitleWrap}>
                        <div className={styles.itemTitle} title={expander.title}>{expander.title}</div>
                        <div className={styles.itemSub}>
                            <span className={styles.mono}>{expander.contentId}</span>
                            <span>{episodes.length === 0 ? "expanding…" : `${episodes.length} tracked · ${ready} ready`}</span>
                        </div>
                    </div>
                </div>
                <div className={styles.rowActions}>
                    {error && <span className={styles.metaBad} title={error}>failed — retry</span>}
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="recheck-item" />
                        <input type="hidden" name="key" value={expander.key} />
                        <button type="submit" className={styles.linkBtn} disabled={fetcher.state !== "idle"}>{checking ? "checking…" : "check now"}</button>
                    </fetcher.Form>
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="remove-item" />
                        <input type="hidden" name="key" value={expander.key} />
                        <button type="submit" className={`${styles.linkBtn} ${styles.linkDanger}`} disabled={fetcher.state !== "idle"}>{removing ? "removing…" : "remove"}</button>
                    </fetcher.Form>
                </div>
            </div>
            {sorted.length > 0 && (
                <div className={styles.children}>
                    {sorted.map(c => <ItemRow key={c.key} item={c} />)}
                </div>
            )}
        </div>
    );
}

function StateChip({ state }: { state: string }) {
    const cls = state === "ready" ? styles.chipReady
        : state === "unavailable" ? styles.chipBad
        : state === "parked" ? styles.chipParked
        : state === "expander" ? styles.chipShow
        : styles.chipScouting;
    const label = state === "ready" ? "Ready"
        : state === "unavailable" ? "Unavailable"
        : state === "parked" ? "Parked"
        : state === "expander" ? "Show"
        : "Scouting";
    return <span className={`${styles.chip} ${cls}`}>{label}</span>;
}

function Stat({ label, value, tone, active, onClick }: { label: string, value: number, tone?: "ok" | "warn" | "bad", active?: boolean, onClick?: () => void }) {
    const toneClass = tone === "ok" ? styles.statOk
        : tone === "warn" ? styles.statWarn
        : tone === "bad" ? styles.statBad
        : "";
    return (
        <button type="button" className={`${styles.stat} ${active ? styles.statActive : ""}`} onClick={onClick}>
            <div className={`${styles.statValue} ${toneClass}`}>{value}</div>
            <div className={styles.statLabel}>{label}</div>
        </button>
    );
}

function formatBytes(bytes: number): string {
    if (bytes <= 0) return "-";
    const u = ["B", "KB", "MB", "GB", "TB"];
    let i = 0;
    let v = bytes;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2)} ${u[i]}`;
}

function formatAge(unixSeconds: number): string {
    const age = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (age < 5) return "just now";
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}

function formatWhen(unixSeconds: number): string {
    const d = Math.floor(unixSeconds - Date.now() / 1000);
    if (d <= 0) return "soon";
    if (d < 60) return `in ${d}s`;
    if (d < 3600) return `in ${Math.floor(d / 60)}m`;
    if (d < 86400) return `in ${Math.floor(d / 3600)}h`;
    return `in ${Math.floor(d / 86400)}d`;
}
