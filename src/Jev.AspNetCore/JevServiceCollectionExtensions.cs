using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Jev.AspNetCore;

public static class JevServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IJevClient"/> via IHttpClientFactory, configured from code.</summary>
    public static IHttpClientBuilder AddJev(this IServiceCollection services, Action<JevOptions> configure)
    {
        AddOptions(services).Configure(configure);
        return AddClient(services);
    }

    /// <summary>Registers <see cref="IJevClient"/> configured from a config section (default <c>"Jev"</c>).</summary>
    public static IHttpClientBuilder AddJev(this IServiceCollection services, IConfiguration configuration, string section = "Jev")
    {
        AddOptions(services).Bind(configuration.GetSection(section));
        return AddClient(services);
    }

    /// <summary>Adds a health check that verifies configuration only (no paid API call is made).</summary>
    public static IHealthChecksBuilder AddJevHealthCheck(this IHealthChecksBuilder builder, string name = "jev") =>
        builder.AddCheck<JevHealthCheck>(name);

    private static OptionsBuilder<JevOptions> AddOptions(IServiceCollection services) =>
        services.AddOptions<JevOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Jev:ApiKey is required.")
            .Validate(o => o.Endpoint is { IsAbsoluteUri: true }, "Jev:Endpoint must be an absolute URI.")
            .ValidateOnStart();

    private static IHttpClientBuilder AddClient(IServiceCollection services) =>
        services.AddHttpClient("Jev", http =>
                http.Timeout = System.Threading.Timeout.InfiniteTimeSpan) // JevClient enforces its own per-attempt timeout
            .AddTypedClient<IJevClient>((http, sp) =>
                new JevClient(http, sp.GetRequiredService<IOptions<JevOptions>>().Value));
}

internal sealed class JevHealthCheck(IOptions<JevOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        return Task.FromResult(string.IsNullOrWhiteSpace(o.ApiKey)
            ? HealthCheckResult.Unhealthy("Jev API key is not configured.")
            : HealthCheckResult.Healthy($"Jev configured for {o.Endpoint.Host}."));
    }
}
