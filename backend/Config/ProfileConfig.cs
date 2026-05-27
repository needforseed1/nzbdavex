namespace NzbWebDAV.Config;

public class ProfileConfig
{
    public List<Profile> Profiles { get; set; } = [];

    public class Profile
    {
        public required string Token { get; set; }
        public required string Name { get; set; }
        public List<string> IndexerNames { get; set; } = [];
        public List<string>? EnabledAdapters { get; set; }

        // When > 0 and the ID-based pass returns fewer than this many results,
        // run a second text-query pass and merge. 0 disables the fallback.
        public int QueryFallbackMinResults { get; set; } = 0;
    }
}
