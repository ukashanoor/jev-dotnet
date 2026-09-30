using System.Net;

namespace Jev;

/// <summary>Base class for all errors raised by the Jev client.</summary>
public class JevException : Exception
{
    public JevException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The API returned a non-success status code.</summary>
public class JevApiException : JevException
{
    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }

    public JevApiException(HttpStatusCode statusCode, string message, string? responseBody)
        : base($"Jev API error {(int)statusCode} {statusCode}: {message}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

/// <summary>401/403: missing, invalid or wrong-host API key.</summary>
public sealed class JevAuthenticationException : JevApiException
{
    public JevAuthenticationException(HttpStatusCode statusCode, string message, string? body)
        : base(statusCode, message, body) { }
}

/// <summary>429: rate limit or quota exhausted (after retries).</summary>
public sealed class JevRateLimitException : JevApiException
{
    public TimeSpan? RetryAfter { get; }

    public JevRateLimitException(string message, string? body, TimeSpan? retryAfter)
        : base(HttpStatusCode.TooManyRequests, message, body) => RetryAfter = retryAfter;
}

/// <summary>The response was missing an answer or had an unexpected shape.</summary>
public sealed class JevResponseException : JevException
{
    public JevResponseException(string message, Exception? inner = null) : base(message, inner) { }
}
