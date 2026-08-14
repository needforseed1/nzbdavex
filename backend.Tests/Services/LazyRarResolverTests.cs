using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class LazyRarResolverTests
{
    [Theory]
    [InlineData(64, null, 64)]
    [InlineData(64, "secret", 1)]
    [InlineData(2, "secret", 1)]
    [InlineData(0, null, 1)]
    public void PasswordedRarResolutionCapsCpuWithoutSlowingOrdinaryArchives(
        int configuredConcurrency,
        string? archivePassword,
        int expected)
    {
        Assert.Equal(expected,
            LazyRarResolver.GetResolutionConcurrency(configuredConcurrency, archivePassword));
    }

    [Fact]
    public async Task SuccessfulResolutionIsReusedAfterFirstCallerCompletes()
    {
        using var cache = new LazyRarResolutionCache();
        var calls = 0;
        var expected = FilePart("segment");

        var first = await cache.GetOrCreateAsync(
            Guid.Empty,
            "segment",
            () =>
            {
                calls++;
                return Task.FromResult(expected);
            },
            CancellationToken.None);
        var second = await cache.GetOrCreateAsync(
            Guid.Empty,
            "segment",
            () =>
            {
                calls++;
                return Task.FromResult(FilePart("duplicate"));
            },
            CancellationToken.None);

        Assert.Same(expected, first);
        Assert.Same(expected, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentResolutionRunsOnlyOneFactory()
    {
        using var cache = new LazyRarResolutionCache();
        var calls = 0;
        var release = new TaskCompletionSource<DavMultipartFile.FilePart>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<DavMultipartFile.FilePart> Resolve()
        {
            Interlocked.Increment(ref calls);
            return release.Task;
        }

        var first = cache.GetOrCreateAsync(
            Guid.Empty, "segment", Resolve, CancellationToken.None);
        var second = cache.GetOrCreateAsync(
            Guid.Empty, "segment", Resolve, CancellationToken.None);
        release.SetResult(FilePart("segment"));

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task FailedResolutionIsNotCached()
    {
        using var cache = new LazyRarResolutionCache();
        var calls = 0;

        async Task<DavMultipartFile.FilePart> Resolve()
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidDataException("bad header");
            return await Task.FromResult(FilePart("segment"));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetOrCreateAsync(
            Guid.Empty, "segment", Resolve, CancellationToken.None));
        var resolved = await cache.GetOrCreateAsync(
            Guid.Empty, "segment", Resolve, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("segment", resolved.SegmentIds[0]);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelSharedResolution()
    {
        using var cache = new LazyRarResolutionCache();
        var calls = 0;
        var release = new TaskCompletionSource<DavMultipartFile.FilePart>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var cancelledWait = cache.GetOrCreateAsync(
            Guid.Empty,
            "segment",
            () =>
            {
                Interlocked.Increment(ref calls);
                return release.Task;
            },
            cts.Token);
        var survivingWait = cache.GetOrCreateAsync(
            Guid.Empty,
            "segment",
            () => throw new InvalidOperationException("must share"),
            CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        release.SetResult(FilePart("segment"));

        Assert.Equal("segment", (await survivingWait).SegmentIds[0]);
        Assert.Equal(1, calls);
    }

    private static DavMultipartFile.FilePart FilePart(string segmentId) => new()
    {
        SegmentIds = [segmentId],
        SegmentIdByteRange = LongRange.FromStartAndSize(0, 100),
        FilePartByteRange = LongRange.FromStartAndSize(10, 90),
    };
}
