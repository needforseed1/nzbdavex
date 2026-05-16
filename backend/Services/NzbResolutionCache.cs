using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NzbWebDAV.Services;

public class NzbResolutionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public string Add(string indexerName, string indexerUserAgent, string nzbUrl, string title, string type)
    {
        Cleanup();
        var token = GenerateToken();
        _entries[token] = new Entry
        {
            IndexerName = indexerName,
            IndexerUserAgent = indexerUserAgent,
            NzbUrl = nzbUrl,
            Title = title,
            Type = type,
            CreatedAt = DateTime.UtcNow,
        };
        return token;
    }

    public Entry? Get(string token) => _entries.TryGetValue(token, out var e) ? e : null;

    public void UpdateResolved(string token, Guid davItemId, string extension)
    {
        if (_entries.TryGetValue(token, out var e))
        {
            e.DavItemId = davItemId;
            e.VideoExtension = extension;
        }
    }

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _entries)
            if (kv.Value.CreatedAt < cutoff)
                _entries.TryRemove(kv.Key, out _);
    }

    private static string GenerateToken()
    {
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public class Entry
    {
        public required string IndexerName { get; init; }
        public required string IndexerUserAgent { get; init; }
        public required string NzbUrl { get; init; }
        public required string Title { get; init; }
        public required string Type { get; init; }
        public required DateTime CreatedAt { get; init; }
        public Guid? DavItemId { get; set; }
        public string? VideoExtension { get; set; }
    }
}
