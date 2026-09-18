using System.Net;

namespace Kiseki.Core.Services;

public class JitenHttpException : HttpRequestException
{
    public TimeSpan? RetryAfter { get; }

    public JitenHttpException(
        string message,
        HttpStatusCode statusCode,
        TimeSpan? retryAfter = null,
        Exception? inner = null)
        : base(message, inner, statusCode)
    {
        RetryAfter = retryAfter;
    }
}

