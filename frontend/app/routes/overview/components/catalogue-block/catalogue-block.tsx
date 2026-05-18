import styles from "./catalogue-block.module.css";
import { formatBytes, formatNumber, formatPercent } from "../../utils/format";

export type CatalogueBlockProps = {
    catalogue: {
        fileCount: number,
        totalBytes: number,
        checkedPercent: number,
        repairBacklog: number,
    },
}

export function CatalogueBlock({ catalogue }: CatalogueBlockProps) {
    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Catalogue</h3>
                <div className={styles.sub}>Files and coverage</div>
            </div>

            <div className={styles.grid}>
                <Cell label="Files" value={formatNumber(catalogue.fileCount)} />
                <Cell label="Total size" value={formatBytes(catalogue.totalBytes)} />
                <Cell label="Checked 30d" value={formatPercent(catalogue.checkedPercent, 1)} />
                <Cell label="Repair backlog" value={formatNumber(catalogue.repairBacklog)} accent={catalogue.repairBacklog > 0 ? "warn" : undefined} />
            </div>
        </div>
    );
}

function Cell({ label, value, accent }: { label: string, value: string, accent?: "warn" }) {
    return (
        <div className={`${styles.cell} ${accent === "warn" ? styles.warn : ""}`}>
            <div className={styles.label}>{label}</div>
            <div className={styles.value}>{value}</div>
        </div>
    );
}
