using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This class has four responsibilities that differ from the underlying UsenetClient implementation
///   1. throw `CouldNotConnectToUsenetException` after any connection error.
///   2. throw `CouldNotLoginToUsenetException` after any login error.
///   3. Provide yenc-decoded data for articles retrieved through article/body commands.
///   4. throw `UsenetArticleNotFound` when articles do not exist, within article/body/head commands.
/// </summary>
public class BaseNntpClient : NntpClient
{
    private readonly UsenetClient _client = new();

    public override async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        try
        {
            await _client.ConnectAsync(host, port, useSsl, cancellationToken);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            const string message = "Could not connect to usenet host. Check connection settings.";
            throw new CouldNotConnectToUsenetException(message, e);
        }
    }

    public override async Task<UsenetResponse> AuthenticateAsync
    (
        string user,
        string pass,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await _client.AuthenticateAsync(user, pass, cancellationToken);
            if (!response.Success)
            {
                var message = $"Could not login to usenet host: {response.ResponseMessage}";
                throw new CouldNotLoginToUsenetException(message);
            }

            return response;
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            throw new CouldNotLoginToUsenetException("Could not login to usenet host.", e);
        }
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return _client.StatAsync(segmentId, cancellationToken);
    }

    public override async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        var headResponse = await _client.HeadAsync(segmentId, cancellationToken);

        if (headResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadFollows)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetHeadResponse()
        {
            SegmentId = headResponse.SegmentId,
            ResponseCode = headResponse.ResponseCode,
            ResponseMessage = headResponse.ResponseMessage,
            ArticleHeaders = headResponse.ArticleHeaders!
        };
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var bodyResponse = await _client.BodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);

        if (bodyResponse.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetDecodedBodyResponse()
        {
            SegmentId = bodyResponse.SegmentId,
            ResponseCode = bodyResponse.ResponseCode,
            ResponseMessage = bodyResponse.ResponseMessage,
            Stream = new YencStream(bodyResponse.Stream!),
        };
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var articleResponse = await _client.ArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

        if (articleResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetDecodedArticleResponse()
        {
            SegmentId = articleResponse.SegmentId,
            ResponseCode = articleResponse.ResponseCode,
            ResponseMessage = articleResponse.ResponseMessage,
            ArticleHeaders = articleResponse.ArticleHeaders!,
            Stream = new YencStream(articleResponse.Stream!),
        };
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return _client.DateAsync(cancellationToken);
    }

    public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var ids = ToSegmentIds(segmentIds);
        var index = 0;
        await foreach (var response in _client.StatPipelinedAsync(ids, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new PipelinedStatResult
            {
                SegmentId = segmentIds[index++],
                Exists = response.ArticleExists,
            };
        }
    }

    public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var ids = ToSegmentIds(segmentIds);
        await foreach (var response in _client.BodyPipelinedAsync(ids, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var found = response.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows
                        && response.Stream != null;
            yield return new PipelinedBodyResult
            {
                SegmentId = response.SegmentId,
                Found = found,
                Stream = found ? new YencStream(response.Stream!) : null,
            };
        }
    }

    public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var ids = ToSegmentIds(segmentIds);
        await foreach (var response in _client.ArticlePipelinedAsync(ids, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var found = response.ResponseType == UsenetResponseType.ArticleRetrievedHeadAndBodyFollow
                        && response.Stream != null;
            yield return new PipelinedArticleResult
            {
                SegmentId = response.SegmentId,
                Found = found,
                Stream = found ? new YencStream(response.Stream!) : null,
                ArticleHeaders = response.ArticleHeaders,
            };
        }
    }

    private static List<SegmentId> ToSegmentIds(IReadOnlyList<string> segmentIds)
    {
        var ids = new List<SegmentId>(segmentIds.Count);
        foreach (var segmentId in segmentIds) ids.Add(segmentId);
        return ids;
    }

    public override void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}