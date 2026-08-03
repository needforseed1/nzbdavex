namespace NzbWebDAV.Api.Controllers.TestPlexConnection;

public sealed class TestPlexConnectionResponse : BaseApiResponse
{
    public required bool Connected { get; init; }
    public string? ServerName { get; init; }
    public string? ServerVersion { get; init; }
    public int? ActiveSessions { get; init; }
    public bool? ActivitiesAvailable { get; init; }
    public string? ActivitiesError { get; init; }
}
