namespace Vectorize.Hindsight.Sdk.Reranking.TypeSafe;

/// <summary>
/// The one TypeSafe question a reranker needs answered, expressed against a seam rather than a
/// client type.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the TypeSafe provider must not hard-depend on one particular TypeSafe client.
/// <c>Jev.Sdk</c> is the sibling client in this family and is not published to nuget.org, and a
/// published package that references an unpublished one cannot be restored by anyone else. So the
/// provider depends on <i>the shape of the answer</i>, and a host adapts its client — Jev.Sdk, the
/// vendor's own SDK, or a hand-rolled <c>HttpClient</c> call — with a few lines.
/// </para>
/// <para>
/// The seam is deliberately the smallest thing this reranker needs: one question in, one
/// distribution out. That keeps the adaptation trivial and means a change to the vendor's SDK does
/// not force a new release of this package.
/// </para>
/// </remarks>
public interface IChoiceAsker
{
    /// <summary>
    /// Asks which of <paramref name="options"/> the passage best matches, relative to the query.
    /// </summary>
    /// <param name="instructions">
    /// The complete question. The provider writes a specific one; an implementation should pass it
    /// through unmodified, because the wording is the judgement.
    /// </param>
    /// <param name="state">The content to evaluate.</param>
    /// <param name="options">
    /// The answer options, keyed by name with a description of each. Order is preserved by the
    /// provider's caller, so an implementation that returns probabilities for exactly these keys is
    /// what is required; one that invents or drops options breaks the mapping to candidates.
    /// </param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    /// <returns>
    /// A probability per option name. The values must sum to approximately 1 across the supplied
    /// options, because the provider reads them as a distribution over the candidates.
    /// </returns>
    Task<IReadOnlyDictionary<string, double>> AskChoiceAsync(
        string instructions,
        string state,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}
