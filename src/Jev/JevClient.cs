using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Jev;

/// <summary>Sends typed decision requests to the Jev API.</summary>
public interface IJevClient
{
    Task<JevResponse> DecideAsync(JevRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP client for the Jev System One API with timeouts and retries (408/429/5xx, network errors; honours <c>Retry-After</c>).
/// Thread-safe; register as a singleton or via <c>AddJev()</c>.
/// </summary>
public sealed class JevClient : IJevClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly JevOptions _options;

    public JevClient(string apiKey) : this(new JevOptions { ApiKey = apiKey }) { }

    public JevClient(JevOptions options) : this(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan }, options, ownsHttp: true) { }

    /// <summary>Use an externally managed <see cref="HttpClient"/> (e.g. from IHttpClientFactory).</summary>
    public JevClient(HttpClient httpClient, JevOptions options) : this(httpClient, options, ownsHttp: false) { }

    private JevClient(HttpClient http, JevOptions options, bool ownsHttp)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _http = http;
        _options = options;
        _ownsHttp = ownsHttp;
    }

    public async Task<JevResponse> DecideAsync(JevRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(request.ToWireObject(_options.Model), Json);

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            JevException? failure;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.Timeout);

                using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode) return JevResponse.Parse(body);

                retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
                failure = ToException(response.StatusCode, body, retryAfter);
                if (!IsRetryable(response.StatusCode)) throw failure;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new JevException($"Request timed out after {_options.Timeout.TotalSeconds:0.#}s.");
            }
            catch (HttpRequestException ex)
            {
                failure = new JevException($"Network error: {ex.Message}", ex);
            }

            if (attempt >= _options.MaxRetries) throw failure;
            await Task.Delay(BackoffFor(attempt, retryAfter), cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan BackoffFor(int attempt, TimeSpan? retryAfter)
    {
        var delay = retryAfter ?? TimeSpan.FromTicks((long)(_options.RetryBaseDelay.Ticks * Math.Pow(2, attempt) * (0.75 + Random.Shared.NextDouble() * 0.5)));
        return delay > _options.RetryMaxDelay ? _options.RetryMaxDelay : delay;
    }

    private static bool IsRetryable(HttpStatusCode s) =>
        s == HttpStatusCode.RequestTimeout || s == HttpStatusCode.TooManyRequests || (int)s >= 500;

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? h)
    {
        if (h is null) return null;
        if (h.Delta is { } d) return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        if (h.Date is { } when) { var w = when - DateTimeOffset.UtcNow; return w < TimeSpan.Zero ? TimeSpan.Zero : w; }
        return null;
    }

    private static JevApiException ToException(HttpStatusCode status, string body, TimeSpan? retryAfter)
    {
        var message = ExtractMessage(body) ?? status.ToString();
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new JevAuthenticationException(status, message, body),
            HttpStatusCode.TooManyRequests => new JevRateLimitException(message, body, retryAfter),
            _ => new JevApiException(status, message, body),
        };
    }

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "error", "message", "detail" })
            {
                if (!doc.RootElement.TryGetProperty(name, out var e)) continue;
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
            }
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? null : body.Length > 300 ? body[..300] : body;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
