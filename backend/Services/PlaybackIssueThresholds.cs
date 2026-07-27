namespace NzbWebDAV.Services;

/// <summary>
/// When a wait is large enough to be a plausible playback risk. Shared by the
/// Warning log level and the playback page's source-health status so the two
/// surfaces use the same threshold without claiming the player definitely
/// buffered.
///
/// Only the wait thresholds are shared; the two surfaces do not agree on
/// everything, by design. The page answers "did the viewer suffer", so
/// successful recovery is neutral there. The log answers "did the server have
/// to work around something", so a recovered wedged connection warns. See
/// PlaybackRequestDiagnostics.CompletionLogLevel.
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

    public static bool StallsMatter(int count, long maxMs) =>
        count >= StallMinCount || maxMs >= StallMinMs;

    public static bool WaitsMatter(int count, long maxMs) =>
        count >= WaitMinCount || maxMs >= WaitMinMs;
}
