namespace NzbWebDAV.Api.Controllers.UpdateConfig;

public class UpdateConfigResponse : BaseApiResponse
{
    public long Revision { get; init; }
    public string? Warning { get; init; }
}
