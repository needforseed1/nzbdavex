using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.ClearPlaybackSessions;

public class ClearPlaybackSessionsResponse : BaseApiResponse
{
    [JsonPropertyName("deleted")]
    public required int Deleted { get; init; }
}
