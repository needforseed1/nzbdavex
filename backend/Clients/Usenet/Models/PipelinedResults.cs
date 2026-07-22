using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet.Models;

public sealed record PipelinedBodyResult
{
    public required string SegmentId { get; init; }
    public required bool Found { get; init; }
    public YencStream? Stream { get; init; }
}

public sealed record PipelinedArticleResult
{
    public required string SegmentId { get; init; }
    public required bool Found { get; init; }
    public YencStream? Stream { get; init; }
    public UsenetArticleHeader? ArticleHeaders { get; init; }
}

public sealed record PipelinedStatResult
{
    public required string SegmentId { get; init; }
    public required bool Exists { get; init; }

    // Meaningful only when Exists is false. True means not every eligible
    // provider answered "missing" for this segment — some checks returned no
    // result — so absence is unconfirmed and must not be reported as missing.
    public bool Indeterminate { get; init; }
}
