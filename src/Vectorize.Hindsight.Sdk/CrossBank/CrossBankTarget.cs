namespace Vectorize.Hindsight.Sdk.CrossBank;

/// <summary>
/// A policy a caller applies to one bank during a cross-bank recall.
/// </summary>
/// <remarks>
/// Optional. Supplied when banks are not interchangeable — different trust levels, ownership or
/// lifecycle — and the per-bank request needs to differ, or the ordering needs to know which bank
/// matters more.
/// </remarks>
public sealed class CrossBankTarget
{
    /// <summary>The bank id.</summary>
    public required string BankId { get; init; }

    /// <summary>
    /// A weight for ordering, recorded on each candidate from this bank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One is neutral. <b>This library does not reorder by it.</b> The candidates carry it and
    /// <see cref="CrossBankRecallResult.Candidates"/> stays in a defensible order, so a caller
    /// applying its own policy — or a future reranking stage — reads the weight rather than being
    /// silently reordered by it.
    /// </para>
    /// <para>
    /// That restraint is the documented rule: bank weighting must represent an intentional policy,
    /// not compensate for retrieval-score differences. A library cannot tell which a caller means,
    /// so it records the intent instead of guessing at it.
    /// </para>
    /// </remarks>
    public double PolicyWeight { get; init; } = 1.0;

    /// <summary>
    /// A recall request for this bank only, replacing the shared one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason to have per-bank policy at all: a bank of regulated records may need a tighter
    /// <c>max_tokens</c> or a different <c>budget</c> than a bank of notes, and one shared request
    /// cannot express that.
    /// </para>
    /// <para>
    /// The query is still set per call and is not read from here. Everything else is taken from
    /// this object when it is present, and from
    /// <see cref="CrossBankRecallOptions.Request"/> when it is not.
    /// </para>
    /// </remarks>
    public Models.RecallRequest? Request { get; init; }

    /// <summary>
    /// The number of results to take from this bank, replacing
    /// <see cref="CrossBankRecallOptions.PerBankLimit"/>.
    /// </summary>
    /// <remarks>
    /// For a bank known to be large or noisy, so it contributes fewer candidates than a small
    /// curated one. The cap is applied after the bank's own ranking, so it takes that bank's best.
    /// </remarks>
    public int? PerBankLimit { get; init; }

    /// <summary>Creates a target for a bank.</summary>
    /// <param name="bankId">The bank id.</param>
    public static CrossBankTarget For(string bankId) => new() { BankId = bankId };
}
