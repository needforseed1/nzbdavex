namespace NzbWebDAV.Clients.Plex;

public sealed record PlexServerInfo(
    string? Name,
    string? Version,
    string? MachineIdentifier);

public sealed record PlexSessionObservation
{
    public required string SessionKey { get; init; }
    public string? SessionId { get; init; }
    public string? RatingKey { get; init; }
    public string? Title { get; init; }
    public string? MediaPartPath { get; init; }
    public string State { get; init; } = "unknown";
    public string? PlayerMachineIdentifier { get; init; }
    public string? PlayerTitle { get; init; }
    public string? Product { get; init; }
    public string? PlayerVersion { get; init; }
    public string? Platform { get; init; }
    public string? PlatformVersion { get; init; }
    public bool IsTranscode { get; init; }
}

public sealed record PlexActivityObservation
{
    public required string Key { get; init; }
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
}
