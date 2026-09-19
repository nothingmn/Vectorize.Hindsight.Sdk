namespace Vectorize.Hindsight.Sdk.CrossBank;

/// <summary>
/// Merges per-bank result sets into one ordered, deduplicated candidate set.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the client so the whole decision — ordering, deduplication, capping, diversity —
/// is a pure function over inputs. That makes every case testable without a server, and it makes
/// the interesting part of this feature reviewable on its own.
/// </para>
/// <para>
/// No scoring happens here. Nothing is reranked, no scores are combined, and no weight is applied:
/// per the agreed scope the final ordering is a policy this library expresses, and relevance over
/// the merged set is left to the model the candidates are handed to.
/// </para>
/// </remarks>
internal static class CrossBankMerger
{
    /// <summary>The result of a merge.</summary>
    internal sealed record MergeOutcome(
        IReadOnlyList<CrossBankCandidate> Candidates,
        int CandidatePoolSize,
        int DuplicatesMerged);

    /// <summary>
    /// Merges per-bank result sets.
    /// </summary>
    /// <param name="perBank">
    /// One entry per bank that answered, in the caller's bank order, each already capped to the
    /// per-bank limit.
    /// </param>
    /// <param name="options">The merge options.</param>
    internal static MergeOutcome Merge(
        IReadOnlyList<(string BankId, IReadOnlyList<CrossBankCandidate> Candidates)> perBank,
        CrossBankRecallOptions options)
    {
        // The pool every bank contributed to, before deduplication. This is the number the
        // per-bank cap exists to bound, so it is reported as it was entered rather than after
        // the merge changed it.
        var poolSize = perBank.Sum(entry => entry.Candidates.Count);

        var ordered = options.MergeOrder switch
        {
            CrossBankMergeOrder.ScoreDescending => OrderByScore(perBank),
            CrossBankMergeOrder.BankOrder => OrderByBank(perBank),
            _ => OrderByInterleavedRank(perBank),
        };

        var (deduplicated, merged) = options.Deduplicate
            ? Deduplicate(ordered)
            : (ordered, 0);

        var selected = ApplyLimitsThenCap(deduplicated, options);

        return new MergeOutcome(selected, poolSize, merged);
    }

    /// <summary>
    /// One candidate from each bank in turn, following each bank's own ranking.
    /// </summary>
    /// <remarks>
    /// The default order, and the only one that assumes nothing about cross-bank score
    /// comparability. Each bank's internal order is preserved exactly, because that ordering came
    /// from a single pipeline and is meaningful.
    /// </remarks>
    private static List<CrossBankCandidate> OrderByInterleavedRank(
        IReadOnlyList<(string BankId, IReadOnlyList<CrossBankCandidate> Candidates)> perBank)
    {
        var ordered = new List<CrossBankCandidate>();
        var deepest = perBank.Count == 0 ? 0 : perBank.Max(entry => entry.Candidates.Count);

        for (var rank = 0; rank < deepest; rank++)
        {
            foreach (var (_, candidates) in perBank)
            {
                if (rank < candidates.Count)
                {
                    ordered.Add(candidates[rank]);
                }
            }
        }

        return ordered;
    }

