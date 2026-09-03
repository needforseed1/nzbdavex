export type WatchdogSearchAttempt = {
    requestedTitle: string,
    candidateTitle: string,
    indexerName: string,
    failReason?: string | null,
};

export function matchesWatchdogSearch(attempts: WatchdogSearchAttempt[], rawQuery: string): boolean {
    const query = rawQuery.trim().toLocaleLowerCase();
    if (!query) return true;
    return attempts.some(attempt => [
        attempt.requestedTitle,
        attempt.candidateTitle,
        attempt.indexerName,
        attempt.failReason ?? "",
    ].some(value => value.toLocaleLowerCase().includes(query)));
}
