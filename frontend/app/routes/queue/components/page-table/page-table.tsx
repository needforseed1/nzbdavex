import { Table } from "react-bootstrap";
import styles from "./page-table.module.css";
import type { ReactNode } from "react";
import { TriCheckbox, type TriCheckboxState } from "../tri-checkbox/tri-checkbox";
import { Truncate } from "../truncate/truncate";
import { StatusBadge } from "../status-badge/status-badge";
import { formatFileSize } from "~/utils/file-size";
import { classNames } from "~/utils/styling";
import type { ProviderUsage } from "~/clients/backend-client.server";
import { ProviderSummary } from "~/components/provider-summary/provider-summary";

export type PageTableProps = {
    children?: ReactNode,
    headerCheckboxState: TriCheckboxState,
    onHeaderCheckboxChange: (isChecked: boolean) => void,
    footer?: ReactNode,
}

export function PageTable({ children, headerCheckboxState, onHeaderCheckboxChange, footer }: PageTableProps) {
    return (
        <div className={styles.tableContainer}>
            <Table className={styles.table}>
                <thead>
                    <tr>
                        <th>
                            <TriCheckbox state={headerCheckboxState} onChange={onHeaderCheckboxChange}>
                                Name
                            </TriCheckbox>
                        </th>
                        <th className={styles.desktop}>Category</th>
                        <th className={styles.desktop}>Indexer</th>
                        <th className={styles.desktop}>Provider</th>
                        <th className={styles.desktop}>Status</th>
                        <th className={styles.desktop}>Size</th>
                        <th>Actions</th>
                    </tr>
                </thead>
                <tbody>
                    {children}
                </tbody>
            </Table>
            {footer &&
                <div className={styles.footer}>{footer}</div>
            }
        </div>
    );
}

export type PageRowProps = {
    isUploading?: boolean,
    isSelected: boolean,
    isRemoving: boolean,
    name: string,
    category: string,
    status: string,
    percentage?: string,
    error?: string,
    fileSizeBytes: number,
    actions: ReactNode,
    indexer?: string | null,
    providers?: ProviderUsage[] | null,
    onRowSelectionChanged: (isSelected: boolean) => void
}
export function PageRow(props: PageRowProps) {
    const rowStyles = [
        props.isRemoving && styles.removing,
        props.isUploading && styles.uploading
    ];

    return (
        <tr className={classNames(rowStyles)}>
            <td>
                <TriCheckbox state={props.isSelected} onChange={props.onRowSelectionChanged}>
                    <Truncate>{props.name}</Truncate>
                    <div className={styles.mobile}>
                        <div className={styles.badges}>
                            <StatusBadge status={props.status} percentage={props.percentage} error={props.error} />
                            <CategoryBadge category={props.category} />
                            {props.indexer && <IndexerBadge indexer={props.indexer} />}
                            {props.providers && props.providers.length > 0 && <ProvidersBadge providers={props.providers} />}
                        </div>
                        <div>{formatFileSize(props.fileSizeBytes)}</div>
                    </div>
                </TriCheckbox>
            </td>
            <td className={styles.desktop}>
                <CategoryBadge category={props.category} />
            </td>
            <td className={styles.desktop}>
                {props.indexer ? <IndexerBadge indexer={props.indexer} /> : <span className={styles.emptyCell}>—</span>}
            </td>
            <td className={styles.desktop}>
                {props.providers && props.providers.length > 0
                    ? <ProvidersBadge providers={props.providers} />
                    : <span className={styles.emptyCell}>—</span>}
            </td>
            <td className={styles.desktop}>
                <StatusBadge status={props.status} percentage={props.percentage} error={props.error} />
            </td>
            <td className={styles.desktop}>
                {formatFileSize(props.fileSizeBytes)}
            </td>
            <td>
                <div className={styles.actions}>
                    {props.actions}
                </div>
            </td>
        </tr>
    );
}

export function CategoryBadge({ category }: { category: string }) {
    const categoryLower = category?.toLowerCase();
    return <div className={styles.categoryBadge}>{categoryLower}</div>
}

export function IndexerBadge({ indexer }: { indexer: string }) {
    return <div className={styles.indexerBadge} title={`Indexer: ${indexer}`}>via {indexer}</div>
}

export function ProvidersBadge({ providers }: { providers: ProviderUsage[] }) {
    if (providers.length === 0) return null;
    const total = providers.reduce((acc, p) => acc + p.segments, 0);
    const activeCount = providers.filter(p => p.segments > 0).length;
    const labelOf = (p: ProviderUsage) => p.nickname?.trim() || stripHost(p.host);
    const items = providers.map((provider, index) => {
        const percentage = total > 0
            ? `${Math.round((provider.segments / total) * 100)}%`
            : "idle";
        return {
            key: `${provider.host}-${index}`,
            label: labelOf(provider),
            host: provider.host,
            amount: `${provider.segments} article${provider.segments === 1 ? "" : "s"}`,
            share: percentage,
        };
    });

    return (
        <ProviderSummary
            items={items}
            meta={`${total} articles · ${activeCount}/${providers.length} active`}
        />
    );
}

// Generic NNTP hostname prefixes that aren't brand-identifying.
const GENERIC_HOST_PREFIXES = new Set(["news", "reader", "premium", "secure", "ssl", "nntp", "usenet", "block"]);

function stripHost(host: string): string {
    if (!host) return "—";
    const cleanHost = host.replace(/\s*\(\d+%\)\s*$/, "").replace(/:\d+$/, "");
    const labels = cleanHost.split(".").filter(Boolean);
    if (labels.length === 0) return host;
    if (labels.length === 1) return labels[0];
    const identifying = labels.find(label => !GENERIC_HOST_PREFIXES.has(label.toLowerCase()));
    if (identifying) return identifying;
    // pick whichever of the first two is longer (heuristic for "more identifying")
    return labels[0].length >= labels[1].length ? labels[0] : labels[1];
}
