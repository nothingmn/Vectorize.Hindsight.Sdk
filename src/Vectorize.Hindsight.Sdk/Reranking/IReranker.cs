namespace Vectorize.Hindsight.Sdk.Reranking;

/// <summary>
/// Scores a set of candidate passages against a query, so a merged result set can be ordered by
/// relevance rather than by retrieval scores that are not comparable across banks.
/// </summary>
/// <remarks>
/// <para>
/// This is the second stage of the documented pipeline: the first stage asks "what looks relevant
/// inside this bank?", and this one asks "of everything retrieved from every bank, what is most
/// useful for this query?". Retrieval scores are meaningful primarily within the retrieval
/// operation that produced them, so a cross-bank comparison needs a scorer that sees every
/// candidate under one query and one model.
/// </para>
/// <para>
/// The interface lives in the core library and the implementations live in separate packages, so a
/// caller who does not want a reranker never takes a dependency on an inference runtime. See
/// <c>Vectorize.Hindsight.Sdk.Reranking.Onnx</c> for a local ONNX cross-encoder implementation.
/// </para>
/// <para>
/// <b>Scores are ordinal, not calibrated.</b> The only contract that matters is that a higher
/// score means more relevant to this query, and that the scores of one call are comparable with
/// each other. Comparing a score across two calls, or against a fixed threshold, is not
/// meaningful for most cross-encoder models — a heavily quantized model in particular can compress
/// its output into a narrow band while getting the <i>order</i> right. Order by it; do not
/// threshold on it.
/// </para>
/// </remarks>
public interface IReranker
{
    /// <summary>
    /// True when this reranker makes no judgement and preserves the order it is given.
    /// </summary>
    /// <remarks>
    /// Read by the cross-bank pipeline to decide whether to report the result as reranked. A
    /// no-op default must not be able to claim that relevance was evaluated, or a caller cannot
    /// tell a relevance-ordered result from a merge-ordered one — which is the single distinction
    /// this stage exists to provide.
    /// </remarks>
    bool IsIdentity => false;


    /// <summary>
    /// Scores every document against the query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns one score per document, aligned by index: result <c>i</c> is the score for
    /// <paramref name="documents"/><c>[i]</c>. The count is checked against the input, and a
    /// mismatch is treated as a failure rather than silently mismatching scores to passages —
    /// a reranker that returns them out of order is worse than no reranker at all, because the
    /// result looks plausible.
    /// </para>
    /// <para>
    /// The implementation must honour <paramref name="cancellationToken"/>, and should bound its
    /// own concurrency rather than assuming single-threaded use.
    /// </para>
    /// </remarks>
    /// <param name="query">The query every document is scored against.</param>
    /// <param name="documents">The candidate passages, in the order scores are expected back.</param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    /// <returns>One score per document, aligned by index. Higher is more relevant.</returns>
    Task<IReadOnlyList<double>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken);
}
