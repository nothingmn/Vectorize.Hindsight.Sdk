using System.Diagnostics;
using Vectorize.Hindsight.Sdk.CrossBank;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;

namespace Vectorize.Hindsight.Sdk;

/// <summary>
/// The cross-bank recall operation: fan out, merge, preserve provenance.
/// </summary>
/// <remarks>
/// <para>
/// A partial part of <see cref="HindsightClient"/>. Recall from one bank is a single call; recall
/// from several is a fan-out, a merge, and a policy — which is a different operation with its own
/// options and its own failure semantics, not a loop over the first.
/// </para>
/// <para>
/// There is no reranker here by design. The banks stay isolated for storage and permissions, the
/// merge is a policy this library can defend, and relevance over the combined set is decided by
/// the model the candidates are handed to.
/// </para>
/// </remarks>
public sealed partial class HindsightClient
{
    /// <summary>
    /// Recalls from several banks concurrently and merges the results.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every bank is queried independently and in parallel, each capped to its own limit, then the
    /// candidates are merged with their provenance intact. A candidate keeps its bank, its bank's
    /// own rank, and its bank's reported score as separate fields, so the final order is
    /// explainable rather than a number that was silently treated as comparable across banks.
    /// </para>
    /// <para>
    /// The default order takes one candidate from each bank in turn, following each bank's own
    /// ranking. That is the only order which assumes nothing about cross-bank score comparability,
    /// which is the point of merging at all.
    /// </para>
    /// <para>
    /// By default a bank that fails does not fail the call. Its exception is reported on
    /// <see cref="CrossBankRecallResult.Banks"/> and the banks that answered still return — one
    /// bank's outage should not be a total retrieval failure. Check
    /// <see cref="CrossBankRecallResult.IsPartial"/> to tell a complete answer from a truncated
    /// one; the two are not the same answer. Use
    /// <see cref="CrossBankFailureMode.RequireAll"/> when a partial answer would be wrong rather
    /// than merely incomplete.
    /// </para>
    /// <para>
    /// Concurrency is bounded by <see cref="CrossBankRecallOptions.MaxConcurrency"/> when set, and
    /// unbounded otherwise. Cancellation is honoured through every await and stops the fan-out
    /// rather than letting queued banks continue after the caller has given up.
    /// </para>
    /// </remarks>
    /// <param name="query">The query, applied to every bank.</param>
    /// <param name="bankIds">The banks to recall from. At least one.</param>
    /// <param name="options">The merge and fan-out options. Null means defaults.</param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    /// <returns>The merged candidates and a per-bank outcome.</returns>
    /// <exception cref="HindsightValidationException">
    /// No banks were supplied, a bank id is blank, the query is blank, or a limit is not positive.
    /// Raised before any request, so a misconfigured call costs nothing.
    /// </exception>
    [HindsightComposition]
    public Task<CrossBankRecallResult> RecallAcrossBanksAsync(
        string query,
        IReadOnlyList<string> bankIds,
        CrossBankRecallOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(bankIds);

        var targets = new List<CrossBankTarget>(bankIds.Count);
        foreach (var bankId in bankIds)
        {
            targets.Add(CrossBankTarget.For(bankId));
        }

        return RecallAcrossBanksAsync(query, targets, options, cancellationToken);
    }

