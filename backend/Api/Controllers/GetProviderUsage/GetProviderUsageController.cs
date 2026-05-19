using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.Controllers.GetProviderUsage;

[ApiController]
[Route("api/get-provider-usage")]
public class GetProviderUsageController(
    ConfigManager configManager,
    ProviderBytesTracker bytesTracker
) : BaseApiController
{
    private async Task<GetProviderUsageResponse> GetUsageAsync()
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var bytesPerDayByHost = await ProviderUsageHelper
            .ReadBytesPerDayAsync(providerConfig.Providers.Select(p => p.Host))
            .ConfigureAwait(false);

        var items = providerConfig.Providers
            .Select((provider, index) =>
            {
                var used = ProviderUsageHelper.ComputeUsage(bytesTracker, provider);
                bytesPerDayByHost.TryGetValue(provider.Host, out var bytesPerDay);
                return new GetProviderUsageResponse.ProviderUsageItem
                {
                    Index = index,
                    Host = provider.Host,
                    BytesUsed = used,
                    ByteLimit = provider.ByteLimit,
                    OverLimit = ProviderUsageHelper.IsOverLimit(bytesTracker, provider),
                    BytesPerDay = bytesPerDay,
                    DaysRemaining = ProviderUsageHelper.ProjectDaysRemaining(provider, used, bytesPerDay),
                };
            })
            .ToList();

        return new GetProviderUsageResponse
        {
            Status = true,
            Providers = items,
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var response = await GetUsageAsync().ConfigureAwait(false);
        return Ok(response);
    }
}
