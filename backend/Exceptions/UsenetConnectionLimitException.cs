namespace NzbWebDAV.Exceptions;

public class UsenetConnectionLimitException(string message) : RetryableDownloadException(message)
{
}
