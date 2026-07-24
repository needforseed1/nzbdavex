using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class PlaybackDiagnosticContextTests
{
    [Fact]
    public async Task Context_FlowsIntoBackgroundWorkAndRestoresPreviousScope()
    {
        var diagnostics = CreateDiagnostics();

        Assert.Null(PlaybackDiagnosticContext.Current);
        using (PlaybackDiagnosticContext.Begin(diagnostics))
        {
            Assert.Same(diagnostics, PlaybackDiagnosticContext.Current);
            var flowed = await Task.Run(() => PlaybackDiagnosticContext.Current);
            Assert.Same(diagnostics, flowed);
        }
        Assert.Null(PlaybackDiagnosticContext.Current);
    }

    [Fact]
    public async Task TransferPump_SeparatelyCountsSlowUpstreamAndDownstream()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        await using var source = new DelayedReadStream([1, 2, 3, 4], TimeSpan.FromMilliseconds(10));
        await using var destination = new DelayedWriteStream(TimeSpan.FromMilliseconds(10));

        await PlaybackTransferPump.CopyAsync(
            source,
            destination,
            diagnostics,
            startOffset: 5,
            endOffset: null,
            seekSource: false,
            onBytesServed: null,
            onSourceError: null,
            CancellationToken.None);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(4, snapshot.BytesServed);
        Assert.Equal(9, snapshot.CurrentOffset);
        Assert.Equal(1, snapshot.UpstreamStalls);
        Assert.Equal(1, snapshot.DownstreamStalls);
        Assert.True(snapshot.MaxUpstreamStallMs >= 1);
        Assert.True(snapshot.MaxDownstreamStallMs >= 1);
    }

    [Fact]
    public void Snapshot_SummarizesBackupCacheAndPermitActivity()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));

        diagnostics.RecordBackupAttempt(
            "backup-id", "backup.example", "segment", "primary.example:timeout");
        diagnostics.RecordBackupOutcome(
            "backup-id", "backup.example", "segment", "rescued", 42);
        diagnostics.RecordFallbackRescue(
            "backup.example", "segment", "primary.example:timeout", 42);
        diagnostics.RecordCacheHit();
        diagnostics.RecordCacheHit();
        diagnostics.RecordCacheMiss();
        diagnostics.RecordConnectionPermitWait(12, "High", "acquired");
        diagnostics.RecordProviderPoolWait(
            "primary.example", 18, "acquired", 10, 9, 1, 2);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(1, snapshot.FallbackRescues);
        Assert.Equal(2, snapshot.CacheHits);
        Assert.Equal(1, snapshot.CacheMisses);
        Assert.Equal(1, snapshot.ConnectionPermitWaits);
        Assert.Equal(12, snapshot.MaxConnectionPermitWaitMs);
        Assert.Equal(1, snapshot.ProviderPoolWaits);
        Assert.Equal(18, snapshot.MaxProviderPoolWaitMs);
        Assert.Contains(
            "backup.example:attempts=1,rescued=1,missing=0,timeouts=0,errors=0",
            snapshot.BackupSummary);
    }

    private static PlaybackRequestDiagnostics CreateDiagnostics(TimeSpan? stallThreshold = null) =>
        new(
            Guid.NewGuid(),
            "/media/test.mkv",
            "test.mkv",
            requestedRange: null,
            stallThreshold: stallThreshold);

    private sealed class DelayedReadStream(byte[] bytes, TimeSpan delay) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class DelayedWriteStream(TimeSpan delay) : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
