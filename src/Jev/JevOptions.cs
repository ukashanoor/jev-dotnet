namespace Jev;

/// <summary>Configuration for <see cref="JevClient"/>.</summary>
public sealed class JevOptions
{
    /// <summary>Official TypeSafe endpoint.</summary>
    public const string DefaultEndpoint = "https://api.typesafe.ai/v1/systemone";

    /// <summary>Bearer API key. Keys are host-specific (TypeSafe, jev-agent.com and OpenRouter keys are not interchangeable).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Full URL of the System One endpoint.</summary>
    public Uri Endpoint { get; set; } = new(DefaultEndpoint);

    /// <summary>Optional model route, e.g. <c>jev-latest</c>, <c>jev-preview</c> or <c>jev-1.13.0</c>. Omitted from the request when null.</summary>
    public string? Model { get; set; }

    /// <summary>Per-attempt timeout. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries after the first attempt for 408/429/5xx, network errors and timeouts. Default 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay for exponential backoff (doubles each retry, with jitter). Default 500ms.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for any single backoff or <c>Retry-After</c> wait. Default 10 seconds.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("JevOptions.ApiKey is required.", nameof(ApiKey));
        if (Endpoint is null || !Endpoint.IsAbsoluteUri)
            throw new ArgumentException("JevOptions.Endpoint must be an absolute URI.", nameof(Endpoint));
        if (MaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout));
    }
}
