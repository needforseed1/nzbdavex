namespace NzbWebDAV.Services;

/// <summary>
/// Propagates one HTTP playback request into its background segment work.
/// Mutable diagnostics live on the request object itself; this scope only owns
/// the AsyncLocal lifetime.
/// </summary>
internal static class PlaybackDiagnosticContext
{
    private static readonly AsyncLocal<PlaybackRequestDiagnostics?> CurrentScope = new();

    public static PlaybackRequestDiagnostics? Current => CurrentScope.Value;

    public static IDisposable Begin(PlaybackRequestDiagnostics diagnostics)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = diagnostics;
        return new Scope(() => CurrentScope.Value = previous);
    }

    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