    /// <summary>
    /// Recalls from several banks concurrently, each with its own policy.
    /// </summary>
    /// <remarks>
    /// The overload for banks that are not interchangeable: a per-bank request, a per-bank limit,
    /// and a weight recorded on each candidate. See <see cref="CrossBankTarget"/> for why the
    /// weight is carried rather than applied.
    /// </remarks>
    /// <param name="query">The query, applied to every bank.</param>
    /// <param name="targets">The banks and their policies. At least one.</param>
    /// <param name="options">The merge and fan-out options. Null means defaults.</param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    /// <returns>The merged candidates and a per-bank outcome.</returns>
    [HindsightComposition]
    public async Task<CrossBankRecallResult> RecallAcrossBanksAsync(
        string query,
        IReadOnlyList<CrossBankTarget> targets,
        CrossBankRecallOptions? options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(targets);

        options ??= new CrossBankRecallOptions();
        CrossBankValidation.Validate(query, targets, options);

        var perBank = new (string BankId, IReadOnlyList<CrossBankCandidate> Candidates)[targets.Count];
        var outcomes = new CrossBankOutcome[targets.Count];

        // A semaphore rather than chunking: excess banks wait their turn instead of being sent in
        // waves, so a fast bank is not held behind a slow one it was grouped with. The token is
        // passed to WaitAsync, so a caller who cancels releases a queued bank rather than leaving
        // it parked.
        using var gate = options.MaxConcurrency is { } limit
            ? new SemaphoreSlim(limit, limit)
            : null;

        var tasks = new Task[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var slot = index;

            tasks[slot] = RecallOneBankAsync(
                query, target, options, gate, perBank, outcomes, slot, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // A null slot means the bank did not complete, which only happens when the caller
        // cancelled: every other failure is captured as an outcome. Propagating is right, because
        // the caller asked to stop.
        cancellationToken.ThrowIfCancellationRequested();

        var completed = new List<(string BankId, IReadOnlyList<CrossBankCandidate> Candidates)>(perBank.Length);
        foreach (var slot in perBank)
        {
            if (slot.Candidates is not null)
            {
                completed.Add(slot);
            }
        }

        var merged = CrossBankMerger.Merge(completed, options);

        // Second stage, when a reranker was supplied: compare every candidate against the query
        // under one model, which is the only comparison that is meaningful across banks.
        var candidates = merged.Candidates;
        var rerankApplied = false;
        Exception? rerankFailure = null;

        if (options.Reranker is not null && candidates.Count > 0)
        {
            (candidates, rerankApplied, rerankFailure) =
                await RerankAsync(query, candidates, options, cancellationToken).ConfigureAwait(false);
        }

        var selectedCounts = candidates
            .GroupBy(candidate => candidate.BankId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var finalOutcomes = new CrossBankOutcome[outcomes.Length];
        for (var index = 0; index < outcomes.Length; index++)
        {
            var outcome = outcomes[index];
            finalOutcomes[index] = new CrossBankOutcome
            {
                BankId = outcome.BankId,
                Succeeded = outcome.Succeeded,
                ResultCount = outcome.ResultCount,
                CandidateCount = outcome.CandidateCount,
                SelectedCount = selectedCounts.GetValueOrDefault(outcome.BankId),
                Failure = outcome.Failure,
                Elapsed = outcome.Elapsed,
            };
        }

        return new CrossBankRecallResult
        {
            Candidates = candidates,
            Banks = finalOutcomes,
            CandidatePoolSize = merged.CandidatePoolSize,
            DuplicatesMerged = merged.DuplicatesMerged,
            RerankApplied = rerankApplied,
            RerankFailure = rerankFailure,
        };
    }

    /// <summary>
    /// Scores the merged candidates against the query and reorders them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reranker failure is reported rather than raised. The caller asked two questions — what is
    /// relevant, and in what order — and the first was already answered successfully by the banks.
    /// Failing the whole call would discard memories that were retrieved, which is a worse outcome
    /// than handing them back in the pre-rerank order with the degradation stated. The fallback is
    /// never silent: it is on <see cref="CrossBankRecallResult.RerankFailure"/> and rendered into
    /// the context, so a model is told the position is not a statement about relevance.
    /// </para>
    /// <para>
    /// When only some candidates are reranked, the rest keep their merge order and follow. They are
    /// not dropped, and they are not mixed among the reranked ones in a way that would imply they
    /// were compared.
    /// </para>
    /// </remarks>
    private static async Task<(IReadOnlyList<CrossBankCandidate> Candidates, bool Applied, Exception? Failure)>
        RerankAsync(
            string query,
            IReadOnlyList<CrossBankCandidate> candidates,
            CrossBankRecallOptions options,
            CancellationToken cancellationToken)
    {
        var toRerank = options.MaxRerankCandidates is { } limit && candidates.Count > limit
            ? candidates.Take(limit).ToList()
            : [.. candidates];

        var documents = new List<string>(toRerank.Count);
        foreach (var candidate in toRerank)
        {
            documents.Add(candidate.Text);
        }

        IReadOnlyList<double> scores;
        try
        {
            scores = await options.Reranker!.RerankAsync(query, documents, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. That is not a reranker failure and must not be reported as one.
            throw;
        }
        catch (Exception ex)
        {
            return (candidates, false, ex);
        }

        // A reranker that returns the wrong number of scores would have them silently attached to
        // the wrong passages, which is worse than no reranking at all: the result would look
        // plausible and be wrong. Treated as a failure.
        if (scores.Count != toRerank.Count)
        {
            return (candidates, false, new HindsightProtocolException(
                $"The reranker returned {scores.Count} score(s) for {toRerank.Count} document(s). "
                + "Scores cannot be matched to passages safely, so the ranking was discarded.",
                "IReranker.RerankAsync",
                innerException: null));
        }

        var scored = new List<CrossBankCandidate>(toRerank.Count);
        for (var index = 0; index < toRerank.Count; index++)
        {
            scored.Add(toRerank[index].WithRerankScore(scores[index]));
        }

        var ordered = scored
            .OrderByDescending(candidate => candidate.RerankScore ?? double.NegativeInfinity)
            // Deterministic tie-break, so equal scores do not order by sort stability alone.
            .ThenBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.BankId, StringComparer.Ordinal)
            .ToList();

        if (toRerank.Count < candidates.Count)
        {
            ordered.AddRange(candidates.Skip(toRerank.Count));
        }

        return (ordered, true, null);
    }

    /// <summary>Recalls from one bank and records its outcome in its slot.</summary>
    private async Task RecallOneBankAsync(
        string query,
        CrossBankTarget target,
        CrossBankRecallOptions options,
        SemaphoreSlim? gate,
        (string BankId, IReadOnlyList<CrossBankCandidate> Candidates)[] perBank,
        CrossBankOutcome[] outcomes,
        int slot,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (gate is not null)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (options.ValidateBanksExist)
            {
                await EnsureBankExistsAsync(target.BankId, cancellationToken).ConfigureAwait(false);
            }

            var request = BuildBankRequest(query, target, options);

            var response = await RecallMemoriesAsync(target.BankId, request, cancellationToken)
                .ConfigureAwait(false);

            var limit = target.PerBankLimit ?? options.PerBankLimit;
            var candidates = new List<CrossBankCandidate>(Math.Min(response.Results.Count, limit));

            // The bank's own order is preserved exactly: this is the comparison that is
            // trustworthy, because it came from one pipeline over one content set.
            for (var rank = 0; rank < response.Results.Count && rank < limit; rank++)
            {
                candidates.Add(CrossBankCandidate.Create(
                    target.BankId, response.Results[rank], rank, target.PolicyWeight));
            }

            perBank[slot] = (target.BankId, candidates);
            outcomes[slot] = new CrossBankOutcome
            {
                BankId = target.BankId,
                Succeeded = true,
                ResultCount = response.Results.Count,
                CandidateCount = candidates.Count,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. The slot stays null and the caller's token is checked after the
            // fan-out, which propagates rather than reporting a bank as "failed".
            throw;
        }
        catch (Exception ex) when (ex is HindsightException or HttpRequestException or TaskCanceledException)
        {
            perBank[slot] = (target.BankId, null!);
            outcomes[slot] = new CrossBankOutcome
            {
                BankId = target.BankId,
                Succeeded = false,
                Failure = ex,
                Elapsed = stopwatch.Elapsed,
            };

            if (options.FailureMode == CrossBankFailureMode.RequireAll)
            {
                // Surfaced after the fan-out so every bank is still given the chance to be
                // recorded, which makes the failure report complete rather than racing.
                throw;
            }
        }
        finally
        {
            gate?.Release();
            stopwatch.Stop();
        }
    }

    /// <summary>
    /// Raises when a bank does not exist, so a typo is not silently read as an empty bank.
    /// </summary>
    /// <remarks>
    /// Recall against a nonexistent bank answers HTTP 200 with no results — verified against a
    /// live server — so the recall itself cannot distinguish a bank that is empty from one that
    /// was never there. Listing is what does distinguish them: an unknown bank is absent from the
    /// listing, and the listing is the only cheap way to tell.
    /// </remarks>
    private async Task EnsureBankExistsAsync(string bankId, CancellationToken cancellationToken)
    {
        const int PageSize = 200;
        var offset = 0;

        while (true)
        {
            var page = await ListBanksAsync(
                new ListBanksQuery { Limit = PageSize, Offset = offset },
                cancellationToken).ConfigureAwait(false);

            foreach (var bank in page.Banks)
            {
                if (string.Equals(bank.BankId, bankId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            var seen = offset + page.Banks.Count;
            if (page.Banks.Count == 0 || (page.Total is { } total && seen >= total))
            {
                throw new HindsightNotFoundException(
                    $"Bank '{bankId}' does not exist. Recall against a missing bank returns an empty "
                    + "result set rather than an error, so this is checked explicitly.",
                    $"GET {TenantPrefix}/banks",
                    responseBody: null);
            }

            offset = seen;
        }
    }

    /// <summary>
    /// Builds the request for one bank: the shared one, then the target's, then the query.
    /// </summary>
    /// <remarks>
    /// The query is assigned last and cannot be overridden per bank, because the whole point of
    /// this operation is that every bank answers the same question.
    /// </remarks>
    private static RecallRequest BuildBankRequest(
        string query, CrossBankTarget target, CrossBankRecallOptions options)
    {
        var source = target.Request ?? options.Request;

        // A fresh instance per bank: the same request object is serialized concurrently, and
        // mutating a caller's object to inject the query would race between banks.
        var request = new RecallRequest { Query = query };

        if (source is null)
        {
            return request;
        }

        request.Types = source.Types;
        request.PreferObservations = source.PreferObservations;
        request.Budget = source.Budget;
        request.MaxTokens = source.MaxTokens;
        request.Trace = source.Trace;
        request.QueryTimestamp = source.QueryTimestamp;
        request.Include = source.Include;
        request.Tags = source.Tags;
        request.TagsMatch = source.TagsMatch;
        request.TagGroups = source.TagGroups;
        request.MinScores = source.MinScores;
        request.TemporalWindow = source.TemporalWindow;

        return request;
    }
}

/// <summary>
/// Validates a cross-bank call before anything is sent.
/// </summary>
/// <remarks>
/// A misconfigured fan-out is the expensive kind to discover: N banks in flight, all of them
/// failing the same way. Catching it here makes the mistake cost nothing and names the argument.
/// </remarks>
internal static class CrossBankValidation
{
    internal static void Validate(
        string query, IReadOnlyList<CrossBankTarget> targets, CrossBankRecallOptions options)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new HindsightValidationException("The query is blank.") { ParamName = nameof(query) };
        }

        if (targets.Count == 0)
        {
            throw new HindsightValidationException(
                "At least one bank is required. For a single bank, call RecallMemoriesAsync directly — "
                + "the fan-out and merge add nothing when there is nothing to merge.")
            {
                ParamName = nameof(targets),
            };
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.BankId))
            {
                throw new HindsightValidationException("A bank id is blank.") { ParamName = nameof(targets) };
            }

            // The same bank twice would double its results and double-count it in the merge,
            // which is never what a caller means.
            if (!seen.Add(target.BankId))
            {
                throw new HindsightValidationException(
                    $"Bank '{target.BankId}' appears more than once. Each bank must appear once; "
                    + "duplicates would double its contribution to the merge.")
                {
                    ParamName = nameof(targets),
                };
            }

            if (target.PerBankLimit is { } targetLimit && targetLimit <= 0)
            {
                throw new HindsightValidationException(
                    $"The per-bank limit for '{target.BankId}' must be positive when set.")
                {
                    ParamName = nameof(targets),
                };
            }
        }

        if (options.PerBankLimit <= 0)
        {
            throw new HindsightValidationException("PerBankLimit must be positive.")
            {
                ParamName = nameof(options),
            };
        }

        if (options.MaxCandidates <= 0)
        {
            throw new HindsightValidationException("MaxCandidates must be positive.")
            {
                ParamName = nameof(options),
            };
        }

        if (options.MaxConcurrency is { } concurrency && concurrency <= 0)
        {
            throw new HindsightValidationException(
                "MaxConcurrency must be positive when set. Leave it null for no bound.")
            {
                ParamName = nameof(options),
            };
        }

        if (options.MaxPerBank is { } ceiling && ceiling <= 0)
        {
            throw new HindsightValidationException("MaxPerBank must be positive when set.")
            {
                ParamName = nameof(options),
            };
        }

        if (options.MinPerBank is { } floor && floor <= 0)
        {
            throw new HindsightValidationException("MinPerBank must be positive when set.")
            {
                ParamName = nameof(options),
            };
        }

        if (options.MinPerBank is { } floorValue && options.MaxPerBank is { } ceilingValue && floorValue > ceilingValue)
        {
            throw new HindsightValidationException(
                $"MinPerBank ({floorValue}) is above MaxPerBank ({ceilingValue}), which cannot be satisfied.")
            {
                ParamName = nameof(options),
            };
        }
    }
}
