using Serilog.Events;

namespace NzbWebDAV.Services;

/// <summary>
/// Decides whether recorded playback evidence is significant enough to surface.
/// Presentation can differ between logs and the playback page, but both consume
/// the same source-pressure thresholds.
/// </summary>
internal static class PlaybackOutcomeClassifier
{
    public static PlaybackSourcePressure ClassifySourcePressure(
        int upstreamStalls,
        long maxUpstreamStallMs,
        int connectionPermitWaits,
        long maxConnectionPermitWaitMs,
        int providerPoolWaits,
        long maxProviderPoolWaitMs) =>
        new(
            PlaybackIssueThresholds.StallsMatter(
                upstreamStalls,
                maxUpstreamStallMs),
            PlaybackIssueThresholds.WaitsMatter(
                connectionPermitWaits,
                maxConnectionPermitWaitMs),
            PlaybackIssueThresholds.WaitsMatter(
                providerPoolWaits,
                maxProviderPoolWaitMs));

    /// <summary>
    /// Clean completions and slow downstream writes are normal playback
    /// lifecycle/pacing events. A completion warning means the source had
    /// actionable pressure, bad data was served, a body connection wedged, or
    /// the request failed terminally.
    /// </summary>
    public static LogEventLevel CompletionLogLevel(
        PlaybackDiagnosticSnapshot snapshot,
        string reason,
        Exception? exception)
    {
        var terminalFailure =
            exception is not null ||
            reason.Equals("timeout", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("error", StringComparison.OrdinalIgnoreCase);
        var servedBadData =
            snapshot.ZeroFilledSegments > 0 ||
            snapshot.BodyStallRecoveries > 0;
        var sourcePressure = ClassifySourcePressure(
            snapshot.UpstreamStalls,
            snapshot.MaxUpstreamStallMs,
            snapshot.ConnectionPermitWaits,
            snapshot.MaxConnectionPermitWaitMs,
            snapshot.ProviderPoolWaits,
            snapshot.MaxProviderPoolWaitMs);

        return terminalFailure || servedBadData || sourcePressure.Any
            ? LogEventLevel.Warning
            : LogEventLevel.Information;
    }
}
