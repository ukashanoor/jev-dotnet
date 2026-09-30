namespace Jev;

/// <summary>One-question shortcuts over <see cref="IJevClient"/>.</summary>
public static class JevClientExtensions
{
    private const string Id = "q";

    public static async Task<ChoiceAnswer> ChooseAsync(this IJevClient client, string state, string instructions,
        IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken = default) =>
        (await client.DecideAsync(new JevRequest(state).AddChoice(Id, instructions, options), cancellationToken).ConfigureAwait(false)).Choice(Id);

    public static async Task<ScoreAnswer> ScoreAsync(this IJevClient client, string state, string instructions,
        IReadOnlyList<string> levels, CancellationToken cancellationToken = default) =>
        (await client.DecideAsync(new JevRequest(state).AddScore(Id, instructions, levels.ToArray()), cancellationToken).ConfigureAwait(false)).Score(Id);

    public static async Task<NoulAnswer> NoulAsync(this IJevClient client, string state, string statement,
        CancellationToken cancellationToken = default) =>
        (await client.DecideAsync(new JevRequest(state).AddNoul(Id, statement), cancellationToken).ConfigureAwait(false)).Noul(Id);
}
