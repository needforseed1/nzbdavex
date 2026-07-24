using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Handlers;
using NWebDav.Server.Helpers;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Implementation of the GET and HEAD method.
/// </summary>
/// <remarks>
/// The specification of the WebDAV GET and HEAD methods for collections
/// can be found in the
/// <see href="http://www.webdav.org/specs/rfc2518.html#rfc.section.8.4">
/// WebDAV specification
/// </see>.
/// </remarks>
public class GetAndHeadHandlerPatch : IRequestHandler
{
    private readonly IStore _store;
    private readonly ProviderUsageTracker _providerUsageTracker;
    private readonly ActiveReadRegistry _activeReadRegistry;
    private readonly ConfigManager _configManager;

    public GetAndHeadHandlerPatch(
        IStore store,
        ProviderUsageTracker providerUsageTracker,
        ActiveReadRegistry activeReadRegistry,
        ConfigManager configManager)
    {
        _store = store;
        _providerUsageTracker = providerUsageTracker;
        _activeReadRegistry = activeReadRegistry;
        _configManager = configManager;
    }
    
    /// <summary>
    /// Handle a GET or HEAD request.
    /// </summary>
    /// <param name="httpContext">
    /// The HTTP context of the request.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous GET or HEAD operation. The
    /// task will always return <see langword="true"/> upon completion.
    /// </returns>
    public async Task<bool> HandleRequestAsync(HttpContext httpContext)
    {
        // Obtain request and response
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Determine if we are invoked as HEAD
        var isHeadRequest = request.Method == HttpMethods.Head;

        // Determine the requested range
        var range = request.GetRange();

        // Obtain the WebDAV collection
        var entry = await _store.GetItemAsync(request.GetUri(), httpContext.RequestAborted).ConfigureAwait(false);
        if (entry == null)
        {
            // Set status to not found
            response.SetStatus(DavStatusCode.NotFound);
            return true;
        }

        // ETag might be used for a conditional request
        string? etag = null;

        // Add non-expensive headers based on properties
        var propertyManager = entry.PropertyManager;
        if (propertyManager != null)
        {
            // Add Last-Modified header
            var lastModifiedUtc = (string?)await propertyManager.GetPropertyAsync(entry, DavGetLastModified<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (lastModifiedUtc != null)
                response.Headers.LastModified = lastModifiedUtc;

            // Add ETag
            etag = (string?)await propertyManager.GetPropertyAsync(entry, DavGetEtag<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (etag != null)
                response.Headers.ETag = etag;

            // Add type
            var contentType = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentType<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentType != null)
                response.ContentType = contentType;

            // Add language
            var contentLanguage = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentLanguage<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentLanguage != null)
                response.Headers.ContentLanguage = contentLanguage;
        }

        var playbackPath = request.GetUri().AbsolutePath;
        var playbackFileName = entry switch
        {
            NzbWebDAV.WebDav.DatabaseStoreIdFile idFile => idFile.FriendlyName,
            _ => !string.IsNullOrEmpty(entry.Name)
                ? entry.Name
                : System.IO.Path.GetFileName(playbackPath)
        };
        Guid? sessionId = null;
        PlaybackRequestDiagnostics? diagnostics = null;
        if (!isHeadRequest)
        {
            var clientKey = $"{httpContext.Connection.RemoteIpAddress}|{request.Headers.UserAgent}";
            sessionId = _activeReadRegistry.GetOrCreate(
                playbackPath, clientKey, playbackFileName, fileSize: null);
            _activeReadRegistry.MarkRequestStarted(
                sessionId.Value,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                request.Headers.UserAgent.ToString());
            diagnostics = new PlaybackRequestDiagnostics(
                sessionId.Value,
                playbackPath,
                playbackFileName,
                FormatRange(range?.Start, range?.End),
                range?.Start ?? 0);
        }

        using var usageScope = sessionId.HasValue
            ? _providerUsageTracker.BeginScope(sessionId.Value)
            : null;
        using var byteCapture = sessionId.HasValue
            ? _providerUsageTracker.BeginByteCapture()
            : null;
        using var readSessionScope = sessionId.HasValue
            ? MultiProviderNntpClient.BeginReadSessionScope(sessionId.Value)
            : null;
        using var diagnosticScope = diagnostics is not null
            ? PlaybackDiagnosticContext.Begin(diagnostics)
            : null;

        var reason = "completed";
        var endReason = ReadSession.EndReasonCode.Completed;
        Exception? terminalException = null;
        try
        {
            // Stream construction stays inside the scopes so buffered
            // background segment tasks inherit this playback session.
            var stream = await entry.GetReadableStreamAsync(httpContext.RequestAborted)
                .ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                if (stream != Stream.Null)
                {
                    var streamLength = TryGetLength(stream);
                    diagnostics?.MarkStreamOpened(
                        playbackFileName,
                        streamLength,
                        range?.Start ?? 0);
                    if (sessionId.HasValue)
                        _activeReadRegistry.UpdateInfo(
                            sessionId.Value,
                            playbackFileName,
                            streamLength);

                    response.SetStatus(DavStatusCode.Ok);

                    try
                    {
                        if (stream.CanSeek)
                        {
                            response.Headers.AcceptRanges = "bytes";
                            var length = stream.Length;

                            if (range != null)
                            {
                                var start = range.Start ?? 0;
                                var end = Math.Min(range.End ?? long.MaxValue, length - 1);
                                if (start > end)
                                {
                                    response.Headers.ContentRange = $"bytes */{stream.Length}";
                                    response.SetStatus((DavStatusCode)416);
                                    return true;
                                }

                                length = end - start + 1;
                                response.Headers.ContentRange = $"bytes {start}-{end}/{stream.Length}";
                                if (length < stream.Length)
                                    response.SetStatus(DavStatusCode.PartialContent);
                            }

                            response.ContentLength = length;
                        }
                    }
                    catch (NotSupportedException)
                    {
                        // If the content length is not supported, skip it.
                    }

                    if (etag != null && request.Headers.IfNoneMatch == etag)
                    {
                        response.ContentLength = 0;
                        response.SetStatus(DavStatusCode.NotModified);
                        return true;
                    }

                    if (!isHeadRequest && diagnostics is not null && sessionId.HasValue)
                    {
                        await PlaybackTransferPump.CopyAsync(
                                stream,
                                response.Body,
                                diagnostics,
                                range?.Start ?? 0,
                                range?.End,
                                seekSource: true,
                                onBytesServed: (bytes, position) =>
                                    _activeReadRegistry.Touch(sessionId.Value, bytes, position),
                                onSourceError: null,
                                cancellationToken: httpContext.RequestAborted)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    response.SetStatus(DavStatusCode.NoContent);
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            reason = "client-abort";
            endReason = ReadSession.EndReasonCode.Aborted;
            throw;
        }
        catch (Exception exception)
        {
            terminalException = exception;
            if (ContainsTimeout(exception))
            {
                reason = "timeout";
                endReason = ReadSession.EndReasonCode.Timeout;
            }
            else
            {
                reason = "error";
                endReason = ReadSession.EndReasonCode.Error;
            }
            throw;
        }
        finally
        {
            if (sessionId.HasValue && diagnostics is not null)
            {
                _activeReadRegistry.MarkRequestEnded(sessionId.Value, endReason);
                CompletePlaybackDiagnostics(diagnostics, reason, terminalException);
            }
        }
    }

    private void CompletePlaybackDiagnostics(
        PlaybackRequestDiagnostics diagnostics,
        string reason,
        Exception? terminalException)
    {
        var usage = ProviderUsageTracker.ToDisplayHosts(
            _providerUsageTracker.Snapshot(diagnostics.SessionId),
            _configManager.GetUsenetProviderConfig().Providers);
        var providerSummary = usage.Count == 0
            ? "none"
            : string.Join(
                ',',
                usage.OrderByDescending(x => x.Value)
                    .Select(x => $"{x.Key}:{x.Value}"));
        var bytesFetched = _providerUsageTracker
            .SnapshotBytes(diagnostics.SessionId)
            .Values
            .Sum();
        diagnostics.Complete(
            reason,
            providerSummary,
            bytesFetched,
            _providerUsageTracker.GetFailoverSaves(diagnostics.SessionId),
            terminalException);
    }

    private static string? FormatRange(long? start, long? end) =>
        start is null && end is null
            ? null
            : $"bytes={start?.ToString() ?? ""}-{end?.ToString() ?? ""}";

    private static bool ContainsTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is TimeoutException)
                return true;
        return false;
    }

    private static long? TryGetLength(Stream stream)
    {
        if (!stream.CanSeek) return null;
        try
        {
            return stream.Length;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
