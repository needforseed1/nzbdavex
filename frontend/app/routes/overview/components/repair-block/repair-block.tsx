import styles from "./repair-block.module.css";
import { formatNumber } from "../../utils/format";

export type RepairBlockProps = {
    repair: {
        healthy: number,
        repaired: number,
        deleted: number,
        actionNeeded: number,
    },
}

export function RepairBlock({ repair }: RepairBlockProps) {
    const total = repair.healthy + repair.repaired + repair.deleted + repair.actionNeeded;
    const pct = (n: number) => total > 0 ? (n / total) * 100 : 0;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Repair &amp; health</h3>
                <div className={styles.sub}>Last 30 days</div>
            </div>

            <div className={styles.bar}>
                <div className={styles.barFillHealthy} style={{ width: `${pct(repair.healthy)}%` }} title={`Healthy: ${formatNumber(repair.healthy)}`} />
                <div className={styles.barFillRepaired} style={{ width: `${pct(repair.repaired)}%` }} title={`Repaired: ${formatNumber(repair.repaired)}`} />
                <div className={styles.barFillDeleted} style={{ width: `${pct(repair.deleted)}%` }} title={`Deleted: ${formatNumber(repair.deleted)}`} />
                <div className={styles.barFillAction} style={{ width: `${pct(repair.actionNeeded)}%` }} title={`Action needed: ${formatNumber(repair.actionNeeded)}`} />
            </div>

            <div className={styles.legend}>
                <Item color="healthy" label="Healthy" value={repair.healthy} />
                <Item color="repaired" label="Repaired" value={repair.repaired} />
                <Item color="deleted" label="Deleted" value={repair.deleted} />
                <Item color="action" label="Needs action" value={repair.actionNeeded} />
            </div>
        </div>
    );
}

function Item({ color, label, value }: { color: "healthy" | "repaired" | "deleted" | "action", label: string, value: number }) {
    const dotClass = {
        healthy: styles.dotHealthy,
        repaired: styles.dotRepaired,
        deleted: styles.dotDeleted,
        action: styles.dotAction,
    }[color];
    return (
        <div className={styles.item}>
            <span className={`${styles.dot} ${dotClass}`} />
            <span className={styles.itemLabel}>{label}</span>
            <span className={styles.itemValue}>{formatNumber(value)}</span>
        </div>
    );
}
