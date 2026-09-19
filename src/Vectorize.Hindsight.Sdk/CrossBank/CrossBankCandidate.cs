using Vectorize.Hindsight.Sdk.Models;

namespace Vectorize.Hindsight.Sdk.CrossBank;

/// <summary>
/// One memory retrieved from one bank, carrying where it came from.
/// </summary>
/// <remarks>
/// <para>
/// Provenance is attached before anything is merged and survives the entire pipeline. A merged
/// result that has lost its bank is not merely less useful — it is a claim about authority the
/// library is not entitled to make, and the document is explicit that the source bank must
/// remain attached throughout.
/// </para>
/// <para>
/// The scores are kept as separate fields rather than collapsed into one number, so the ranking
/// stays explainable. <see cref="RetrievalScore"/> is what the bank said;
/// <see cref="Rank"/> is where the bank placed it; <see cref="PolicyWeight"/> is what the caller's
/// policy said. Nothing is overwritten, so a surprising order can be traced to the field that
/// caused it.
/// </para>
/// </remarks>
public sealed class CrossBankCandidate
{
    /// <summary>The bank this came from. Never null, and never rewritten.</summary>
    public required string BankId { get; init; }

    /// <summary>The memory itself.</summary>
    public required RecallResult Source { get; init; }

    /// <summary>The memory's text, or an empty string when the bank sent none.</summary>
    public string Text => Source.Text ?? string.Empty;

    /// <summary>
    /// The score the bank reported, or null when it reported none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taken from <c>scores.final</c>, which is the value the bank itself ranks by. Kept exactly
    /// as received, including its null.
    /// </para>
    /// <para>
    /// <b>Not comparable across banks.</b> It ranks candidates within one bank's own pipeline and
    /// content distribution. Exposed because a caller may have reason to compare them — banks
    /// known to share a configuration — but the library does not do so by default.
    /// </para>
    /// </remarks>
    public double? RetrievalScore => Source.Scores?.Final;

    /// <summary>
    /// Where this bank placed the result in its own ranking, zero-based.
    /// </summary>
    /// <remarks>
    /// The one ordering across banks that is trustworthy without a reranker, because it is a
    /// comparison the bank already made within a single pipeline. This is what
    /// <see cref="CrossBankMergeOrder.InterleavedByRank"/> sorts on.
    /// </remarks>
    public required int Rank { get; init; }

    /// <summary>
    /// A caller-supplied weight applied to this bank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried rather than applied. This library does not reorder by it, because the document's
    /// rule is that bank weighting must represent an intentional policy rather than compensate
    /// blindly for retrieval-score differences — and a library cannot know which of those a
    /// caller means. So it is recorded on the candidate, and the ordering stays something the
    /// caller can reason about.
    /// </para>
    /// <para>
    /// One is the neutral value. A caller applying its own policy multiplies
    /// <see cref="RetrievalScore"/> or reorders on <see cref="Rank"/> using this.
    /// </para>
    /// </remarks>
    public double PolicyWeight { get; init; } = 1.0;

    /// <summary>
    /// Every bank this content was seen in, this one first.
    /// </summary>
    /// <remarks>
    /// The same fact in three banks is one piece of evidence, not three. When duplicates are
    /// collapsed the survivor lists all of them, so provenance is preserved even though the
    /// copies are not. Always contains <see cref="BankId"/>.
    /// </remarks>
    public IReadOnlyList<string> SeenInBanks { get; init; } = [];

    /// <summary>True when this content was found in more than one bank.</summary>
    public bool IsDuplicateAcrossBanks => SeenInBanks.Count > 1;

    /// <summary>
    /// An identity for this content, used for deduplication within the merge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No bank-local identifier is used.</b> Both candidates the API offers are scoped to the
    /// bank that minted them, verified against a live server:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>chunk_id</c> is literally <c>{bank_id}_{document_id}_{n}</c>, so the same document in
    /// two banks yields two different chunk ids and a key built on it can never match.
    /// </description></item>
    /// <item><description>
    /// <c>id</c> is a per-bank UUID, likewise unique to one bank.
    /// </description></item>
    /// </list>
    /// <para>
    /// So the bank is deliberately dropped from the key: two candidates are the same content when
    /// they describe the same document the same way, and the fact that different banks minted
    /// different local ids for it is exactly what deduplication is here to see past.
    /// </para>
    /// <para>
    /// Text is compared with whitespace collapsed and case-insensitively, because a fact
    /// extracted independently in two banks is not always rendered identically. It is still an
    /// exact-text comparison: two <i>differently worded</i> facts are never merged, which is the
    /// boundary this library deliberately does not cross.
    /// </para>
    /// </remarks>
    internal string DeduplicationKey()
    {
        var normalized = NormalizeText(Text);

        return string.IsNullOrEmpty(Source.DocumentId)
            ? $"text:{normalized}"
            : $"doc:{Source.DocumentId}\u001f{normalized}";
    }

