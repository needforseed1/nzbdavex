using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Tests.Services.Benchmark;

public class BenchmarkCorpusProviderTests
{
    [Fact]
    public void HealthCorpusTakesLargestFilesFirstAndRemovesDuplicates()
    {
        var document = new NzbDocument();
        document.Files.Add(File("small", "small-1", "shared"));
        document.Files.Add(File("large", "large-1", "shared", "large-2", "large-3"));

        var selected = BenchmarkCorpusProvider.SelectHealthSegments(document, 4);

        Assert.Equal(["large-1", "shared", "large-2", "large-3"], selected);
    }

    private static NzbFile File(string subject, params string[] ids)
    {
        var file = new NzbFile { Subject = subject };
        foreach (var id in ids)
            file.Segments.Add(new NzbSegment { Bytes = 1, MessageId = id });
        return file;
    }
}
