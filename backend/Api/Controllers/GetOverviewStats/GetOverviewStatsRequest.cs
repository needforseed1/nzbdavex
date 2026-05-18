using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

public class GetOverviewStatsRequest
{
    public OverviewWindow Window { get; init; } = OverviewWindow.Last24Hours;
    public CancellationToken CancellationToken { get; init; }

    public GetOverviewStatsRequest(HttpContext context)
    {
        CancellationToken = context.RequestAborted;
        var w = context.GetQueryParam("window");
        if (w is null) return;
        Window = w.ToLowerInvariant() switch
        {
            "7d" => OverviewWindow.Last7Days,
            "24h" => OverviewWindow.Last24Hours,
            _ => throw new BadHttpRequestException("Invalid window parameter (use 24h or 7d)")
        };
    }

    public enum OverviewWindow
    {
        Last24Hours,
        Last7Days,
    }
}
