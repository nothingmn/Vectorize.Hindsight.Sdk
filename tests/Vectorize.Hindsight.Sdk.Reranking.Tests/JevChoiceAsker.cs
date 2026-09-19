#if JE_SDK_REFERENCED
using Jev.Sdk;
using Vectorize.Hindsight.Sdk.Reranking.TypeSafe;

namespace Vectorize.Hindsight.Sdk.Reranking.Tests;

/// <summary>
/// Adapts a real <see cref="JevClient"/> to the <see cref="IChoiceAsker"/> seam.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole cost of using the TypeSafe provider against the sibling client: one small
/// adapter, written once by the host. It lives here, behind a compilation symbol, to document the
/// exact shape and to prove the seam fits a real client — but it cannot be compiled in this
/// repository, because <c>Jev.Sdk</c> is not published to nuget.org and a package reference to it
/// would make this solution unrestorable for anyone else.
/// </para>
/// <para>
/// The provider itself deliberately takes no dependency on any TypeSafe client, which is what keeps
/// it publishable. Copy this class into a host that references both packages.
/// </para>
/// </remarks>
internal sealed class JevChoiceAsker : IChoiceAsker
{
    private readonly JevClient _client;
    private readonly string? _model;

    /// <summary>Creates the adapter.</summary>
    /// <param name="client">The TypeSafe client.</param>
    /// <param name="model">The model, or null for the client's default.</param>
    internal JevChoiceAsker(JevClient client, string? model = null)
    {
        _client = client;
        _model = model;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, double>> AskChoiceAsync(
        string instructions,
        string state,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        // One Choice question, whose options are the candidate labels the provider built. The
        // criteria names are what the answer's `probabilities` map is keyed by, so they are passed
        // through unmodified — a renamed option would break the mapping back to the candidates.
        var question = new ChoiceQuestion
        {
            Instructions = instructions,
            Criteria = options.ToDictionary(option => option.Key, option => option.Value, StringComparer.Ordinal),
        };

        var response = await _client.SystemOneAsync(
            state,
            new Dictionary<string, Question>(StringComparer.Ordinal) { ["rerank"] = question },
            _model,
            cancellationToken).ConfigureAwait(false);

        if (response.Answers is null || !response.Answers.TryGetValue("rerank", out var answer))
        {
            throw new InvalidOperationException(
                "The TypeSafe response contained no answer under the 'rerank' question id.");
        }

        if (answer is not ChoiceAnswer choice)
        {
            // The answer type must match the question type that was asked; anything else means the
            // response was misread and attaching its numbers to candidates would be wrong.
            throw new InvalidOperationException(
                $"Expected a choice answer but received '{answer.Type}'.");
        }

        return choice.Probabilities;
    }
}
#endif
