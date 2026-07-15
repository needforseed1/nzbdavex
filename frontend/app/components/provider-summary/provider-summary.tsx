import { OverlayTrigger, Popover } from "react-bootstrap";
import styles from "./provider-summary.module.css";

export type ProviderSummaryItem = {
    key: string,
    label: string,
    host: string,
    amount?: string,
    share?: string,
}

export type ProviderSummaryProps = {
    items: ProviderSummaryItem[],
    heading?: string,
    meta?: string,
}

const MAX_INLINE_PROVIDERS = 2;

export function ProviderSummary({ items, heading = "Provider usage", meta }: ProviderSummaryProps) {
    if (items.length === 0) return null;

    const visible = items.slice(0, MAX_INLINE_PROVIDERS);
    const hidden = items.length - visible.length;
    const accessibleSummary = items
        .map(item => [item.label, item.amount, item.share].filter(Boolean).join(" "))
        .join(", ");

    const overlay = (
        <Popover className={styles.popover}>
            <div className={styles.popoverContent}>
                <div className={styles.popoverHeader}>
                    <span>{heading}</span>
                    {meta && <span>{meta}</span>}
                </div>
                <div className={styles.popoverRows}>
                    {items.map(item => (
                        <div key={item.key} className={styles.popoverRow}>
                            <div className={styles.popoverIdentity}>
                                <span>{item.label}</span>
                                <span>{item.host}</span>
                            </div>
                            {(item.amount || item.share) && (
                                <div className={styles.popoverStats}>
                                    {item.amount && <span>{item.amount}</span>}
                                    {item.share && <span>{item.share}</span>}
                                </div>
                            )}
                        </div>
                    ))}
                </div>
            </div>
        </Popover>
    );

    return (
        <OverlayTrigger
            placement="auto"
            overlay={overlay}
            trigger={["hover", "focus", "click"]}
            rootClose>
            <button
                type="button"
                className={styles.summary}
                aria-label={`${heading}: ${accessibleSummary}`}
                onClick={event => event.stopPropagation()}>
                <span className={styles.entries} aria-hidden="true">
                    {visible.map((item, index) => (
                        <span key={item.key} className={styles.entry}>
                            {index > 0 && <span className={styles.separator}>·</span>}
                            <span className={styles.label}>{item.label}</span>
                            {item.share && <span className={styles.share}>{item.share}</span>}
                        </span>
                    ))}
                </span>
                {hidden > 0 && <span className={styles.more}>+{hidden}</span>}
            </button>
        </OverlayTrigger>
    );
}
