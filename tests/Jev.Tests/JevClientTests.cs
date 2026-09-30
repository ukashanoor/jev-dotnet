using System.Net;
using System.Text;
using System.Text.Json;
using Jev;
using Jev.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jev.Tests;

public class JevClientTests
{
    private const string OkBody = """
    {
      "model": "jev-1.13.0",
      "answers": {
        "team":  { "type": "choice", "choice": "billing", "confidence": 0.91, "probabilities": { "billing": 0.91, "tech": 0.09 } },
        "sev":   { "type": "score", "score": 2.4, "confidence": 0.7, "legend": { "0": "Low", "1": "Medium", "2": "High", "3": "Critical" }, "probabilities": { "0": 0.0, "1": 0.1, "2": 0.5, "3": 0.4 } },
        "refund":{ "type": "noul", "noul": 0.82 }
      },
      "usage": { "input_tokens": 120, "output_tokens": 8 },
      "quota": { "used": 5, "limit": 100, "remaining": 95 }
    }
    """;

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public List<string> Bodies { get; } = new();
        public HttpRequestMessage? Last;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request, Calls++);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static JevClient Client(StubHandler h, Action<JevOptions>? cfg = null)
    {
        var o = new JevOptions { ApiKey = "k", RetryBaseDelay = TimeSpan.Zero, MaxRetries = 2 };
        cfg?.Invoke(o);
        return new JevClient(new HttpClient(h), o);
    }

    private static JevRequest Request() => new JevRequest("ticket text")
        .AddChoice("team", "Which team?", new Dictionary<string, string> { ["billing"] = "money", ["tech"] = "bugs" })
        .AddScore("sev", "How severe?", "Low", "Medium", "High", "Critical")
        .AddNoul("refund", "Wants a refund?");

    [Fact]
    public async Task Sends_expected_wire_format_and_parses_typed_answers()
    {
        var h = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var res = await Client(h, o => o.Model = "jev-latest").DecideAsync(Request());

        Assert.Equal("Bearer k", h.Last!.Headers.Authorization!.ToString());
        Assert.Equal(JevOptions.DefaultEndpoint, h.Last.RequestUri!.ToString());

        using var sent = JsonDocument.Parse(h.Bodies[0]);
        var root = sent.RootElement;
        Assert.Equal("ticket text", root.GetProperty("state").GetString());
        Assert.Equal("jev-latest", root.GetProperty("model").GetString());
        var q = root.GetProperty("questions");
        Assert.Equal("choice", q.GetProperty("team").GetProperty("type").GetString());
        Assert.Equal("money", q.GetProperty("team").GetProperty("criteria").GetProperty("billing").GetString());
        Assert.Equal(4, q.GetProperty("sev").GetProperty("criteria").GetArrayLength());
        Assert.Equal("Yes", q.GetProperty("refund").GetProperty("criteria").GetProperty("true").GetString());

        var team = res.Choice("team");
        Assert.Equal("billing", team.Choice);
        Assert.Equal(0.91, team.ProbabilityOf("billing"));
        var sev = res.Score("sev");
        Assert.Equal(2.4, sev.Score);
        Assert.Equal("High", sev.Level);
        Assert.Equal(0.8, sev.Normalized!.Value, 3);
        Assert.True(res.Noul("refund").IsTrue());
        Assert.Equal(120, res.Usage!.InputTokens);
        Assert.Equal(95, res.Quota!.Remaining);
    }

    [Fact]
    public async Task Wrong_answer_type_or_missing_id_throws()
    {
        var res = await Client(new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody))).DecideAsync(Request());
        Assert.Throws<JevResponseException>(() => res.Score("team"));
        Assert.Throws<JevResponseException>(() => res.Choice("nope"));
    }

    private enum Team { Billing, Tech }

    [Fact]
    public async Task Choice_maps_to_enum()
    {
        var res = await Client(new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody))).DecideAsync(Request());
        Assert.Equal(Team.Billing, res.Choice("team").As<Team>());
    }

    [Fact]
    public async Task Retries_on_5xx_then_succeeds()
    {
        var h = new StubHandler((_, n) => n < 2 ? Json(HttpStatusCode.BadGateway, "{}") : Json(HttpStatusCode.OK, OkBody));
        var res = await Client(h).DecideAsync(Request());
        Assert.Equal(3, h.Calls);
        Assert.Equal("billing", res.Choice("team").Choice);
    }

    [Fact]
    public async Task Does_not_retry_auth_errors()
    {
        var h = new StubHandler((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
        var ex = await Assert.ThrowsAsync<JevAuthenticationException>(() => Client(h).DecideAsync(Request()));
        Assert.Equal(1, h.Calls);
        Assert.Contains("bad key", ex.Message);
    }

    [Fact]
    public async Task Rate_limit_exhausts_retries_and_surfaces_retry_after()
    {
        var h = new StubHandler((_, _) =>
        {
            var r = Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        });
        var ex = await Assert.ThrowsAsync<JevRateLimitException>(() => Client(h).DecideAsync(Request()));
        Assert.Equal(3, h.Calls);
        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        var h = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(h).DecideAsync(Request(), cts.Token));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Request_validation()
    {
        Assert.Throws<ArgumentException>(() => new JevRequest("s").AddScore("a", "x", "only"));
        Assert.Throws<ArgumentException>(() => new JevRequest("s").AddNoul("a", "x").AddNoul("a", "y"));
        var h = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(h).DecideAsync(new JevRequest("s")));
        Assert.Equal(0, h.Calls);
        Assert.Throws<ArgumentException>(() => new JevClient(new JevOptions { ApiKey = "" }));
    }

    [Fact]
    public async Task One_question_extensions()
    {
        var h = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"answers":{"q":{"type":"noul","noul":0.1}}}"""));
        var a = await Client(h).NoulAsync("text", "Is it spam?");
        Assert.False(a.IsTrue());
    }

    [Fact]
    public void AddJev_registers_client_and_validates_options()
    {
        var services = new ServiceCollection();
        services.AddJev(o => o.ApiKey = "k");
        using var sp = services.BuildServiceProvider();
        Assert.IsType<JevClient>(sp.GetRequiredService<IJevClient>());
    }
}
