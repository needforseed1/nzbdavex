using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class LazyRarResolverTests
{
    [Theory]
    [InlineData(64, null, 64)]
    [InlineData(64, "secret", 2)]
    [InlineData(2, "secret", 2)]
    [InlineData(0, null, 1)]
    public void PasswordedRarResolutionCapsCpuWithoutSlowingOrdinaryArchives(
        int configuredConcurrency,
        string? archivePassword,
        int expected)
    {
        Assert.Equal(expected,
            LazyRarResolver.GetResolutionConcurrency(configuredConcurrency, archivePassword));
    }
}
