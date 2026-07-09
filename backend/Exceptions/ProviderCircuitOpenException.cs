namespace NzbWebDAV.Exceptions;

public sealed class ProviderCircuitOpenException(string provider)
    : RetryableDownloadException($"Usenet provider {provider} is temporarily unavailable.")
{
}
