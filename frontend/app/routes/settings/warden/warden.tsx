import { Alert, Button, Form, Spinner } from "react-bootstrap";
import { type ChangeEvent, type DragEvent, type Dispatch, type SetStateAction, useEffect, useRef, useState } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";

type WardenSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

type Status = { text: string; variant: "success" | "danger" } | null;
type Busy = null | "import" | "clear";

export function WardenSettings({ config, setNewConfig }: WardenSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const hideDead = (config["warden.hide-dead"] ?? "true") === "true";

    const [count, setCount] = useState<number | null>(null);
    const [busy, setBusy] = useState<Busy>(null);
    const [message, setMessage] = useState<Status>(null);
    const [dragOver, setDragOver] = useState(false);
    const [showClear, setShowClear] = useState(false);
    const fileRef = useRef<HTMLInputElement>(null);

    const hasEntries = count !== null && count > 0;

    const refreshCount = async () => {
        try {
            const res = await fetch("/api/get-warden");
            if (res.ok) {
                const data = await res.json();
                setCount(data.count ?? 0);
            }
        } catch {
            setCount(null);
        }
    };

    useEffect(() => { refreshCount(); }, []);

    // Success messages clear themselves; errors stick until dismissed.
    useEffect(() => {
        if (message?.variant !== "success") return;
        const t = setTimeout(() => setMessage(null), 4000);
        return () => clearTimeout(t);
    }, [message]);

    const importFile = async (file: File) => {
        setBusy("import");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("file", file);
            const res = await fetch("/api/warden-import", { method: "POST", body: form });
            const data = await res.json().catch(() => ({}));
            if (res.ok) {
                setMessage({ text: `Imported ${(data.added ?? 0).toLocaleString()} new (total ${(data.total ?? 0).toLocaleString()}).`, variant: "success" });
                await refreshCount();
            } else {
                setMessage({ text: data.error || "Import failed.", variant: "danger" });
            }
        } catch (err: any) {
            setMessage({ text: err?.message || "Import failed.", variant: "danger" });
        } finally {
            setBusy(null);
            if (fileRef.current) fileRef.current.value = "";
        }
    };

    const onImportInput = (e: ChangeEvent<HTMLInputElement>) => {
        const file = e.target.files?.[0];
        if (file) importFile(file);
    };

    const onDragOver = (e: DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        if (!busy) setDragOver(true);
    };
    const onDragLeave = (e: DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        setDragOver(false);
    };
    const onDrop = (e: DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        setDragOver(false);
        if (busy) return;
        const file = e.dataTransfer.files?.[0];
        if (file) importFile(file);
    };

    const onClear = async () => {
        setShowClear(false);
        setBusy("clear");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("action", "clear");
            const res = await fetch("/api/warden-import", { method: "POST", body: form });
            const data = await res.json().catch(() => ({}));
            if (res.ok) {
                setMessage({ text: `Cleared ${(data.cleared ?? 0).toLocaleString()}.`, variant: "success" });
                await refreshCount();
            } else {
                setMessage({ text: data.error || "Clear failed.", variant: "danger" });
            }
        } catch (err: any) {
            setMessage({ text: err?.message || "Clear failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    return (
        <div style={{ padding: 16, maxWidth: 720 }}>
            <div style={{ marginBottom: 16 }}>
                <div style={{ fontSize: 18, fontWeight: 600 }}>Warden</div>
                <div style={{ opacity: 0.7, fontSize: 14 }}>
                    A portable filter list. It holds fingerprints and keeps anything matching them out
                    of your search-profile results. The fingerprints are universal: identical on any
                    provider, indexer, or server, and free of credentials. It fills in automatically
                    over time. The exported file drops into any other config or server and just works.
                </div>
            </div>

            <Form.Group style={{ marginBottom: 16 }}>
                <Form.Check
                    type="switch"
                    id="warden-hide-dead"
                    label="Filter out anything on the list"
                    checked={hideDead}
                    onChange={e => set("warden.hide-dead", String(e.target.checked))} />
                <Form.Text muted>
                    When on, anything whose fingerprint is on the list is removed from what your search
                    profiles return. If everything matches, results are shown anyway as a last resort.
                </Form.Text>
            </Form.Group>

            <hr />

            <div style={{ marginBottom: 12, fontWeight: 600 }}>
                {count === null
                    ? "Fingerprints on the list: …"
                    : count === 0
                        ? "The list is empty. It fills in automatically as unusable items are found."
                        : `Fingerprints on the list: ${count.toLocaleString()}`}
            </div>

            <div
                onDragOver={onDragOver}
                onDragLeave={onDragLeave}
                onDrop={onDrop}
                style={{
                    border: `1px dashed ${dragOver ? "#6ea8fe" : "rgba(128,128,128,0.45)"}`,
                    borderRadius: 8,
                    padding: 12,
                    background: dragOver ? "rgba(110,168,254,0.10)" : "transparent",
                    transition: "border-color 120ms ease, background 120ms ease",
                }}>
                <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                    <Button href="/api/warden-export" variant="secondary" size="sm" disabled={!hasEntries}>
                        Export
                    </Button>
                    <Button variant="secondary" size="sm" disabled={busy !== null} onClick={() => fileRef.current?.click()}>
                        {busy === "import"
                            ? <><Spinner as="span" animation="border" size="sm" /> Importing…</>
                            : "Import"}
                    </Button>
                    <input
                        ref={fileRef}
                        type="file"
                        accept=".gz,.ndjson,.json,application/gzip,application/json"
                        style={{ display: "none" }}
                        onChange={onImportInput} />
                    <Button variant="outline-danger" size="sm" disabled={busy !== null || !hasEntries} onClick={() => setShowClear(true)}>
                        {busy === "clear"
                            ? <><Spinner as="span" animation="border" size="sm" /> Clearing…</>
                            : "Clear"}
                    </Button>
                </div>
                <div style={{ marginTop: 8, fontSize: 13, opacity: dragOver ? 0.95 : 0.55 }}>
                    {dragOver ? "Drop to import" : "…or drag & drop a warden file here to import."}
                </div>
            </div>

            {message &&
                <Alert
                    variant={message.variant}
                    dismissible
                    onClose={() => setMessage(null)}
                    style={{ marginTop: 12, marginBottom: 0, fontSize: 14, padding: "8px 12px" }}>
                    {message.text}
                </Alert>}

            <ConfirmModal
                show={showClear}
                title="Clear the Warden list?"
                message={`This permanently removes all ${(count ?? 0).toLocaleString()} fingerprint${count === 1 ? "" : "s"} from the list. The list will repopulate automatically over time as unusable items are found.`}
                cancelText="Cancel"
                confirmText="Clear list"
                onCancel={() => setShowClear(false)}
                onConfirm={onClear} />
        </div>
    );
}

export function isWardenSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["warden.hide-dead"] !== newConfig["warden.hide-dead"];
}
