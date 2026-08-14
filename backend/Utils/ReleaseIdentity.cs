using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Utils;

public static class ReleaseIdentity
{
    public static string Key(long size, string? poster, DateTimeOffset? usenetDate, string? nzbUrl)
    {
        var fingerprint = Fingerprint(size, poster, usenetDate);
        if (fingerprint is not null) return fingerprint;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nzbUrl ?? string.Empty));
        return "rk1:" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static string? Fingerprint(long size, string? poster, DateTimeOffset? usenetDate)
    {
        if (size <= 0) return null;

        var hasPoster = !string.IsNullOrWhiteSpace(poster);
        var hasDate = usenetDate.HasValue;
        if (!hasPoster && !hasDate) return null;

        var posterNorm = hasPoster ? poster!.Trim().ToLowerInvariant() : "";
        var dayBucket = hasDate ? usenetDate!.Value.ToUnixTimeSeconds() / 86400 : 0;
        var canonical = $"{size}|{posterNorm}|{dayBucket}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "rk1:" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
