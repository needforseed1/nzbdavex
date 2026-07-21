namespace NzbWebDAV.Exceptions;

public class NonRetryableDownloadException : Exception
{
    public NonRetryableDownloadException(string message) : base(message)
    {
    }

    public NonRetryableDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
