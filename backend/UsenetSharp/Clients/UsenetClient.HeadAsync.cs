using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        using var operationCts = CreateOperationCts(cancellationToken);
        var operationToken = operationCts.Token;

        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            // Send HEAD command with message-id
            await WriteLineAsync($"HEAD <{segmentId}>".AsMemory(), operationToken);
            var response = await ReadLineAsync(operationToken);
            var responseCode = ParseResponseCode(response);

            // Article retrieved - head follows (multi-line)
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedHeadFollows)
            {
                // Parse headers
                var headers = await ParseArticleHeadersAsync(operationToken);

                return new UsenetHeadResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    ArticleHeaders = headers
                };
            }

            return new UsenetHeadResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                ArticleHeaders = null
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
