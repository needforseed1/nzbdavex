import { useCallback } from "react";
import styles from "./empty-queue.module.css"

interface EmptyQueueProps {
    onUploadClicked?: () => void;
}

export function EmptyQueue(props: EmptyQueueProps) {
    const onUploadClicked = useCallback(() => {
        props.onUploadClicked?.call(null);
    }, [props.onUploadClicked]);

    return (
        <div className={styles.emptyState}>
            {/* The panel heading already says "Queue"; this only needs to say
                what to do next. */}
            <div className={styles.emptyCopy}>Drop .nzb files here to start a download</div>
            <button type="button" className={styles.emptyAction} onClick={onUploadClicked}>
                Choose files
            </button>
        </div>
    );
}
