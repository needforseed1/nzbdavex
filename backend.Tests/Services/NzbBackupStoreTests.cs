using System.Text;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class NzbBackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nzbdavex-backup-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RetentionKeepsNewestFiftyBackupsAcrossAllCategories()
    {
        var oldest = "";
        for (var i = 0; i < 50; i++)
        {
            var category = i % 2 == 0 ? "movies" : "tv";
            var directory = Path.Combine(_root, category);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"existing-{i:D2}.nzb");
            await File.WriteAllTextAsync(path, i.ToString());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i - 100));
            if (i == 0) oldest = path;
        }

        var unrelated = Path.Combine(_root, "movies", "notes.txt");
        await File.WriteAllTextAsync(unrelated, "keep me");

        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("new nzb"));
        await NzbBackupStore.SaveAsync(source, "new.nzb", "manual", _root, 50);

        Assert.Equal(50, Directory.EnumerateFiles(_root, "*.nzb", SearchOption.AllDirectories).Count());
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(Path.Combine(_root, "manual", "new.nzb")));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task DuplicateNamesAreIncrementedBeforeRetentionRuns()
    {
        var directory = Path.Combine(_root, "movies");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "release.nzb"), "original");

        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("duplicate"));
        await NzbBackupStore.SaveAsync(source, "release.nzb", "movies", _root, 2);

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(directory, "release.nzb")));
        Assert.Equal("duplicate", await File.ReadAllTextAsync(Path.Combine(directory, "release (2).nzb")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
