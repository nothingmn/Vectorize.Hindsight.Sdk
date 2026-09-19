namespace Vectorize.Hindsight.Sdk.CrossBank;

/// <summary>
/// How candidates from several banks are ordered once they are merged.
/// </summary>
/// <remarks>
/// This choice matters more than it looks. Each bank's retrieval score is produced by that
/// bank's own pipeline, over its own content distribution, with its own reranker settings
/// — so the numbers are meaningful *within* the bank that produced them and are not on a
/// common scale across banks. Sorting merged candidates by those scores silently treats
/// them as comparable, which is the error this whole operation exists to avoid.
/// </remarks>
public enum CrossBankMergeOrder
{
    /// <summary>
    /// Take one candidate from each bank in turn, by each bank's own rank: A1, B1, C1, A2, B2, C2…
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default, and the only order that does not require believing the scores are
    /// comparable. It gives every bank an equal voice at every position: a bank whose
    /// top result scored 0.62 is not pushed below a different bank's fourth result that
    /// happened to score 0.80, because those numbers were never on the same scale.
    /// </para>
    /// <para>
    /// Within a bank, the bank's own ranking is preserved exactly — that ordering *is*
    /// meaningful, because it came from one pipeline.
    /// </para>
    /// <para>
    /// The cost is honest and worth stating: a bank with one excellent result places that
    /// result first, then contributes nothing, while a bank with ten mediocre results keeps
    /// a presence through position ten. When coverage across banks matters more than raw
    /// relevance, that is the intent. When it does not, use <see cref="ScoreDescending"/>
    /// and accept its caveat.
    /// </para>
    /// </remarks>
    InterleavedByRank,

    /// <summary>
    /// Order by the raw retrieval scores, highest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Available because sometimes it is genuinely what a caller wants — a set of banks known
    /// to share a configuration, or a diagnostic view. It is not the default because it
    /// asserts something usually false: that a 0.9 from one bank means what a 0.9 from another
    /// means.
    /// </para>
    /// <para>
    /// Every candidate carries <see cref="CrossBankCandidate.RetrievalScore"/> and
    /// <see cref="CrossBankCandidate.Rank"/> unmodified, so a caller can reorder on real
    /// information rather than on this enum.
    /// </para>
    /// </remarks>
    ScoreDescending,

    /// <summary>
    /// Keep the banks in the order the caller supplied, and each bank's own ranking within it.
    /// </summary>
    /// <remarks>
    /// For when the bank order *is* the policy — a caller who knows the first bank is the
    /// authoritative one and wants its results presented first, without pretending a score
    /// comparison took place.
    /// </remarks>
    BankOrder,
}

/// <summary>
/// What happens when one of the banks fails.
/// </summary>
public enum CrossBankFailureMode
{
    /// <summary>
    /// Return what succeeded and report the failures alongside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default. A bank being unreachable, unauthorized or deleted should not discard the
    /// results from the banks that answered — that would turn one bank's outage into a total
    /// retrieval failure. The failure is not swallowed: it appears in
    /// <see cref="CrossBankRecallResult.Banks"/> with its exception, and the count of banks
    /// that answered is on the result.
    /// </para>
    /// <para>
    /// The caller is expected to look. A result that quietly lost a bank is a worse answer than
    /// one that says it did, which is why the outcome is modelled rather than logged.
    /// </para>
    /// </remarks>
    Partial,

    /// <summary>
    /// Raise if any bank fails, even when others succeeded.
    /// </summary>
    /// <remarks>
    /// For a caller whose correctness depends on every bank being consulted — an
    /// authorization decision, or a report that must not omit a tenant. Choose this when a
    /// partial answer would be *wrong* rather than merely incomplete.
    /// </remarks>
    RequireAll,
}

/// <summary>
/// Options for a cross-bank recall.
/// </summary>
public sealed class CrossBankRecallOptions
{
    /// <summary>
    /// How many results to take from each bank before merging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten by default. This cap is what stops a large or densely-populated bank from filling
    /// the candidate pool on its own: with three banks the pool is at most thirty candidates,
    /// and the merge compares those thirty rather than the global top thirty by score.
    /// </para>
    /// <para>
    /// A larger value costs tokens and latency in proportion to the number of banks, since it
    /// is applied per bank.
    /// </para>
    /// </remarks>
    public int PerBankLimit { get; set; } = 10;

