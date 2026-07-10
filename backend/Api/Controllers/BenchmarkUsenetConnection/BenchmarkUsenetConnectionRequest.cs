using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Api.Controllers.BenchmarkUsenetConnection;

public class BenchmarkUsenetConnectionRequest
{
    public string Host { get; init; }
    public string User { get; init; }
    public string Pass { get; init; }
    public int Port { get; init; }
    public bool UseSsl { get; init; }

    /// <summary>The provider's currently-configured max connections (the sweep probes a bit above it).</summary>
    public int MaxConnections { get; init; }

    public BenchmarkIntensity Intensity { get; init; }

    /// <summary>When true, skip the connection sweep and only tune pipelining depth.</summary>
    public bool PipeliningOnly { get; init; }

    /// <summary>When true, skip throughput and measure first-buffer playback startup behavior.</summary>
    public bool StartupOnly { get; init; }

    /// <summary>When true, benchmark repeated pipelined STAT health-check batches.</summary>
    public bool HealthOnly { get; init; }

    public ProviderType Type { get; init; }

    public BenchmarkUsenetConnectionRequest(HttpContext context)
    {
        Host = context.Request.Form["host"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet host is required");

        User = context.Request.Form["user"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet user is required");

        Pass = context.Request.Form["pass"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet pass is required");

        var port = context.Request.Form["port"].FirstOrDefault()
                   ?? throw new BadHttpRequestException("Usenet port is required");

        var useSsl = context.Request.Form["use-ssl"].FirstOrDefault()
                     ?? throw new BadHttpRequestException("Usenet use-ssl is required");

        Port = !int.TryParse(port, out var portValue)
            ? throw new BadHttpRequestException("Invalid usenet port")
            : portValue;

        UseSsl = !bool.TryParse(useSsl, out var useSslValue)
            ? throw new BadHttpRequestException("Invalid use-ssl value")
            : useSslValue;

        // Optional knobs — fall back to sensible defaults rather than rejecting.
        var maxConnections = context.Request.Form["max-connections"].FirstOrDefault();
        MaxConnections = int.TryParse(maxConnections, out var mc) && mc > 0 ? mc : 10;

        var intensity = context.Request.Form["intensity"].FirstOrDefault();
        Intensity = string.Equals(intensity, "thorough", StringComparison.OrdinalIgnoreCase)
            ? BenchmarkIntensity.Thorough
            : BenchmarkIntensity.Quick;

        var pipeliningOnly = context.Request.Form["pipelining-only"].FirstOrDefault();
        PipeliningOnly = bool.TryParse(pipeliningOnly, out var po) && po;

        var startupOnly = context.Request.Form["startup-only"].FirstOrDefault();
        StartupOnly = bool.TryParse(startupOnly, out var so) && so;

        var healthOnly = context.Request.Form["health-only"].FirstOrDefault();
        HealthOnly = bool.TryParse(healthOnly, out var ho) && ho;

        var providerType = context.Request.Form["provider-type"].FirstOrDefault();
        Type = int.TryParse(providerType, out var pt) && Enum.IsDefined(typeof(ProviderType), pt)
            ? (ProviderType)pt
            : ProviderType.Pooled;
    }

    public UsenetProviderConfig.ConnectionDetails ToConnectionDetails()
    {
        return new UsenetProviderConfig.ConnectionDetails
        {
            Host = Host,
            User = User,
            Pass = Pass,
            Port = Port,
            UseSsl = UseSsl,
            MaxConnections = 1,
            Type = ProviderType.Disabled
        };
    }
}
