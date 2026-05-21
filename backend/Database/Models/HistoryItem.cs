namespace NzbWebDAV.Database.Models;

public class HistoryItem
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string FileName { get; set; } = null!;
    public string JobName { get; set; } = null!;
    public string Category { get; set; }
    public DownloadStatusOption DownloadStatus { get; set; }
    public long TotalSegmentBytes { get; set; }
    public int DownloadTimeSeconds { get; set; }
    public string? FailMessage { get; set; }
    public Guid? DownloadDirId { get; set; }
    public Guid? NzbBlobId { get; set; }
    public string? IndexerName { get; set; }

    // Variants — opaque content-group identifier inherited from the play click
    // (entry.Type:entry.Id). Lets multiple size-variants of the same content be
    // tracked as siblings without nzbdav ever inspecting what the string means.
    public string? ContentGroupKey { get; set; }

    // Variants — last successful play redirect, used as the LRU signal for eviction.
    public DateTimeOffset? LastPlayedAt { get; set; }

    public enum DownloadStatusOption
    {
        Completed = 1,
        Failed = 2,
    }
}