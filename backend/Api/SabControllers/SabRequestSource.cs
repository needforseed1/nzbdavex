namespace NzbWebDAV.Api.SabControllers;

internal static class SabRequestSource
{
    internal const string StreamingApiKeyPrefix = "streaming:";
    internal const string StreamingSubmissionSource = "streaming";
    internal const string SonarrSubmissionSource = "sonarr";
    internal const string RadarrSubmissionSource = "radarr";

    internal static bool IsValidApiKey(
        string? suppliedApiKey,
        string configuredApiKey,
        string frontendApiKey)
    {
        return suppliedApiKey == configuredApiKey
               || suppliedApiKey == frontendApiKey
               || IsStreamingApiKey(suppliedApiKey, configuredApiKey);
    }

    internal static bool IsStreamingApiKey(string? suppliedApiKey, string configuredApiKey)
    {
        return suppliedApiKey == $"{StreamingApiKeyPrefix}{configuredApiKey}";
    }

    internal static string? GetSubmissionSource(
        string? suppliedApiKey,
        string configuredApiKey,
        string? userAgent)
    {
        // The dedicated streaming key is explicit and therefore takes
        // precedence over the less authoritative HTTP product name.
        if (IsStreamingApiKey(suppliedApiKey, configuredApiKey))
            return StreamingSubmissionSource;

        return GetArrSubmissionSource(userAgent);
    }

    internal static string? GetArrSubmissionSource(string? userAgent)
    {
        if (HasProductPrefix(userAgent, "Sonarr"))
            return SonarrSubmissionSource;
        if (HasProductPrefix(userAgent, "Radarr"))
            return RadarrSubmissionSource;
        return null;
    }

    private static bool HasProductPrefix(string? userAgent, string product)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return false;

        var value = userAgent.TrimStart();
        if (!value.StartsWith(product, StringComparison.OrdinalIgnoreCase))
            return false;

        return value.Length == product.Length ||
               value[product.Length] is '/' or ' ' or '(';
    }
}
