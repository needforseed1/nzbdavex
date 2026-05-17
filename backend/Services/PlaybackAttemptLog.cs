using System.Collections.Concurrent;

namespace NzbWebDAV.Services;

/// <summary>
/// In-memory ring buffer of recent playback fallback attempts. Surfaced via
/// the Watchdog settings tab so users can see what was tried, what failed,
/// and what won.
/// </summary>
public class PlaybackAttemptLog
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<Attempt> _attempts = new();

    public void Record(Attempt attempt)
    {
        _attempts.Enqueue(attempt);
        while (_attempts.Count > Capacity && _attempts.TryDequeue(out _)) { }
    }

    public IReadOnlyList<Attempt> GetRecent(int limit)
    {
        var snapshot = _attempts.ToArray();
        Array.Reverse(snapshot);
        return snapshot.Take(Math.Max(1, limit)).ToList();
    }

    public class Attempt
    {
        public required Guid ClickId { get; init; }
        public required DateTimeOffset AttemptedAt { get; init; }
        public required string ContentType { get; init; }
        public required string RequestedTitle { get; init; }
        public required string CandidateTitle { get; init; }
        public required string IndexerName { get; init; }
        public required long Size { get; init; }
        public required int RankIndex { get; init; }
        public required Outcome Result { get; init; }
        public string? FailReason { get; init; }
        public int DurationMs { get; init; }
        public bool IsWinner { get; init; }
        public string? ProviderHost { get; init; }
    }

    public enum Outcome
    {
        PreVerifyAvailable,
        PreVerifyDead,
        PreVerifyTimeout,
        Cancelled,
        EnqueueFailed,
        QueueFailed,
        QueueCompleted,
        BudgetTimeout,
        ExcludedByPattern,
    }
}
