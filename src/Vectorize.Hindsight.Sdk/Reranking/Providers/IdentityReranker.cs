using Vectorize.Hindsight.Sdk.Reranking;

namespace Vectorize.Hindsight.Sdk.Reranking.Providers;

/// <summary>
/// A reranker that does not score anything: it keeps the order it was given.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the default, and it is a real choice rather than a placeholder.</b> A merged cross-bank
/// candidate set already arrives in a defensible order — interleaved by each bank's own rank, which
/// is the only ordering that assumes nothing about scores being comparable across banks. Adding no
/// reranker means adding no cost, no dependency and no new failure mode, and for many queries that
/// is the right trade.
/// </para>
/// <para>
/// The scores it returns are the candidates' positions, so a caller who applies them gets exactly
/// the order it passed in. That makes it usable anywhere an <see cref="IReranker"/> is required —
/// a test, a null-object default, a host that wants to switch reranking off by configuration without
/// making the property nullable at every call site — while never pretending to have judged anything.
/// </para>
/// <para>
/// <b>It does not report as a successful rerank.</b> <see cref="IsIdentity"/> is true, and
/// <c>CrossBankRecallResult.RerankApplied</c> stays false when it is used. Claiming "reranked"
/// because a no-op ran would make a caller unable to distinguish relevance-ordered results from
/// merge-ordered ones, which is the one distinction this whole stage exists to make.
/// </para>
/// </remarks>
public sealed class IdentityReranker : IReranker
{
    /// <summary>A shared instance. The type is stateless.</summary>
    public static readonly IdentityReranker Instance = new();

    /// <summary>True, because this reranker performs no judgement.</summary>
    public bool IsIdentity => true;

    /// <summary>
    /// Returns position-derived scores, descending, so applying them preserves the input order.
    /// </summary>
    /// <param name="query">Unused. No judgement is made.</param>
    /// <param name="documents">The passages, whose order is preserved.</param>
    /// <param name="cancellationToken">Checked before returning.</param>
    public Task<IReadOnlyList<double>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();

        // Descending by position, so sorting by score descending is the identity. Returning zeros
        // would also preserve order only by accident of a stable sort, and would read as though
        // every candidate had been judged equally rather than not judged at all.
        var scores = new double[documents.Count];
        for (var index = 0; index < documents.Count; index++)
        {
            scores[index] = documents.Count - index;
        }

        return Task.FromResult<IReadOnlyList<double>>(scores);
    }
}