    /// <summary>
    /// The most candidates to return after merging.
    /// </summary>
    /// <remarks>
    /// Twenty by default. <see cref="PerBankLimit"/> bounds what is *considered*;
    /// this bounds what is *handed on*, and it is the number that decides how much of a
    /// model's context window the memories occupy.
    /// </remarks>
    public int MaxCandidates { get; set; } = 20;

    /// <summary>
    /// How many banks to query at once. Null means all of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fan-out is concurrent by design, so the default is every bank at once. It is
    /// configurable because concurrency here is real load on a real server: a caller with
    /// forty banks, or one talking to a deployment with a modest connection pool, would
    /// rather queue them.
    /// </para>
    /// <para>
    /// The bound is a semaphore, so excess banks wait rather than fail, and cancellation
    /// releases a waiting bank rather than leaving it queued.
    /// </para>
    /// </remarks>
    public int? MaxConcurrency { get; set; }

    /// <summary>What to do when a bank fails. Defaults to <see cref="CrossBankFailureMode.Partial"/>.</summary>
    public CrossBankFailureMode FailureMode { get; set; } = CrossBankFailureMode.Partial;

    /// <summary>How to order the merged candidates. Defaults to <see cref="CrossBankMergeOrder.InterleavedByRank"/>.</summary>
    public CrossBankMergeOrder MergeOrder { get; set; } = CrossBankMergeOrder.InterleavedByRank;

    /// <summary>
    /// Whether to confirm every bank exists before recalling from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, because it costs one listing request and a missing bank is usually a
    /// configuration error the caller would rather see directly.
    /// </para>
    /// <para>
    /// Worth turning on when the bank list is assembled from data rather than fixed in code,
    /// because of a trap verified against a live server: <b>recall against a bank that does not
    /// exist returns HTTP 200 with an empty result set, not an error.</b> A nonexistent bank is
    /// therefore indistinguishable from a bank that is genuinely empty, and a typo in a bank id
    /// looks exactly like a bank with nothing relevant in it. When this is enabled, a bank that
    /// does not exist is reported as a failed bank — carrying a
    /// <see cref="Errors.HindsightNotFoundException"/> — rather than an empty one.
    /// </para>
    /// </remarks>
    public bool ValidateBanksExist { get; init; }

    /// <summary>
    /// Whether to drop candidates that duplicate one already seen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default. The same fact often exists in several banks — a document indexed in two,
    /// a mirrored bank, an overlapping retention — and presenting it three times wastes context
    /// and makes one piece of evidence look like three.
    /// </para>
    /// <para>
    /// Duplicates are detected on identity, never on meaning: the same document and chunk, or
    /// identical text. Semantic deduplication is deliberately not attempted, because deciding
    /// that two differently-worded facts are the same fact requires a judgement this library
    /// cannot make reliably and a reranker or model is the right place for it.
    /// </para>
    /// <para>
    /// A dropped duplicate is not discarded silently: the surviving candidate records every
    /// bank it was seen in, so provenance survives the merge.
    /// </para>
    /// </remarks>
    public bool Deduplicate { get; set; } = true;

    /// <summary>
    /// The most candidates any one bank may contribute to the final set. Null means no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A context-construction policy, not a retrieval one. Interleaving already gives each bank
    /// a voice, but a bank with many good results still accumulates: eight of twenty slots is
    /// possible. This bounds that.
    /// </para>
    /// <para>
    /// Applies *after* ordering, so it removes the lowest-ordered candidates from an
    /// over-represented bank rather than re-ranking anything.
    /// </para>
    /// </remarks>
    public int? MaxPerBank { get; set; }

    /// <summary>
    /// The least any bank that returned results should contribute, when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off unless set, because forcing a bank into the answer is a claim about its relevance
    /// that this library cannot make. Set it when coverage is the point — a compliance question
    /// that must consider every tenant, or a summary that must not omit a source.
    /// </para>
    /// <para>
    /// It is a floor, not a guarantee, and it does not invent results: a bank that returned
    /// nothing contributes nothing. The document puts it plainly — do not force diversity when
    /// a bank genuinely has nothing relevant — and a bank with no results is that case.
    /// </para>
    /// </remarks>
    public int? MinPerBank { get; set; }

    /// <summary>
    /// The recall request used for every bank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The query is set per call and is not read from here; everything else — budget, types,
    /// tags, temporal window, include options — applies to every bank unless a per-bank
    /// override replaces it.
    /// </para>
    /// <para>
    /// The object is serialized per bank, so a caller must not mutate it while a call is in
    /// flight.
    /// </para>
    /// </remarks>
    public Models.RecallRequest? Request { get; set; }
}
