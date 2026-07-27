import styles from "./playback-card.module.css";

export function PlaybackStat({
    label,
    value,
    title,
}: {
    label: string,
    value: string,
    title?: string,
}) {
    return (
        <span className={styles.statBox} title={title}>
            <span className={styles.statLabel}>{label}</span>
            <span className={styles.statValue}>{value}</span>
        </span>
    );
}
