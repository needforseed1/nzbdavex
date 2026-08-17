using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class FilenameMatcherTests
{
    public static TheoryData<string, int?, int?, int?> AnimeEpisodeExamples => new()
    {
        {
            "4[SubsPlease] Mushoku Tensei S3 - 08 (1080p) [0161AAEA].mkv",
            3, 8, null
        },
        {
            "BLEACH Thousand Year Blood War S01E44 THE PERFECT CRIMSON 1080p DSNP WEB-DL AAC2.0 H.264-VARYG",
            1, 44, null
        },
        {
            "[Erai-raws] Mushoku Tensei III: Isekai Ittara Honki Dasu - 08 [1080p CR WEB-DL AVC AAC][MultiSub][21DB5819]",
            null, 8, null
        },
        {
            "[Erai-raws] Tensei Shitara Slime Datta Ken 4th Season - 17 [1080p CR WEB-DL AVC AAC]",
            4, 17, null
        },
        {
            "[ToonsHub] One Piece EP1174 1080p TVER WEB-DL AAC2.0 H.264 (Japanese Sub)",
            null, 1174, null
        },
        {
            "[SubsPlease] One Piece - 1174 (1080p) [B4711849].mkv",
            null, 1174, null
        },
        {
            "[Knight-Subs] Bleach Thousand-Year Blood War - E44v2 - THE PERFECT CRIMSON (1080p CR WEB-DL DDP2.0 H.264)",
            null, 44, null
        },
        {
            "3[Some-Stuffs] Pocket Monsters (2023) 145 (1080p) [C282D06B]",
            null, 145, null
        },
        {
            "42[Anime Time] One Piece (0001-1071+Movies+Specials) [BD+CR] [1080p]",
            null, 1, 1071
        },
        {
            "[Group] Show - 01-02 [1080p]",
            null, 1, 2
        },
        {
            "[Almighty].Bleach.Sennen.Kessen.Hen-14.[BD.1920x1080.x264.10bit.FLAC]",
            null, 14, null
        },
        {
            "[Asakura] Tensei Shitara Slime Datta Ken 4th Season - 18 [1080p WEB AAC x264] | Episode 90",
            4, 18, null
        },
        {
            "[SubsPlease] Re Zero kara Hajimeru Isekai Seikatsu - 78 (1080p) [30D08902].mkv",
            null, 78, null
        },
        {
            "[SubsPlease] Youjo Senki S2 - 06 (1080p) [45710242].mkv",
            2, 6, null
        },
    };

    [Theory]
    [MemberData(nameof(AnimeEpisodeExamples))]
    public void ParseEpisodeRecognizesAnimeReleaseConventions(
        string title, int? expectedSeason, int? expectedEpisode, int? expectedEnd)
    {
        var tag = FilenameMatcher.ParseEpisode(title);

        Assert.NotNull(tag);
        Assert.Equal(expectedSeason, tag.Value.Season);
        Assert.Equal(expectedEpisode, tag.Value.Episode);
        Assert.Equal(expectedEnd, tag.Value.EpisodeEnd);
    }

    [Theory]
    [InlineData("4[SubsPlease] Show (1080p) [0161AAEA].mkv")]
    [InlineData("[Group-01] Show (2026) [1080p] [12345678]")]
    [InlineData("86 Eighty-Six (2026) (1080p)")]
    [InlineData("Mob Psycho 100 III 1080p H.264")]
    [InlineData("Show - 2026 (1080p)")]
    [InlineData("Show - 1080p WEB-DL")]
    [InlineData("Movie (2023-2026) 1080p")]
    [InlineData("Feature H.264 x265 10bit 5.1")]
    public void ParseEpisodeIgnoresTechnicalAndTitleNumbers(string title)
    {
        Assert.Null(FilenameMatcher.ParseEpisode(title));
    }

    [Fact]
    public void EpisodeCompatibleMatchesAbsoluteEpisodeWithoutAssumingASeason()
    {
        const string title = "[Judas] Bleach - 366";

        Assert.True(FilenameMatcher.EpisodeCompatible(title, season: 16, episode: 366));
        Assert.False(FilenameMatcher.EpisodeCompatible(title, season: 16, episode: 365));
    }

    [Fact]
    public void EpisodeCompatibleStillEnforcesExplicitSeasonNumbers()
    {
        const string title = "[AnoZu] Mushoku Tensei S03E08 1080p WEB-DL";

        Assert.True(FilenameMatcher.EpisodeCompatible(title, season: 3, episode: 8));
        Assert.False(FilenameMatcher.EpisodeCompatible(title, season: 2, episode: 8));
    }

    [Fact]
    public void EpisodeCompatibleUnderstandsAbsoluteBatchRanges()
    {
        const string title = "[Anime Time] One Piece (0001-1071+Movies+Specials) [1080p]";

        Assert.True(FilenameMatcher.EpisodeCompatible(title, season: 1, episode: 500));
        Assert.False(FilenameMatcher.EpisodeCompatible(title, season: 1, episode: 1174));
    }
}
