# Jev for .NET

Unofficial, typed C# client for the [Jev](https://jevapi.io/) (TypeSafe System One) decision API.
Ask **Choice**, **Score** and **Noul** questions about any text and get typed answers with calibrated probabilities. Several questions cost one call.

| Package | Purpose |
|---|---|
| `Jev` | Client, typed models, retries (net8.0) |
| `Jev.AspNetCore` | `AddJev()`, `IHttpClientFactory`, config binding, health check |

```bash
dotnet add package Jev
dotnet add package Jev.AspNetCore   # optional
```

## Quick start

```csharp
using Jev;

var client = new JevClient(Environment.GetEnvironmentVariable("JEV_API_KEY")!);

var request = new JevRequest(ticketText)
    .AddChoice("team", "Which team should own this?", new Dictionary<string, string>
    {
        ["billing"] = "Payments, invoices, refunds",
        ["tech"]    = "Bugs, outages, errors",
    })
    .AddScore("severity", "How severe is the problem?", "Low", "Medium", "High", "Critical")
    .AddNoul("refund", "Is the customer asking for a refund?");

JevResponse res = await client.DecideAsync(request);

string team      = res.Choice("team").Choice;          // "billing"
double p         = res.Choice("team").Confidence ?? 0;
string? level    = res.Score("severity").Level;        // "High"  (Score = 2.4 probability-weighted)
bool wantsRefund = res.Noul("refund").IsTrue(0.7);     // P(true) >= 0.7
```

One-question shortcuts: `client.ChooseAsync(...)`, `client.ScoreAsync(...)`, `client.NoulAsync(...)`.

Enums work too: `request.AddChoice<Team>("team", "Which team?")` and `res.Choice("team").As<Team>()`.

## ASP.NET Core

```csharp
builder.Services.AddJev(builder.Configuration);   // binds the "Jev" section
builder.Services.AddHealthChecks().AddJevHealthCheck();
```

```json
{ "Jev": { "ApiKey": "…", "Model": "jev-latest", "MaxRetries": 3, "Timeout": "00:00:30" } }
```

Inject `IJevClient` anywhere. `AddJev` returns an `IHttpClientBuilder`, so you can add Polly, logging or a proxy handler. Options are validated on startup. Keep the key in user-secrets / environment, not in source.

## Options

| Option | Default | Notes |
|---|---|---|
| `ApiKey` | required | Bearer key. Keys are host-specific. |
| `Endpoint` | `https://api.typesafe.ai/v1/systemone` | Use `https://jev-agent.com/api/v1/systemone` for the free tier. |
| `Model` | none | `jev-latest`, `jev-preview`, `jev-1.13.0` |
| `Timeout` | 30s | Per attempt |
| `MaxRetries` | 3 | 408, 429, 5xx, network errors, timeouts. Exponential backoff with jitter; honours `Retry-After`. |

## Errors

`JevException` → `JevApiException` (`StatusCode`, `ResponseBody`) → `JevAuthenticationException` (401/403), `JevRateLimitException` (429, `RetryAfter`). Unexpected payloads raise `JevResponseException`.

## Build & publish

```bash
dotnet test
dotnet pack -c Release -o artifacts
dotnet nuget push artifacts/*.nupkg -k <NUGET_API_KEY> -s https://api.nuget.org/v3/index.json
```

Not affiliated with TypeSafe AI.

## License

MIT
