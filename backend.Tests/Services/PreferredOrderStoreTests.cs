using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class PreferredOrderStoreTests
{
    [Fact]
    public void ApplyOrderMovesReportedCandidatesFirstAndKeepsFallbackOrder()
    {
        string[] candidates = ["first", "second", "third", "fourth"];

        var ordered = PreferredOrderStore.ApplyOrder(
            candidates,
            candidate => candidate,
            ["third", "first"]);

        Assert.Equal(["third", "first", "second", "fourth"], ordered);
    }
}
