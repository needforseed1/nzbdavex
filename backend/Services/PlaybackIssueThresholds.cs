namespace NzbWebDAV.Services;

/// <summary>
/// Thresholds for actionable source pressure. Request logs use counts and the
/// worst wait because an in-flight request has no final duration. Completed
/// playback history can be more selective by comparing non-overlapping wait
/// time with the duration of the activity.
///
/// Only the wait thresholds are shared; the two surfaces do not agree on
/// everything, by design. The page answers "did the viewer suffer", so
/// successful recovery is neutral there. The log answers "did the server have
/// to work around something", so a recovered wedged connection warns. See
/// <see cref="PlaybackOutcomeClassifier"/>.
/// </summary>
public static class PlaybackIssueThresholds
{
    /// <summary>
    /// A single one-second wait on usenet is not worth flagging as a source
    /// issue — players usually buffer ahead of the playhead. Flag only a
    /// repeated pattern or one long enough to pose a playback risk.
    /// </summary>
    public const int StallMinCount = 3;

    public const int StallMinMs = 3_000;

    /// <summary>
    /// A wait for a connection only matters if it outlasted the buffer feeding
    /// the player. Observed clean: two waits, worst 1.4 s, alongside zero
    /// upstream stalls and a source running 28% ahead of the playhead — the
    /// viewer saw nothing, yet the play was marked as having a source issue.
    /// </summary>
    public const int WaitMinCount = 5;

    public const int WaitMinMs = 3_000;

    /// <summary>
    /// Completed history distinguishes a source delay from a plausible playback
    /// interruption. A count alone is deliberately insufficient: three waits in
    /// thirty seconds and three waits in a two-hour film are not equivalent.
    /// </summary>
    public const int PlaybackRiskMinContinuousWaitMs = 10_000;
    public const int PlaybackRiskMinWallWaitMs = 3_000;
    public const int PlaybackRiskMinWaitPercent = 10;

    public static bool StallsMatter(int count, long maxMs) =>
        count >= StallMinCount || maxMs >= StallMinMs;

    public static bool WaitsMatter(int count, long maxMs) =>
        count >= WaitMinCount || maxMs >= WaitMinMs;

    public static bool PlaybackRisk(
        long activeMs,
        long upstreamWaitWallMs,
        long maxUpstreamWaitWallMs) =>
        maxUpstreamWaitWallMs >= PlaybackRiskMinContinuousWaitMs
        || (activeMs > 0
            && upstreamWaitWallMs >= PlaybackRiskMinWallWaitMs
            && upstreamWaitWallMs * 100
            >= activeMs * PlaybackRiskMinWaitPercent);
}
