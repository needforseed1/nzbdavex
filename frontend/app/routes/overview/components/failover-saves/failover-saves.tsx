import { useMemo } from "react";
import styles from "./failover-saves.module.css";
import type { FailoverBlock, OverviewWindow } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type FailoverSavesProps = {
    failover: FailoverBlock,
    window: OverviewWindow,
}

export function FailoverSaves({ failover, window }: FailoverSavesProps) {
    const { articlesRecovered, readsSaved, providers } = failover;

    const maxSaves = useMemo(
        () => Math.max(1, ...providers.map(p => p.saves)),
        [providers],
    );

    const hasData = articlesRecovered > 0 && providers.length > 0;
    const sinceLabel = window === "all" ? "all time" : `last ${window}`;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Failover saves</h3>
                <div className={styles.sub}>Articles a backup provider rescued, {sinceLabel}</div>
            </div>

            {hasData ? (
                <>
                    <div className={styles.hero}>
                        <div className={styles.heroMain}>
                            <span className={styles.heroNum}>{formatNumber(articlesRecovered)}</span>
                            <span className={styles.heroLabel}>articles recovered</span>
                        </div>
                        {readsSaved > 0 && (
                            <div className={styles.heroAside}>
                                <span className={styles.heroAsideNum}>{formatNumber(readsSaved)}</span>
                                <span className={styles.heroAsideLabel}>
                                    {readsSaved === 1 ? "read would've failed" : "reads would've failed"}
                                </span>
                            </div>
                        )}
                    </div>

                    <div className={styles.rankHead}>Rescued by</div>
                    <div className={styles.ranking}>
                        {providers.map(p => {
                            const pct = (p.saves / maxSaves) * 100;
                            return (
                                <div key={p.provider} className={styles.row} title={p.provider}>
                                    <span className={styles.name}>{p.nickname?.trim() || p.provider}</span>
                                    <span className={styles.track}>
                                        <span className={styles.base} />
                                        <span className={styles.stem} style={{ width: `${pct.toFixed(1)}%` }} />
                                        <span className={styles.dot} style={{ left: `${pct.toFixed(1)}%` }} />
                                    </span>
                                    <span className={styles.value}>{formatNumber(p.saves)}</span>
                                </div>
                            );
                        })}
                    </div>
                </>
            ) : (
                <div className={styles.empty}>
                    No failover saves in this window.
                    <div className={styles.emptySub}>
                        Every article was served on the first try. When a provider misses, a backup steps in
                        — and you'll see which one rescued what here.
                    </div>
                </div>
            )}
        </div>
    );
}