    /// <summary>Collapses runs of whitespace and trims, so cosmetic differences do not block a match.</summary>
    private static string NormalizeText(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Creates a candidate from one bank's result.</summary>
    /// <param name="bankId">The bank.</param>
    /// <param name="result">The result the bank returned.</param>
    /// <param name="rank">The bank's own rank for it, zero-based.</param>
    /// <param name="policyWeight">The caller's weight for that bank.</param>
    internal static CrossBankCandidate Create(string bankId, RecallResult result, int rank, double policyWeight) =>
        new()
        {
            BankId = bankId,
            Source = result,
            Rank = rank,
            PolicyWeight = policyWeight,
            SeenInBanks = [bankId],
        };

    /// <summary>
    /// Returns this candidate with an additional bank recorded.
    /// </summary>
    /// <param name="bankId">Another bank the same content appeared in.</param>
    internal CrossBankCandidate WithAdditionalBank(string bankId) =>
        new()
        {
            BankId = BankId,
            Source = Source,
            Rank = Rank,
            PolicyWeight = PolicyWeight,
            SeenInBanks = [.. SeenInBanks, bankId],
        };
}

/// <summary>What happened to one bank in a cross-bank recall.</summary>
public sealed class CrossBankOutcome
{
    /// <summary>The bank.</summary>
    public required string BankId { get; init; }

    /// <summary>True when the bank was queried successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>How many results it contributed before the per-bank cap was applied.</summary>
    public int ResultCount { get; init; }

    /// <summary>How many survived into the candidate pool.</summary>
    public int CandidateCount { get; init; }

    /// <summary>How many of its candidates reached the final set.</summary>
    public int SelectedCount { get; init; }

    /// <summary>The failure, when it failed.</summary>
    /// <remarks>
    /// A caller in <see cref="CrossBankFailureMode.Partial"/> mode reads this rather than
    /// catching, because a partial result is the intended outcome and an exception would
    /// discard the banks that worked.
    /// </remarks>
    public Exception? Failure { get; init; }

    /// <summary>How long the bank took.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>True when this bank contributed nothing to the final set.</summary>
    public bool IsAbsentFromResults => SelectedCount == 0;
}

/// <summary>The merged outcome of a cross-bank recall.</summary>
public sealed class CrossBankRecallResult
{
    /// <summary>
    /// The merged candidates, already ordered and capped.
    /// </summary>
    /// <remarks>
    /// This is what a caller passes to a model. Every element still knows its bank.
    /// </remarks>
    public required IReadOnlyList<CrossBankCandidate> Candidates { get; init; }

    /// <summary>What happened to each bank, in the order the banks were supplied.</summary>
    public required IReadOnlyList<CrossBankOutcome> Banks { get; init; }

    /// <summary>How many banks were asked.</summary>
    public int BankCount => Banks.Count;

    /// <summary>How many banks answered.</summary>
    public int SucceededCount => Banks.Count(bank => bank.Succeeded);

    /// <summary>How many candidates were considered before capping.</summary>
    public required int CandidatePoolSize { get; init; }

    /// <summary>How many duplicates were collapsed.</summary>
    public required int DuplicatesMerged { get; init; }

    /// <summary>True when every bank answered.</summary>
    public bool AllBanksSucceeded => Banks.All(bank => bank.Succeeded);

    /// <summary>The banks that failed.</summary>
    public IReadOnlyList<CrossBankOutcome> Failures => [.. Banks.Where(bank => !bank.Succeeded)];

    /// <summary>
    /// True when this result is incomplete because a bank failed.
    /// </summary>
    /// <remarks>
    /// Worth checking explicitly. In partial mode a caller that ignores this cannot tell a
    /// complete answer from one that lost a bank, and the two are not the same answer.
    /// </remarks>
    public bool IsPartial => !AllBanksSucceeded;

    /// <summary>
    /// The banks that contributed at least one candidate, in the order they were supplied.
    /// </summary>
    public IReadOnlyList<string> ContributingBanks =>
        [.. Banks.Where(bank => bank.SelectedCount > 0).Select(bank => bank.BankId)];

    /// <summary>
    /// The candidates, in the order they appear here, rendered with provenance for a model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The format the document recommends — a source marker before each passage — so a model can
    /// reason about where information came from rather than receiving an undifferentiated block.
    /// Provenance is not decoration: it is what lets the model weigh a memory differently
    /// according to its bank.
    /// </para>
    /// <para>
    /// A bank that could not be reached is named too. A model told only about the banks that
    /// answered will present a partial picture as the whole one.
    /// </para>
    /// </remarks>
    public string ToContext()
    {
        var builder = new System.Text.StringBuilder();

        foreach (var candidate in Candidates)
        {
            var source = candidate.IsDuplicateAcrossBanks
                ? string.Join(',', candidate.SeenInBanks)
                : candidate.BankId;

            builder.Append("[SOURCE ")
                .Append(source);

            if (!string.IsNullOrEmpty(candidate.Source.Id))
            {
                builder.Append(" | result ").Append(candidate.Source.Id);
            }

            builder.AppendLine("]");
            builder.AppendLine(candidate.Text);
            builder.AppendLine();
        }

        foreach (var failure in Failures)
        {
            builder.Append("[SOURCE ").Append(failure.BankId).AppendLine(" unavailable]");
            builder.AppendLine("No memories could be retrieved from this bank.");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
