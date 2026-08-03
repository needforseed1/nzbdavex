using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients.Plex;

/// <summary>
/// Small, read-only Plex Media Server client. The integration deliberately
/// limits itself to server identity, active sessions, and current background
/// activities. The notification stream supplements polling so activities that
/// begin and end between two polls are still observable.
/// </summary>
public sealed class PlexClient : IDisposable
{
    private const int MaxNotificationBytes = 1_048_576;
    private readonly HttpClient _http;

    public PlexClient() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
    })
    {
    }

    public PlexClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public async Task<PlexServerInfo> GetServerInfoAsync(
        string baseUrl,
        string token,
        CancellationToken cancellationToken)
    {
        using var document = await GetAsync(baseUrl, token, "/", cancellationToken)
            .ConfigureAwait(false);
        var container = GetContainer(document.RootElement);
        return new PlexServerInfo(
            GetString(container, "friendlyName"),
            GetString(container, "version"),
            GetString(container, "machineIdentifier"));
    }

    public async Task<IReadOnlyList<PlexSessionObservation>> GetSessionsAsync(
        string baseUrl,
        string token,
        CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
                baseUrl, token, "/status/sessions", cancellationToken)
            .ConfigureAwait(false);
        var container = GetContainer(document.RootElement);
        if (!container.TryGetProperty("Metadata", out var metadata))
            return [];

        var result = new List<PlexSessionObservation>();
        IReadOnlyList<JsonElement> items = metadata.ValueKind switch
        {
            JsonValueKind.Array => metadata.EnumerateArray().ToArray(),
            JsonValueKind.Object => [metadata],
            _ => [],
        };
        foreach (var item in items)
        {
            var player = FirstObject(item, "Player");
            var session = FirstObject(item, "Session");
            var media = FirstObject(item, "Media");
            var part = media is { } mediaValue
                ? FirstObject(mediaValue, "Part")
                : null;
            var transcode = FirstObject(item, "TranscodeSession");

            var sessionKey = GetString(item, "sessionKey")
                             ?? GetString(session, "id")
                             ?? BuildFallbackSessionKey(item, player);
            var videoDecision = GetString(transcode, "videoDecision")
                                ?? GetString(media, "videoDecision");
            var audioDecision = GetString(transcode, "audioDecision")
                                ?? GetString(media, "audioDecision");
            var title = BuildTitle(item);

            result.Add(new PlexSessionObservation
            {
                SessionKey = sessionKey,
                SessionId = GetString(session, "id"),
                RatingKey = GetString(item, "ratingKey"),
                Title = title,
                MediaPartPath = GetString(part, "file"),
                State = GetString(player, "state")?.Trim().ToLowerInvariant() ?? "unknown",
                PlayerMachineIdentifier = GetString(player, "machineIdentifier"),
                PlayerTitle = GetString(player, "title"),
                Product = GetString(player, "product"),
                PlayerVersion = GetString(player, "version"),
                Platform = GetString(player, "platform"),
                PlatformVersion = GetString(player, "platformVersion"),
                IsTranscode = transcode is not null
                              || string.Equals(videoDecision, "transcode",
                                  StringComparison.OrdinalIgnoreCase)
                              || string.Equals(audioDecision, "transcode",
                                  StringComparison.OrdinalIgnoreCase),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<PlexActivityObservation>> GetActivitiesAsync(
        string baseUrl,
        string token,
        CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
                baseUrl, token, "/activities", cancellationToken)
            .ConfigureAwait(false);
        var container = GetContainer(document.RootElement);
        if (!container.TryGetProperty("Activity", out var activity))
            return [];

        IReadOnlyList<JsonElement> items = activity.ValueKind switch
        {
            JsonValueKind.Array => activity.EnumerateArray().ToArray(),
            JsonValueKind.Object => [activity],
            _ => [],
        };
        return items.Select(item =>
        {
            var type = GetString(item, "type");
            var title = GetString(item, "title");
            var subtitle = GetString(item, "subtitle");
            return new PlexActivityObservation
            {
                Key = GetString(item, "uuid")
                      ?? string.Join('|', type ?? "activity", title, subtitle),
                Type = type,
                Title = title,
                Subtitle = subtitle,
            };
        }).ToList();
    }

    public async Task ListenForActivityNotificationsAsync(
        string baseUrl,
        string token,
        Action<IReadOnlyList<PlexActivityObservation>> onActivities,
        CancellationToken cancellationToken)
    {
        var baseUri = ValidateEndpoint(baseUrl, token);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/:/websockets/notifications",
            Query = "",
        };

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        AddPlexHeaders(socket.Options, baseUrl, token);
        await socket.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[16_384];
        while (socket.State == WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (message.Length + result.Count > MaxNotificationBytes)
                    throw new InvalidDataException(
                        "Plex sent an unexpectedly large notification.");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var activities = ParseActivityNotifications(message.ToArray());
            if (activities.Count > 0) onActivities(activities);
        }
    }

    internal static IReadOnlyList<PlexActivityObservation> ParseActivityNotifications(
        ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("NotificationContainer", out var container))
            container = root;
        if (!container.TryGetProperty("ActivityNotification", out var notifications))
            return [];

        IReadOnlyList<JsonElement> items = notifications.ValueKind switch
        {
            JsonValueKind.Array => notifications.EnumerateArray().ToArray(),
            JsonValueKind.Object => [notifications],
            _ => [],
        };
        var result = new List<PlexActivityObservation>();
        foreach (var item in items)
        {
            var activity = FirstObject(item, "Activity");
            if (activity is not { } value) continue;
            var type = GetString(value, "type");
            var title = GetString(value, "title");
            var subtitle = GetString(value, "subtitle");
            result.Add(new PlexActivityObservation
            {
                Key = GetString(value, "uuid")
                      ?? GetString(item, "uuid")
                      ?? string.Join('|', type ?? "activity", title, subtitle),
                Type = type,
                Title = title,
                Subtitle = subtitle,
            });
        }
        return result;
    }

    private async Task<JsonDocument> GetAsync(
        string baseUrl,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(baseUrl, token);

        var uri = new Uri($"{baseUrl.TrimEnd('/')}{path}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddPlexHeaders(request.Headers, baseUrl, token);

        using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Plex returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                null,
                response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static Uri ValidateEndpoint(string baseUrl, string token)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                "Plex server URL must be an absolute HTTP(S) URL.");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("A Plex token is required.");
        return parsed;
    }

    private static void AddPlexHeaders(
        HttpHeaders headers,
        string baseUrl,
        string token)
    {
        headers.TryAddWithoutValidation("X-Plex-Token", token);
        headers.TryAddWithoutValidation("X-Plex-Product", "NzbDAVex");
        headers.TryAddWithoutValidation("X-Plex-Version", ConfigManager.AppVersion);
        headers.TryAddWithoutValidation("X-Plex-Platform", "NzbDAVex");
        headers.TryAddWithoutValidation(
            "X-Plex-Client-Identifier", ClientIdentifier(baseUrl));
    }

    private static void AddPlexHeaders(
        ClientWebSocketOptions options,
        string baseUrl,
        string token)
    {
        options.SetRequestHeader("X-Plex-Token", token);
        options.SetRequestHeader("X-Plex-Product", "NzbDAVex");
        options.SetRequestHeader("X-Plex-Version", ConfigManager.AppVersion);
        options.SetRequestHeader("X-Plex-Platform", "NzbDAVex");
        options.SetRequestHeader(
            "X-Plex-Client-Identifier", ClientIdentifier(baseUrl));
    }

    private static JsonElement GetContainer(JsonElement root) =>
        root.TryGetProperty("MediaContainer", out var container) ? container : root;

    private static JsonElement? FirstObject(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Object) return value;
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) return item;
        return null;
    }

    private static string? GetString(JsonElement? parent, string property)
    {
        if (parent is not { } value
            || !value.TryGetProperty(property, out var child))
            return null;
        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString(),
            JsonValueKind.Number => child.GetRawText(),
            _ => null,
        };
    }

    private static string BuildFallbackSessionKey(JsonElement item, JsonElement? player) =>
        string.Join('|',
            GetString(player, "machineIdentifier") ?? "unknown-player",
            GetString(item, "ratingKey") ?? GetString(item, "key") ?? "unknown-media");

    private static string? BuildTitle(JsonElement item)
    {
        var title = GetString(item, "title");
        var parent = GetString(item, "parentTitle");
        var grandparent = GetString(item, "grandparentTitle");
        if (grandparent is not null && title is not null)
            return parent is null
                ? $"{grandparent} · {title}"
                : $"{grandparent} · {parent} · {title}";
        return title ?? parent ?? grandparent;
    }

    private static string ClientIdentifier(string baseUrl)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl.Trim().ToLowerInvariant()));
        return $"nzbdavex-{Convert.ToHexString(digest)[..16].ToLowerInvariant()}";
    }

    public void Dispose() => _http.Dispose();
}
