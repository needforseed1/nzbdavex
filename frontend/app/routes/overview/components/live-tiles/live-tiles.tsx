import styles from "./live-tiles.module.css";
import { formatBytes } from "../../utils/format";

export type LiveTilesProps = {
    tiles: {
        activeReads: number,
        articlesPerMinute: number,
        errorsPerMinute: number,
        bytesServedPerMinute: number,
    },
}

export function LiveTiles({ tiles }: LiveTilesProps) {
    const bytesPerSec = tiles.bytesServedPerMinute / 60;
    const articlesPerSec = tiles.articlesPerMinute / 60;
    return (
        <div className={styles.grid}>
            <Tile
                label="Active reads"
                value={tiles.activeReads.toString()}
                accent={tiles.activeReads > 0 ? "live" : undefined}
            />
            <Tile
                label="Articles / s"
                value={articlesPerSec >= 10 ? articlesPerSec.toFixed(0) : articlesPerSec.toFixed(1)}
                sub={`${tiles.articlesPerMinute.toLocaleString()} / min`}
            />
            <Tile
                label="Read throughput"
                value={formatBytes(bytesPerSec) + "/s"}
                sub={`${formatBytes(tiles.bytesServedPerMinute)} / min`}
            />
            <Tile
                label="Fetch errors"
                value={tiles.errorsPerMinute.toString()}
                sub="last minute"
                accent={tiles.errorsPerMinute > 0 ? "danger" : undefined}
            />
        </div>
    );
}

function Tile({ label, value, sub, accent }: {
    label: string,
    value: string,
    sub?: string,
    accent?: "live" | "danger"
}) {
    return (
        <div className={`${styles.tile} ${accent === "live" ? styles.tileLive : ""} ${accent === "danger" ? styles.tileDanger : ""}`}>
            <div className={styles.label}>{label}</div>
            <div className={styles.value}>{value}</div>
            {sub && <div className={styles.sub}>{sub}</div>}
        </div>
    );
}