    /// <summary>Orders by each bank's reported score, highest first.</summary>
    /// <remarks>
    /// The caller has asked for this explicitly. Ties break on rank and then on bank id so the
    /// order is deterministic rather than dependent on the sort implementation.
    /// </remarks>
    private static List<CrossBankCandidate> OrderByScore(
        IReadOnlyList<(string BankId, IReadOnlyList<CrossBankCandidate> Candidates)> perBank)
    {
        var ordered = new List<CrossBankCandidate>();
        foreach (var (_, candidates) in perBank)
        {
            ordered.AddRange(candidates);
        }

        return [.. ordered
            .OrderByDescending(candidate => candidate.RetrievalScore ?? double.NegativeInfinity)
            .ThenBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.BankId, StringComparer.Ordinal)];
    }

    /// <summary>Keeps the caller's bank order, and each bank's own ranking within it.</summary>
    private static List<CrossBankCandidate> OrderByBank(
        IReadOnlyList<(string BankId, IReadOnlyList<CrossBankCandidate> Candidates)> perBank)
    {
        var ordered = new List<CrossBankCandidate>();
        foreach (var (_, candidates) in perBank)
        {
            ordered.AddRange(candidates);
        }

        return ordered;
    }

    /// <summary>
    /// Collapses content seen in more than one bank onto its highest-ordered occurrence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Identity only, never meaning: the same document and chunk, the same memory id, or identical
    /// text. Two differently-worded versions of one fact are left alone, because deciding they are
    /// the same fact is a judgement this library cannot make reliably and a model is the right
    /// place for it.
    /// </para>
    /// <para>
    /// A bank that does not exist is not a failure at the API level — recall against an unknown
    /// bank returns HTTP 200 with an empty result set — so a missing bank arrives here as a bank
    /// that contributed nothing, and looks exactly like a bank that is genuinely empty. Checking
    /// the banks exist first is the caller's defence; see
    /// <see cref="CrossBankRecallOptions"/>.
    ///
    /// The survivor is the occurrence the ordering already placed highest, so deduplication never    /// The survivor is the occurrence the ordering already placed highest, so deduplication never
    /// promotes something. It records every bank the content was seen in, so a caller can still
    /// tell that three banks agree rather than one bank said it.
    /// </para>
    /// </remarks>
    private static (List<CrossBankCandidate> Candidates, int Merged) Deduplicate(
        List<CrossBankCandidate> ordered)
    {
        // Case-insensitive because the key's text component is compared that way: a fact
        // extracted in two banks may differ only in the case of a name.
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CrossBankCandidate>(ordered.Count);
        var merged = 0;

        foreach (var candidate in ordered)
        {
            var key = candidate.DeduplicationKey();

            if (byKey.TryGetValue(key, out var existingIndex))
            {
                result[existingIndex] = result[existingIndex].WithAdditionalBank(candidate.BankId);
                merged++;
                continue;
            }

            byKey[key] = result.Count;
            result.Add(candidate);
        }

        return (result, merged);
    }

    /// <summary>
    /// Applies the per-bank floor and ceiling, then caps the total.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ceiling runs first: removing an over-represented bank's lowest-ordered candidates leaves
    /// room for banks that would otherwise be crowded out. The floor then re-admits one candidate
    /// from any bank that lost its place, which is the point of having a floor at all — without
    /// this order, a floor set for coverage could be satisfied and then immediately undone by the
    /// total cap.
    /// </para>
    /// <para>
    /// The floor never invents results. A bank that returned nothing contributes nothing, which is
    /// the case the design calls out: do not force diversity when a bank genuinely has nothing
    /// relevant.
    /// </para>
    /// </remarks>
    private static List<CrossBankCandidate> ApplyLimitsThenCap(
        List<CrossBankCandidate> ordered, CrossBankRecallOptions options)
    {
        var working = ordered;

        if (options.MaxPerBank is { } ceiling)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var kept = new List<CrossBankCandidate>(working.Count);

            foreach (var candidate in working)
            {
                counts.TryGetValue(candidate.BankId, out var seen);
                if (seen >= ceiling)
                {
                    continue;
                }

                counts[candidate.BankId] = seen + 1;
                kept.Add(candidate);
            }

            working = kept;
        }

        if (options.MaxCandidates > 0 && working.Count > options.MaxCandidates)
        {
            working = CapWithFloor(working, options);
        }

        return working;
    }

    /// <summary>
    /// Takes the top candidates, then re-admits one from any bank the cap excluded.
    /// </summary>
    /// <remarks>
    /// A bank displaced by the cap is restored in its own highest position, and the candidate it
    /// displaces is the one lowest in the current order — so the floor changes who is represented
    /// without changing the relative order of those who remain.
    /// </remarks>
    private static List<CrossBankCandidate> CapWithFloor(
        List<CrossBankCandidate> ordered, CrossBankRecallOptions options)
    {
        var selected = new List<CrossBankCandidate>(options.MaxCandidates);
        var selectedBanks = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in ordered)
        {
            if (selected.Count >= options.MaxCandidates)
            {
                break;
            }

            selected.Add(candidate);
            selectedBanks.Add(candidate.BankId);
        }

        if (options.MinPerBank is not { } floor || floor <= 0)
        {
            return selected;
        }

        // Every bank present in the pool, in the order it first appears, so restoration follows
        // the same order as everything else.
        var present = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in ordered)
        {
            if (seen.Add(candidate.BankId))
            {
                present.Add(candidate.BankId);
            }
        }

        foreach (var bankId in present)
        {
            if (selectedBanks.Contains(bankId))
            {
                continue;
            }

            var replacement = ordered.First(candidate => candidate.BankId == bankId);
            var position = ordered.IndexOf(replacement);

            // Drop the lowest-ordered candidate that is not the sole survivor of its bank,
            // so satisfying the floor for one bank does not empty another.
            var victimIndex = -1;
            for (var index = selected.Count - 1; index >= 0; index--)
            {
                var bankCount = selected.Count(candidate => candidate.BankId == selected[index].BankId);
                if (bankCount > Math.Max(1, floor))
                {
                    victimIndex = index;
                    break;
                }
            }

            if (victimIndex < 0)
            {
                // Nothing to give up without breaking a floor elsewhere. Insert only when there
                // is room, which is the honest answer rather than silently exceeding the cap.
                if (selected.Count < options.MaxCandidates)
                {
                    selected.Insert(Math.Min(position, selected.Count), replacement);
                    selectedBanks.Add(bankId);
                }

                continue;
            }

            selected.RemoveAt(victimIndex);
            selected.Insert(Math.Min(position, selected.Count), replacement);
            selectedBanks.Add(bankId);
        }

        return selected;
    }
}
