namespace Vectorize.Hindsight.Sdk.Reranking.TypeSafe;

/// <summary>
/// How to ask the reranker question: one Choice over all candidates, or one Noul per candidate.
/// </summary>
/// <remarks>
/// <para>
/// This is the one genuine design choice in this provider, and the two modes are not equivalent.
/// </para>
/// <para>
/// <b><see cref="Choice"/></b> asks a single question whose options are the candidates, so the model
/// returns one probability distribution over all of them. The candidates <i>compete</i> inside one
/// judgement, which is what makes the result a ranking rather than a set of unrelated opinions, and
/// the returned probabilities are directly comparable because they were produced together.
/// </para>
/// <para>
/// <b><see cref="Noul"/></b> asks "is this passage relevant?" once per candidate. It is cheaper per
/// question and scales past any option limit, but the answers are <b>independent</b> — the vendor's
/// own documentation is explicit that one question's answer is not context for another. Twenty
/// unrelated 0.9s rank nothing. Offered because it is the right answer for a pool too large to list
/// as options, and because a caller comparing the two on their own data should not have to
/// reimplement it.
/// </para>
/// </remarks>
public enum TypeSafeRerankMode
{
    /// <summary>
    /// One Choice question whose options are the candidates. The default, because it produces a
    /// genuine ranking.
    /// </summary>
    Choice = 0,

    /// <summary>
    /// One independent yes/no question per candidate, scored by probability.
    /// </summary>
    /// <remarks>
    /// Use only when the candidate pool is too large to list as Choice options. The scores are
    /// independent judgements, so a flat distribution of them is not evidence that the candidates
    /// are equally good — it is evidence that the question did not discriminate.
    /// </remarks>
    Noul = 1,
}

/// <summary>Diagnostics for one Choice-based rerank.</summary>
/// <remarks>
/// Exposed because a remote reranker's most likely failure is not an exception — it is a
/// technically successful call whose answer did not discriminate. Without this a caller sees
/// plausible-looking but meaningless scores and has no way to tell.
/// </remarks>
public sealed record TypeSafeRerankDiagnostics
{
    /// <summary>The mode used.</summary>
    public required TypeSafeRerankMode Mode { get; init; }

    /// <summary>How many candidates were put to the model.</summary>
    public required int CandidateCount { get; init; }

    /// <summary>
    /// How many candidates the model actually assigned probability to, above zero.
    /// </summary>
    /// <remarks>
    /// A Choice question can return probability on only a few options, effectively ignoring the
    /// rest. That is a real answer — those candidates were judged irrelevant — but it is worth
    /// surfacing, because a model that assigns probability to exactly one of twenty candidates
    /// either found a clear winner or is not really considering the list.
    /// </remarks>
    public required int OptionsWithProbability { get; init; }

    /// <summary>
    /// The model's own confidence, when the provider reported one.
    /// </summary>
    /// <remarks>
    /// Null in Noul mode, which has no separate confidence. Low confidence in Choice mode means the
    /// distribution was spread across several candidates, which the vendor documents as the signal
    /// that the question did not discriminate rather than that the answer is wrong.
    /// </remarks>
    public double? Confidence { get; init; }
}
