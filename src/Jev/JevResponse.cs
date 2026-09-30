using System.Globalization;
using System.Text.Json;

namespace Jev;

/// <summary>An answer to one question. Pattern-match on the concrete type.</summary>
public abstract record JevAnswer(double? Confidence);

/// <summary>Result of a choice question.</summary>
public sealed record ChoiceAnswer(string Choice, double? Confidence, IReadOnlyDictionary<string, double> Probabilities)
    : JevAnswer(Confidence)
{
    /// <summary>Parse the chosen key into an enum (case-insensitive).</summary>
    public TEnum As<TEnum>() where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Choice, ignoreCase: true, out var v)
            ? v
            : throw new JevResponseException($"Choice '{Choice}' is not a member of {typeof(TEnum).Name}.");

    /// <summary>Probability of a specific option (0 if absent).</summary>
    public double ProbabilityOf(string option) => Probabilities.TryGetValue(option, out var p) ? p : 0;
}

/// <summary>Result of a score question. <see cref="Score"/> is the probability-weighted level index in [0, n-1].</summary>
public sealed record ScoreAnswer(
    double Score,
    double? Confidence,
    IReadOnlyDictionary<int, string> Legend,
    IReadOnlyDictionary<int, double> Probabilities) : JevAnswer(Confidence)
{
    /// <summary>Label of the nearest rubric level, or null if no legend was returned.</summary>
    public string? Level => Legend.TryGetValue((int)Math.Round(Score, MidpointRounding.AwayFromZero), out var l) ? l : null;

    /// <summary>Score scaled to [0, 1] using the legend size (null when the legend is empty or single-level).</summary>
    public double? Normalized => Legend.Count > 1 ? Score / (Legend.Count - 1) : null;
}

/// <summary>Result of a noul (yes/no) question. <see cref="Probability"/> is P(true) in [0, 1].</summary>
public sealed record NoulAnswer(double Probability, double? Confidence) : JevAnswer(Confidence)
{
    public bool IsTrue(double threshold = 0.5) => Probability >= threshold;
}

public sealed record JevUsage(int InputTokens, int OutputTokens);

public sealed record JevQuota(int Used, int Limit, int Remaining);

/// <summary>Typed response to a <see cref="JevRequest"/>.</summary>
public sealed class JevResponse
{
    public string? Model { get; init; }
    public IReadOnlyDictionary<string, JevAnswer> Answers { get; init; } = new Dictionary<string, JevAnswer>();
    public JevUsage? Usage { get; init; }
    public JevQuota? Quota { get; init; }

    public ChoiceAnswer Choice(string id) => Get<ChoiceAnswer>(id);
    public ScoreAnswer Score(string id) => Get<ScoreAnswer>(id);
    public NoulAnswer Noul(string id) => Get<NoulAnswer>(id);

    public T Get<T>(string id) where T : JevAnswer
    {
        if (!Answers.TryGetValue(id, out var a))
            throw new JevResponseException($"No answer returned for question '{id}'.");
        return a as T ?? throw new JevResponseException(
            $"Question '{id}' returned {a.GetType().Name}, expected {typeof(T).Name}.");
    }

    internal static JevResponse Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JevResponseException("Response was not a JSON object.");

            var answers = new Dictionary<string, JevAnswer>();
            if (root.TryGetProperty("answers", out var ans) && ans.ValueKind == JsonValueKind.Object)
                foreach (var p in ans.EnumerateObject())
                    answers[p.Name] = ParseAnswer(p.Name, p.Value);

            return new JevResponse
            {
                Model = Str(root, "model"),
                Answers = answers,
                Usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                    ? new JevUsage(Int(u, "input_tokens"), Int(u, "output_tokens")) : null,
                Quota = root.TryGetProperty("quota", out var q) && q.ValueKind == JsonValueKind.Object
                    ? new JevQuota(Int(q, "used"), Int(q, "limit"), Int(q, "remaining")) : null,
            };
        }
        catch (JsonException ex)
        {
            throw new JevResponseException("Response was not valid JSON.", ex);
        }
    }

    private static JevAnswer ParseAnswer(string id, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new JevResponseException($"Answer '{id}' was not an object.");

        var type = Str(e, "type")?.ToLowerInvariant()
            ?? (e.TryGetProperty("choice", out _) ? "choice"
              : e.TryGetProperty("score", out _) ? "score"
              : e.TryGetProperty("noul", out _) ? "noul" : null);
        var confidence = Dbl(e, "confidence");

        switch (type)
        {
            case "choice":
                return new ChoiceAnswer(
                    Str(e, "choice") ?? throw new JevResponseException($"Answer '{id}' has no 'choice'."),
                    confidence,
                    Map(e, "probabilities", k => k, v => v));
            case "score":
                return new ScoreAnswer(
                    Dbl(e, "score") ?? throw new JevResponseException($"Answer '{id}' has no 'score'."),
                    confidence,
                    Legend(e),
                    IntKeyed(Map(e, "probabilities", k => k, v => v)));
            case "noul":
                return new NoulAnswer(
                    Dbl(e, "noul") ?? Dbl(e, "probability") ?? throw new JevResponseException($"Answer '{id}' has no 'noul'."),
                    confidence);
            default:
                throw new JevResponseException($"Answer '{id}' has unknown type '{type}'.");
        }
    }

    // legend may arrive as {"0":"Low",...} or ["Low",...]
    private static IReadOnlyDictionary<int, string> Legend(JsonElement e)
    {
        var result = new Dictionary<int, string>();
        if (!e.TryGetProperty("legend", out var l)) return result;
        if (l.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in l.EnumerateArray()) result[i++] = item.ToString();
        }
        else if (l.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in l.EnumerateObject())
                if (int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k))
                    result[k] = p.Value.ToString();
        }
        return result;
    }

    private static IReadOnlyDictionary<int, double> IntKeyed(IReadOnlyDictionary<string, double> src)
    {
        var d = new Dictionary<int, double>();
        foreach (var (k, v) in src)
            if (int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) d[i] = v;
        return d;
    }

    private static IReadOnlyDictionary<string, double> Map(JsonElement e, string name, Func<string, string> key, Func<double, double> val)
    {
        var d = new Dictionary<string, double>();
        if (e.TryGetProperty(name, out var p))
        {
            if (p.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in p.EnumerateObject())
                    if (kv.Value.ValueKind == JsonValueKind.Number) d[key(kv.Name)] = val(kv.Value.GetDouble());
            }
            else if (p.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in p.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number) d[i.ToString(CultureInfo.InvariantCulture)] = val(item.GetDouble());
                    i++;
                }
            }
        }
        return d;
    }

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Dbl(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int Int(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
