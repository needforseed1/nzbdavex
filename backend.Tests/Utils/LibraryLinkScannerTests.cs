using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public sealed class LibraryLinkScannerTests
{
    [Fact]
    public async Task ScanReturnsUniqueDavItemIdsAndResolvesRelativeSymlinks()
    {
        using var fixture = new LibraryFixture();
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        fixture.AddAbsoluteSymlink("movie-one.mkv", firstId);
        fixture.AddAbsoluteSymlink("duplicate-movie-one.mkv", firstId);
        fixture.AddRelativeSymlink("movie-two.mkv", secondId);

        var result = await LibraryLinkScanner.ScanAsync(fixture.Config);

        Assert.True(result.IsComplete, result.ErrorMessage);
        Assert.Equal(3, result.RawLinkCount);
        Assert.Equal([firstId, secondId], result.UniqueDavItemIds.Order());
    }

    [Fact]
    public async Task ScanFailsClosedWhenLibraryTraversalCannotStart()
    {
        var config = new ConfigManager();
        config.ApplyChanges(new Dictionary<string, string?>
        {
            ["media.library-dir"] = Path.Combine(Path.GetTempPath(), $"missing-library-{Guid.NewGuid():N}"),
        });

        var result = await LibraryLinkScanner.ScanAsync(config);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Links);
        Assert.NotEmpty(result.ErrorMessage!);
    }

    [Fact]
    public async Task ScanFailsClosedWhenAnNzbdavStrmIsMalformed()
    {
        using var fixture = new LibraryFixture();
        fixture.AddStrm("broken.strm", "not an absolute URL");

        var result = await LibraryLinkScanner.ScanAsync(fixture.Config);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Links);
        Assert.Contains("broken.strm", result.ErrorMessage);
    }

    [Fact]
    public async Task LinkLookupDistinguishesDiscoveryFailureFromMissingLink()
    {
        var config = new ConfigManager();
        config.ApplyChanges(new Dictionary<string, string?>
        {
            ["media.library-dir"] = Path.Combine(Path.GetTempPath(), $"missing-library-{Guid.NewGuid():N}"),
        });
        var davItem = new DavItem { Id = Guid.NewGuid(), Path = "/content/missing.mkv" };

        var result = await OrganizedLinksUtil.GetLinkAsync(davItem, config);

        Assert.False(result.IsComplete);
        Assert.Null(result.LinkPath);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task LinkLookupRescansWhenCachedLinkWasRemoved()
    {
        using var fixture = new LibraryFixture();
        var davItem = new DavItem
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Path = "/content/movie-three.mkv"
        };
        var linkPath = fixture.AddAbsoluteSymlink("movie-three.mkv", davItem.Id);
        var first = await OrganizedLinksUtil.GetLinkAsync(davItem, fixture.Config);
        Assert.Equal(linkPath, first.LinkPath);
        File.Delete(linkPath);

        var second = await OrganizedLinksUtil.GetLinkAsync(davItem, fixture.Config);

        Assert.True(second.IsComplete, second.ErrorMessage);
        Assert.Null(second.LinkPath);
    }

    private sealed class LibraryFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nzbdavex-library-scan-");
        private readonly string _mountDirectory;
        private readonly string _libraryDirectory;

        public LibraryFixture()
        {
            _mountDirectory = Directory.CreateDirectory(Path.Combine(_root.FullName, "mount")).FullName;
            _libraryDirectory = Directory.CreateDirectory(Path.Combine(_root.FullName, "library")).FullName;
            Config = new ConfigManager();
            Config.ApplyChanges(new Dictionary<string, string?>
            {
                ["media.library-dir"] = _libraryDirectory,
                ["rclone.mount-dir"] = _mountDirectory,
            });
        }

        public ConfigManager Config { get; }

        public string AddAbsoluteSymlink(string name, Guid davItemId)
        {
            var target = GetTarget(davItemId);
            var linkPath = Path.Combine(_libraryDirectory, name);
            File.CreateSymbolicLink(linkPath, target);
            return linkPath;
        }

        public void AddRelativeSymlink(string name, Guid davItemId)
        {
            var linkPath = Path.Combine(_libraryDirectory, name);
            var target = Path.GetRelativePath(_libraryDirectory, GetTarget(davItemId));
            File.CreateSymbolicLink(linkPath, target);
        }

        public void AddStrm(string name, string contents) =>
            File.WriteAllText(Path.Combine(_libraryDirectory, name), contents);

        private string GetTarget(Guid davItemId)
        {
            var parts = new[] { _mountDirectory, ".ids" }
                .Concat(davItemId.ToString()[..5].Select(x => x.ToString()))
                .Append(davItemId.ToString())
                .ToArray();
            return Path.Combine(parts);
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
