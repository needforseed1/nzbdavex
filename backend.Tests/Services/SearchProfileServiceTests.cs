using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Services;

public class SearchProfileServiceTests
{
    [Fact]
    public void StrictCanonicalMatchingFiltersAConflictingSingleResult()
    {
        var items = new[] { new Hit("strict", "A Completely Different Movie 2026") }.ToList();
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            FilenameMatcher.NormalizeTitle("Expected Movie"),
        };

        var filtered = SearchProfileService.ApplyStrictMatching(
            items,
            new HashSet<string> { "strict" },
            expected,
            x => x.Indexer,
            x => x.Title);

        Assert.Empty(filtered);
    }

    [Fact]
    public void ConsensusFallbackLeavesASingleResultAlone()
    {
        var items = new[] { new Hit("strict", "Only Result 2026") }.ToList();

        var filtered = SearchProfileService.ApplyStrictMatching(
            items,
            new HashSet<string> { "strict" },
            new HashSet<string>(),
            x => x.Indexer,
            x => x.Title);

        Assert.Same(items, filtered);
    }

    private sealed record Hit(string Indexer, string Title);
}
