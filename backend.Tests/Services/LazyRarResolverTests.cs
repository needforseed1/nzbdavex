using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class LazyRarResolverTests
{
    [Fact]
    public async Task NearEndReadResolvesOnlyTheRequiredTailVolume()
    {
        var resolvedSegments = new ConcurrentQueue<string>();
        var resolver = new LazyRarResolver((_, pending, _) =>
        {
            resolvedSegments.Enqueue(pending.SegmentIds[0]);
            return Task.FromResult(FilePart(pending.SegmentIds[0], pending.EstimatedDataSize));
        });
        var multipartFile = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts = [FilePart("part-0", 100)],
                IsLazy = true,
                PendingParts = Enumerable.Range(1, 7)
                    .Select(index => PendingPart($"part-{index}", 100))
                    .ToArray(),
            },
        };

        var metadata = await resolver.EnsureResolvedForReadAsync(
            multipartFile,
            targetByteOffset: 750,
            totalLength: 800,
            CancellationToken.None);

        Assert.Equal(["part-7"], resolvedSegments);
        Assert.Equal(6, metadata.PendingParts.Length);
        Assert.Single(metadata.TailFileParts);
        Assert.Equal("part-7", metadata.TailFileParts[0].SegmentIds[0]);
    }

    [Fact]
    public async Task TailResolutionContinuesBackwardWhenAnEstimateWasTooLarge()
    {
        var resolvedSegments = new List<string>();
        var resolver = new LazyRarResolver((_, pending, _) =>
        {
            var segmentId = pending.SegmentIds[0];
            resolvedSegments.Add(segmentId);
            var exactSize = segmentId == "part-7" ? 30 : pending.EstimatedDataSize;
            return Task.FromResult(FilePart(segmentId, exactSize));
        });
        var multipartFile = MultipartFile(partCount: 8, partSize: 100);

        var metadata = await resolver.EnsureResolvedForReadAsync(
            multipartFile,
            targetByteOffset: 750,
            totalLength: 800,
            CancellationToken.None);

        Assert.Equal(["part-7", "part-6"], resolvedSegments);
        Assert.Equal(["part-6", "part-7"],
            metadata.TailFileParts.Select(part => part.SegmentIds[0]));
        Assert.Equal(5, metadata.PendingParts.Length);
    }

    [Fact]
    public async Task PrefixAndTailResolutionMergeInPlaybackOrder()
    {
        var resolver = new LazyRarResolver((_, pending, _) =>
            Task.FromResult(FilePart(pending.SegmentIds[0], pending.EstimatedDataSize)));
        var multipartFile = MultipartFile(partCount: 4, partSize: 100);

        await resolver.EnsureResolvedForReadAsync(
            multipartFile,
            targetByteOffset: 350,
            totalLength: 400,
            CancellationToken.None);
        await resolver.EnsureResolvedThroughAsync(
            multipartFile,
            long.MaxValue,
            CancellationToken.None);

        Assert.False(multipartFile.Metadata.IsLazy);
        Assert.Empty(multipartFile.Metadata.PendingParts);
        Assert.Empty(multipartFile.Metadata.TailFileParts);
        Assert.Equal(["part-0", "part-1", "part-2", "part-3"],
            multipartFile.Metadata.FileParts.Select(part => part.SegmentIds[0]));
    }

    [Fact]
    public async Task ForegroundTailResolutionPreservesPlaybackPriority()
    {
        SemaphorePriority? observedPriority = null;
        var resolver = new LazyRarResolver((_, pending, token) =>
        {
            observedPriority = token.GetContext<DownloadPriorityContext>()?.Priority;
            return Task.FromResult(FilePart(pending.SegmentIds[0], pending.EstimatedDataSize));
        });
        var multipartFile = MultipartFile(partCount: 3, partSize: 100);
        using var cts = new CancellationTokenSource();
        using var priorityScope = cts.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
        });

        await resolver.EnsureResolvedForReadAsync(
            multipartFile,
            targetByteOffset: 250,
            totalLength: 300,
            cts.Token);

        Assert.Equal(SemaphorePriority.High, observedPriority);
    }

    [Fact]
    public async Task SeparateRequestObjectsMergePrefixAndTailState()
    {
        var resolver = new LazyRarResolver((_, pending, _) =>
            Task.FromResult(FilePart(pending.SegmentIds[0], pending.EstimatedDataSize)));
        var tailRequest = MultipartFile(partCount: 4, partSize: 100);
        var prefixRequest = MultipartFile(partCount: 4, partSize: 100);
        prefixRequest.Id = tailRequest.Id;

        await resolver.EnsureResolvedForReadAsync(
            tailRequest,
            targetByteOffset: 350,
            totalLength: 400,
            CancellationToken.None);
        var merged = await resolver.ResolveNextAsync(prefixRequest, CancellationToken.None);

        Assert.Equal(["part-0", "part-1"],
            merged.FileParts.Select(part => part.SegmentIds[0]));
        Assert.Equal(["part-2"],
            merged.PendingParts.Select(part => part.SegmentIds[0]));
        Assert.Equal(["part-3"],
            merged.TailFileParts.Select(part => part.SegmentIds[0]));
    }

    private static DavMultipartFile MultipartFile(int partCount, long partSize) => new()
    {
        Id = Guid.NewGuid(),
        Metadata = new DavMultipartFile.Meta
        {
            FileParts = [FilePart("part-0", partSize)],
            IsLazy = true,
            PendingParts = Enumerable.Range(1, partCount - 1)
                .Select(index => PendingPart($"part-{index}", partSize))
                .ToArray(),
        },
    };

    private static DavMultipartFile.PendingPart PendingPart(string segmentId, long size) => new()
    {
        SegmentIds = [segmentId],
        SegmentIdByteRange = LongRange.FromStartAndSize(0, size),
        EstimatedDataSize = size,
    };

    private static DavMultipartFile.FilePart FilePart(string segmentId, long size) => new()
    {
        SegmentIds = [segmentId],
        SegmentIdByteRange = LongRange.FromStartAndSize(0, size),
        FilePartByteRange = LongRange.FromStartAndSize(0, size),
    };
}
