using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

// Shares immutable volume mappings across request-local DavMultipartFile
// instances. In-flight work is coalesced without running losing dictionary
// factories, and successful results remain available briefly so a near-
// simultaneous range request does not repeat every completed header parse.
internal sealed class LazyRarResolutionCache : IDisposable
{
    private const int MaxCachedParts = 8192;
    private static readonly TimeSpan SuccessfulResultLifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<CacheKey, Lazy<Task<DavMultipartFile.FilePart>>> _inFlight = new();
    private readonly MemoryCache _completed = new(new MemoryCacheOptions
    {
        SizeLimit = MaxCachedParts,
    });

    public Task<DavMultipartFile.FilePart> GetOrCreateAsync(
        Guid multipartFileId,
        string firstSegmentId,
        Func<Task<DavMultipartFile.FilePart>> factory,
        CancellationToken callerCt)
    {
        var key = new CacheKey(multipartFileId, firstSegmentId);
        if (_completed.TryGetValue(key, out DavMultipartFile.FilePart? completed)
            && completed is not null)
        {
            return Task.FromResult(completed);
        }

        var shared = _inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<DavMultipartFile.FilePart>>(
                () => ResolveAndCacheAsync(key, factory),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var task = shared.Value;

        // Every caller may register this cheap removal continuation. The
        // key/value remove keeps an older completion from deleting newer work.
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(
                new KeyValuePair<CacheKey, Lazy<Task<DavMultipartFile.FilePart>>>(key, shared)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Caller cancellation stops only that wait; it must not cancel work
        // shared with another request.
        return task.WaitAsync(callerCt);
    }

    private async Task<DavMultipartFile.FilePart> ResolveAndCacheAsync(
        CacheKey key,
        Func<Task<DavMultipartFile.FilePart>> factory)
    {
        var resolved = await factory().ConfigureAwait(false);
        _completed.Set(
            key,
            resolved,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = SuccessfulResultLifetime,
                Size = 1,
            });
        return resolved;
    }

    public void Dispose() => _completed.Dispose();

    private readonly record struct CacheKey(Guid MultipartFileId, string FirstSegmentId);
}
