using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Services;

public class PlexPathResolverTests
{
    [Theory]
    [InlineData("/mnt/nzbdav/.ids/ab/cd/149706ed-b23d-4c27-b03e-ee198c01bf13")]
    [InlineData("http://nzbdav/view/.ids/ab/cd/149706ed-b23d-4c27-b03e-ee198c01bf13")]
    public void ExtractsOnlyExplicitIdsPaths(string path)
    {
        Assert.True(PlexPathResolver.TryExtractDavItemId(path, out var id));
        Assert.Equal(Guid.Parse("149706ed-b23d-4c27-b03e-ee198c01bf13"), id);
    }

    [Theory]
    [InlineData("/media/Movies/149706ed-b23d-4c27-b03e-ee198c01bf13.mkv")]
    [InlineData("/media/.ids-not-really/149706ed-b23d-4c27-b03e-ee198c01bf13")]
    [InlineData("Monsters Inc")]
    public void DoesNotGuessIdentityFromNamesOrUnrelatedGuids(string path)
    {
        Assert.False(PlexPathResolver.TryExtractDavItemId(path, out _));
    }

    [Fact]
    public void PrefixMappingRequiresAPathBoundary()
    {
        Assert.Equal(
            Path.GetFullPath("/local/Movies/Example.mkv"),
            PlexPathResolver.MapToLocalPath(
                "/plex/Movies/Example.mkv", "/plex", "/local"));
        Assert.Null(PlexPathResolver.MapToLocalPath(
            "/plex-other/Example.mkv", "/plex", "/local"));
        Assert.Null(PlexPathResolver.MapToLocalPath(
            "/plex/Movies/Example.mkv", "/plex", null));
    }
}
