namespace NzbWebDAV.Services.Benchmark;

/// <summary>
/// How aggressive the benchmark is. The trade-off is accuracy vs. data used:
/// faster connections need more transferred bytes to measure reliably, so
/// "Thorough" simply moves more data per step.
/// </summary>
public enum BenchmarkIntensity
{
    Quick,
    Thorough,
}

/// <summary>
/// Tunable knobs for a single benchmark run, derived from the chosen intensity.
/// All byte values are decimal megabytes (1 MB = 1,000,000 bytes) so the numbers
/// line up with what download clients display.
/// </summary>
public sealed class BenchmarkProfile
{
    public required int LatencySamples { get; init; }
    public required int MaxCorpusSegments { get; init; }

    /// <summary>Target bytes to transfer at each connection level before moving on.</summary>
    public required long PerLevelBytes { get; init; }

    /// <summary>Hard wall-clock cap per level so a slow/stalled line can't hang the run.</summary>
    public required TimeSpan PerLevelMaxDuration { get; init; }

    /// <summary>Absolute backstop on total data moved across the whole run.</summary>
    public required long HardTotalBytes { get; init; }

    /// <summary>Connection counts to sweep (ascending), before clamping to the ceiling/cap.</summary>
    public required int[] SweepLevels { get; init; }

    /// <summary>Connection count used while comparing pipelining on vs. off.</summary>
    public required int PipelineTestConnections { get; init; }

    /// <summary>Pipeline depths to compare against the non-pipelined baseline.</summary>
    public required int[] PipelineDepths { get; init; }

    /// <summary>Number of leading articles used to approximate playback startup.</summary>
    public required int StartupSegments { get; init; }

    /// <summary>Real article ids mixed with synthetic misses for each health STAT trial.</summary>
    public required int HealthStatSegments { get; init; }

    /// <summary>Repeated trials required before a health pipeline depth is considered reliable.</summary>
    public required int HealthStatRounds { get; init; }

    /// <summary>Per-round deadline. A depth that misses it is rejected rather than merely scored as slow.</summary>
    public required TimeSpan HealthStatRoundTimeout { get; init; }

    /// <summary>Pipeline depths tested against the provider using the real STAT transport.</summary>
    public required int[] HealthPipelineDepths { get; init; }

    public static BenchmarkProfile For(BenchmarkIntensity intensity) => intensity switch
    {
        BenchmarkIntensity.Thorough => new BenchmarkProfile
        {
            LatencySamples = 8,
            MaxCorpusSegments = 8000,
            PerLevelBytes = 20_000_000,
            PerLevelMaxDuration = TimeSpan.FromSeconds(10),
            HardTotalBytes = 650_000_000,
            SweepLevels = [1, 2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 50],
            PipelineTestConnections = 6,
            PipelineDepths = [4, 8, 16],
            StartupSegments = 12,
            HealthStatSegments = 128,
            HealthStatRounds = 3,
            HealthStatRoundTimeout = TimeSpan.FromSeconds(6),
            HealthPipelineDepths = [1, 4, 8, 16, 32, 64],
        },
        _ => new BenchmarkProfile
        {
            LatencySamples = 5,
            MaxCorpusSegments = 2500,
            PerLevelBytes = 8_000_000,
            PerLevelMaxDuration = TimeSpan.FromSeconds(8),
            HardTotalBytes = 160_000_000,
            SweepLevels = [1, 2, 4, 8, 16, 24],
            PipelineTestConnections = 4,
            PipelineDepths = [8, 16],
            StartupSegments = 8,
            HealthStatSegments = 64,
            HealthStatRounds = 2,
            HealthStatRoundTimeout = TimeSpan.FromSeconds(4),
            HealthPipelineDepths = [1, 4, 8, 16, 32, 64],
        },
    };
}

public sealed class BenchmarkLatency
{
    public double MinMs { get; set; }
    public double AvgMs { get; set; }
    public int Samples { get; set; }
}

public sealed class BenchmarkSweepPoint
{
    public int Connections { get; set; }
    public double MbPerSec { get; set; }
}

public sealed class BenchmarkPipeliningPoint
{
    public int Depth { get; set; }
    public double MbPerSec { get; set; }
}

public sealed class BenchmarkPipelining
{
    public int TestedAtConnections { get; set; }
    public double BaselineMbPerSec { get; set; }
    public List<BenchmarkPipeliningPoint> Tested { get; set; } = [];
    public bool RecommendEnabled { get; set; }
    public int RecommendedDepth { get; set; }
}

public sealed class BenchmarkStartup
{
    public int Segments { get; set; }
    public int NonPipelinedConnections { get; set; }
    public double NonPipelinedFirstMs { get; set; }
    public double NonPipelinedReadyMs { get; set; }
    public List<BenchmarkStartupPipeliningPoint> Pipelined { get; set; } = [];
    public bool RecommendPlaybackPipelining { get; set; }
    public int RecommendedDepth { get; set; }
}

public sealed class BenchmarkStartupPipeliningPoint
{
    public int Depth { get; set; }
    public double FirstMs { get; set; }
    public double ReadyMs { get; set; }
}

public sealed class BenchmarkHealthPipeliningPoint
{
    public int Depth { get; set; }
    public int CompletedRounds { get; set; }
    public int RequiredRounds { get; set; }
    public int Requests { get; set; }
    public int Responses { get; set; }
    public int Found { get; set; }
    public int Missing { get; set; }
    public int Timeouts { get; set; }
    public int Errors { get; set; }
    public double AverageMs { get; set; }
    public double StatsPerSecond { get; set; }
    public bool Reliable { get; set; }
    public string? Failure { get; set; }
}

public sealed class BenchmarkHealthPipelining
{
    public int ArticlesPerRound { get; set; }
    public int Rounds { get; set; }
    public int KnownMissingArticles { get; set; }
    public List<BenchmarkHealthPipeliningPoint> Tested { get; set; } = [];
    public bool Reliable { get; set; }
    public int RecommendedDepth { get; set; }
}

/// <summary>
/// The full structured outcome of a benchmark run. Returned from the POST and
/// mirrored (in part) over the websocket progress topic while running.
/// </summary>
public sealed class BenchmarkResult
{
    public BenchmarkLatency? Latency { get; set; }
    public bool ThroughputTested { get; set; }

    /// <summary>True when the run only measured pipelining depth (connection count left untouched).</summary>
    public bool PipeliningOnly { get; set; }

    /// <summary>True when the run only measured playback startup behavior.</summary>
    public bool StartupOnly { get; set; }

    /// <summary>True when the run only measured health-check STAT pipelining.</summary>
    public bool HealthOnly { get; set; }

    public List<BenchmarkSweepPoint> Sweep { get; set; } = [];
    public int? RecommendedConnections { get; set; }

    /// <summary>The connection count the provider refused to exceed, if we hit it.</summary>
    public int? ProviderConnectionCap { get; set; }

    public BenchmarkPipelining? Pipelining { get; set; }
    public BenchmarkStartup? Startup { get; set; }
    public BenchmarkHealthPipelining? HealthPipelining { get; set; }
    public long DataUsedBytes { get; set; }
    public List<string> Warnings { get; set; } = [];
}

/// <summary>Live progress payload pushed over the BenchmarkProgress websocket topic.</summary>
public sealed class BenchmarkProgressUpdate
{
    public required string Phase { get; init; } // latency | corpus | sweep | pipelining | done
    public required string Status { get; init; }
    public int Percent { get; init; }
    public int? CurrentConnections { get; init; }
    public long DataUsedBytes { get; init; }
    public List<BenchmarkSweepPoint> Sweep { get; init; } = [];
}
