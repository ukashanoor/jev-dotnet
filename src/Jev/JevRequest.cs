namespace Jev;

/// <summary>The kind of question asked.</summary>
public enum QuestionType { Choice, Score, Noul }

/// <summary>A typed question. Create via <see cref="JevRequest"/>'s Add* methods.</summary>
public abstract record JevQuestion(string Instructions)
{
    public abstract QuestionType Type { get; }
    internal abstract object Criteria { get; }
}

/// <summary>Pick exactly one option. Keys are option ids; values say when to pick each.</summary>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Options) : JevQuestion(Instructions)
{
    public override QuestionType Type => QuestionType.Choice;
    internal override object Criteria => Options;
}

/// <summary>Rate against ordered rubric levels, lowest first.</summary>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels) : JevQuestion(Instructions)
{
    public override QuestionType Type => QuestionType.Score;
    internal override object Criteria => Levels;
}

/// <summary>Yes/no probability for a statement.</summary>
public sealed record NoulQuestion(string Instructions, string TrueMeaning = "Yes", string FalseMeaning = "No") : JevQuestion(Instructions)
{
    public override QuestionType Type => QuestionType.Noul;
    internal override object Criteria => new Dictionary<string, string> { ["true"] = TrueMeaning, ["false"] = FalseMeaning };
}

/// <summary>
/// A state (any text) plus one or more named questions. All questions are answered in a single call.
/// </summary>
public sealed class JevRequest
{
    private readonly Dictionary<string, JevQuestion> _questions = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public JevRequest(string state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
    }

    public string State { get; }

    /// <summary>Per-request model override. Falls back to <see cref="JevOptions.Model"/>.</summary>
    public string? Model { get; set; }

    public IReadOnlyList<KeyValuePair<string, JevQuestion>> Questions =>
        _order.Select(id => KeyValuePair.Create(id, _questions[id])).ToList();

    public JevRequest AddChoice(string id, string instructions, IReadOnlyDictionary<string, string> options)
    {
        if (options is null || options.Count < 2)
            throw new ArgumentException("A choice question needs at least two options.", nameof(options));
        return Add(id, new ChoiceQuestion(instructions, options));
    }

    /// <summary>Choice from an enum: each member becomes an option key, described by <paramref name="descriptions"/> (falls back to the member name).</summary>
    public JevRequest AddChoice<TEnum>(string id, string instructions, IReadOnlyDictionary<TEnum, string>? descriptions = null)
        where TEnum : struct, Enum
    {
        var options = Enum.GetValues<TEnum>().ToDictionary(
            v => v.ToString(),
            v => descriptions is not null && descriptions.TryGetValue(v, out var d) ? d : v.ToString());
        return AddChoice(id, instructions, options);
    }

    public JevRequest AddScore(string id, string instructions, params string[] levels)
    {
        if (levels is null || levels.Length < 2)
            throw new ArgumentException("A score question needs at least two levels.", nameof(levels));
        return Add(id, new ScoreQuestion(instructions, levels));
    }

    public JevRequest AddNoul(string id, string instructions, string trueMeaning = "Yes", string falseMeaning = "No") =>
        Add(id, new NoulQuestion(instructions, trueMeaning, falseMeaning));

    private JevRequest Add(string id, JevQuestion question)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Question id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(question.Instructions)) throw new ArgumentException("Instructions are required.", nameof(question));
        if (!_questions.TryAdd(id, question)) throw new ArgumentException($"Duplicate question id '{id}'.", nameof(id));
        _order.Add(id);
        return this;
    }

    internal Dictionary<string, object?> ToWireObject(string? defaultModel)
    {
        if (_order.Count == 0) throw new InvalidOperationException("Add at least one question before sending.");

        var questions = new Dictionary<string, object>();
        foreach (var id in _order)
        {
            var q = _questions[id];
            questions[id] = new Dictionary<string, object>
            {
                ["type"] = q.Type.ToString().ToLowerInvariant(),
                ["instructions"] = q.Instructions,
                ["criteria"] = q.Criteria,
            };
        }

        var body = new Dictionary<string, object?> { ["state"] = State, ["questions"] = questions };
        var model = Model ?? defaultModel;
        if (model is not null) body["model"] = model;
        return body;
    }
}
