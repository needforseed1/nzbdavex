namespace NzbWebDAV.Api.SabControllers;

internal static class SabRequestSource
{
    internal const string StreamingApiKeyPrefix = "streaming:";
    internal const string StreamingSubmissionSource = "streaming";

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
}
