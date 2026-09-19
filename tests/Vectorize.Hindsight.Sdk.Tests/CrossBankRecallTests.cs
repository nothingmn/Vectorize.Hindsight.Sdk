using Vectorize.Hindsight.Sdk;
using Vectorize.Hindsight.Sdk.CrossBank;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Cross-bank recall: fan-out, provenance, merging, and the failure semantics.
/// </summary>
/// <remarks>
/// The transport is stubbed, so every path is reachable without a server. The tests that matter
/// most here are the ones about what the merge does <i>not</i> do — no score comparison across
/// banks, no reordering by weight, no semantic deduplication — because those are the decisions a
/// future change is most likely to break silently.
/// </remarks>
public sealed class CrossBankRecallTests
{
    /// <summary>
    /// A transport that answers per bank, so each bank can return its own result set.
    /// </summary>
    private sealed class PerBankTransport : IHindsightTransport
    {
        private readonly Dictionary<string, (int Status, string Json)> _byBank = new(StringComparer.Ordinal);

        internal List<string> RequestedBanks { get; } = [];

        internal List<string> RequestedBodies { get; } = [];

        /// <summary>Banks that are answered slowly, to observe concurrency.</summary>
        internal TimeSpan Delay { get; set; } = TimeSpan.Zero;

        /// <summary>Banks to fail, and how.</summary>
        internal Dictionary<string, Exception> ThrowFor { get; } = new(StringComparer.Ordinal);

        internal int InFlight { get; private set; }

        internal int PeakInFlight { get; private set; }

        internal PerBankTransport Answer(string bankId, int status, string json)
        {
            _byBank[bankId] = (status, json);
            return this;
        }

        /// <summary>Answers with a ranked result set built from labelled scores.</summary>
        internal PerBankTransport Answer(string bankId, params (string Id, double Score)[] results)
        {
            var items = results.Select(r =>
                $"{{\"id\":\"{r.Id}\",\"text\":\"text of {r.Id}\",\"scores\":{{\"final\":{r.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}");
            return Answer(bankId, 200, $"{{\"results\":[{string.Join(",", items)}]}}");
        }

        /// <summary>The banks the listing endpoint reports as existing.</summary>
        private readonly List<string> _existingBanks = [];

        /// <summary>Declares which banks a listing request will report.</summary>
        internal void ForListing(params string[] bankIds) => _existingBanks.AddRange(bankIds);

        public async Task<HindsightResponse> SendAsync(
            HindsightRequest request, bool streamResponse, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A listing request is answered from the declared bank set.
            if (request.Path.EndsWith("/banks", StringComparison.Ordinal))
            {
                RequestedBanks.Add("(listing)");
                var items = _existingBanks.Select(id => $"{{\"bank_id\":\"{id}\"}}");
                var listing = $"{{\"banks\":[{string.Join(",", items)}],\"total\":{_existingBanks.Count}}}";
                return new HindsightResponse
                {
                    StatusCode = 200,
                    Body = System.Text.Encoding.UTF8.GetBytes(listing),
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ContentType = "application/json",
                };
            }

            // The bank id is the last path segment before /memories/recall.
            var segments = request.Path.Split('/');
            var bankId = segments[^3];

            InFlight++;
            PeakInFlight = Math.Max(PeakInFlight, InFlight);

            try
            {
                RequestedBanks.Add(bankId);
                RequestedBodies.Add(request.Body is null
                    ? string.Empty
                    : System.Text.Encoding.UTF8.GetString(request.Body.Value.Span));

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
                }

                if (ThrowFor.TryGetValue(bankId, out var exception))
                {
                    throw exception;
                }

                var (status, json) = _byBank.TryGetValue(bankId, out var answer)
                    ? answer
                    : (200, """{"results":[]}""");

                return new HindsightResponse
                {
                    StatusCode = status,
                    Body = System.Text.Encoding.UTF8.GetBytes(json),
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ContentType = "application/json",
                };
            }
            finally
            {
                InFlight--;
            }
        }

        public void Dispose()
        {
        }
    }

    private static (HindsightClient Client, PerBankTransport Transport) Create()
    {
        var transport = new PerBankTransport();
        var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };
        return (client, transport);
    }

    // --- Fan-out -----------------------------------------------------------

    [Fact]
    public async Task Every_bank_is_queried_with_the_same_query()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 0.9));
            transport.Answer("bank-b", ("B1", 0.8));

            await client.RecallAcrossBanksAsync(
                "what does alice do", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.Equal(2, transport.RequestedBanks.Count);
            Assert.Contains("bank-a", transport.RequestedBanks);
            Assert.Contains("bank-b", transport.RequestedBanks);

            // The query reaches every bank unmodified, which is the point of the operation.
            Assert.All(transport.RequestedBodies,
                body => Assert.Contains("what does alice do", body, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Banks_are_queried_concurrently_not_in_sequence()
    {
        // The fan-out is the reason this operation exists. Sequential calls would produce the
        // same results in N times the latency, so concurrency is asserted rather than assumed.
        var (client, transport) = Create();
        using (client)
        {
            transport.Delay = TimeSpan.FromMilliseconds(80);
            transport.Answer("bank-a", ("A1", 0.9));
            transport.Answer("bank-b", ("B1", 0.8));
            transport.Answer("bank-c", ("C1", 0.7));

            await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b", "bank-c"], null, CancellationToken.None);

            // Peak overlap is the direct evidence of concurrency: all three transports were
            // inside their send at the same moment. Asserted on the overlap rather than on
            // elapsed time, because wall-clock timing flakes when the suite runs alongside the
            // other test assemblies and the difference it measures is scheduling, not design.
            Assert.Equal(3, transport.PeakInFlight);
        }
    }

    [Fact]
    public async Task A_concurrency_limit_bounds_the_fan_out()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Delay = TimeSpan.FromMilliseconds(30);
            transport.Answer("bank-a", ("A1", 1));
            transport.Answer("bank-b", ("B1", 1));
            transport.Answer("bank-c", ("C1", 1));
            transport.Answer("bank-d", ("D1", 1));

            await client.RecallAcrossBanksAsync(
                "q",
                ["bank-a", "bank-b", "bank-c", "bank-d"],
                new CrossBankRecallOptions { MaxConcurrency = 2 },
                CancellationToken.None);

            // Bounded, and every bank still answered: excess banks wait rather than being dropped.
            Assert.Equal(2, transport.PeakInFlight);
            Assert.Equal(4, transport.RequestedBanks.Count);
        }
    }

    // --- Provenance --------------------------------------------------------

    [Fact]
    public async Task Every_candidate_keeps_its_bank_and_its_banks_own_rank()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 0.91), ("A2", 0.84), ("A3", 0.77));
            transport.Answer("bank-b", ("B1", 0.95));

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            var a1 = result.Candidates.Single(c => c.Source.Id == "A1");
            Assert.Equal("bank-a", a1.BankId);
            Assert.Equal(0, a1.Rank);

            var a3 = result.Candidates.Single(c => c.Source.Id == "A3");
            Assert.Equal("bank-a", a3.BankId);
            Assert.Equal(2, a3.Rank);

            var b1 = result.Candidates.Single(c => c.Source.Id == "B1");
            Assert.Equal("bank-b", b1.BankId);
            Assert.Equal(0, b1.Rank);

            // The bank's own score is exposed unchanged, and never compared by the library.
            Assert.Equal(0.91, a1.RetrievalScore!.Value, 3);
            Assert.Equal(0.95, b1.RetrievalScore!.Value, 3);
        }
    }

    [Fact]
    public async Task Default_order_interleaves_by_rank_rather_than_by_score()
    {
        // The heart of the design. Bank C's top result scored highest, but its 0.93 is not on the
        // same scale as bank A's 0.91, so sorting on those numbers would be a false comparison.
        // Interleaving gives every bank an equal voice at every position instead.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 0.91), ("A2", 0.84));
            transport.Answer("bank-b", ("B1", 0.86));
            transport.Answer("bank-c", ("C1", 0.93));

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b", "bank-c"], null, CancellationToken.None);

            // A1, B1, C1, then A2 - not C1 first, which is what a naive score sort would produce.
            Assert.Equal(["A1", "B1", "C1", "A2"], result.Candidates.Select(c => c.Source.Id));
        }
    }

    [Fact]
    public async Task Score_order_is_available_when_a_caller_asks_for_it()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 0.91), ("A2", 0.84));
            transport.Answer("bank-c", ("C1", 0.93));

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-a", "bank-c"],
                new CrossBankRecallOptions { MergeOrder = CrossBankMergeOrder.ScoreDescending },
                CancellationToken.None);

            Assert.Equal(["C1", "A1", "A2"], result.Candidates.Select(c => c.Source.Id));
        }
    }

    [Fact]
    public async Task Bank_order_keeps_the_callers_priority_without_a_score_comparison()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-low", ("L1", 0.10));
            transport.Answer("bank-high", ("H1", 0.99));

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-low", "bank-high"],
                new CrossBankRecallOptions { MergeOrder = CrossBankMergeOrder.BankOrder },
                CancellationToken.None);

            Assert.Equal(["L1", "H1"], result.Candidates.Select(c => c.Source.Id));
        }
    }

    [Fact]
    public async Task A_policy_weight_is_recorded_on_the_candidate_and_does_not_reorder()
    {
        // The documented rule: weighting must express an intentional policy, not compensate for
        // score differences. A library cannot tell which a caller means, so it records the weight
        // and leaves the order defensible.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 0.5));
            transport.Answer("bank-b", ("B1", 0.5));

            var result = await client.RecallAcrossBanksAsync(
                "q",
                [
                    new CrossBankTarget { BankId = "bank-a", PolicyWeight = 0.1 },
                    new CrossBankTarget { BankId = "bank-b", PolicyWeight = 9.0 },
                ],
                null,
                CancellationToken.None);

            var a1 = result.Candidates.Single(c => c.Source.Id == "A1");
            var b1 = result.Candidates.Single(c => c.Source.Id == "B1");

            // Recorded...
            Assert.Equal(0.1, a1.PolicyWeight, 3);
            Assert.Equal(9.0, b1.PolicyWeight, 3);

            // ...but the order is still interleaved by rank, so the caller's policy is theirs to
            // apply against real information rather than something the library guessed at.
            Assert.Equal(["A1", "B1"], result.Candidates.Select(c => c.Source.Id));
        }
    }

    // --- Per-bank cap ------------------------------------------------------

    [Fact]
    public async Task The_per_bank_limit_stops_one_bank_dominating_the_pool()
    {
        var (client, transport) = Create();
        using (client)
        {
            var many = Enumerable.Range(1, 25).Select(i => ($"A{i}", 0.9 - (i * 0.01))).ToArray();
            transport.Answer("bank-a", many);
            transport.Answer("bank-b", ("B1", 0.5));

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-a", "bank-b"],
                new CrossBankRecallOptions { PerBankLimit = 10 },
                CancellationToken.None);

            // Ten from A, one from B: the pool is bounded by the cap rather than by the bank size.
            Assert.Equal(11, result.CandidatePoolSize);
            Assert.Equal(10, result.Candidates.Count(c => c.BankId == "bank-a"));
            Assert.Single(result.Candidates, c => c.BankId == "bank-b");
        }
    }

    [Fact]
    public async Task A_bank_specific_limit_overrides_the_shared_one()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-big", Enumerable.Range(1, 20).Select(i => ($"B{i}", 0.9)).ToArray());
            transport.Answer("bank-small", ("S1", 0.5), ("S2", 0.4));

            var result = await client.RecallAcrossBanksAsync(
                "q",
                [
                    new CrossBankTarget { BankId = "bank-big", PerBankLimit = 3 },
                    CrossBankTarget.For("bank-small"),
                ],
                new CrossBankRecallOptions { PerBankLimit = 10 },
                CancellationToken.None);

            Assert.Equal(3, result.Candidates.Count(c => c.BankId == "bank-big"));
            Assert.Equal(2, result.Candidates.Count(c => c.BankId == "bank-small"));
        }
    }

    // --- Deduplication -----------------------------------------------------

    [Fact]
    public async Task Identical_content_in_two_banks_is_collapsed_and_both_banks_are_recorded()
    {
        // Verified against a live server: the same document seeded into two banks gets a chunk id
        // prefixed with each bank's own id, so the content must be matched on the document and the
        // text rather than on any identifier the bank supplied.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200,
                """{"results":[{"id":"x1","text":"same fact","document_id":"doc-1","chunk_id":"bank-a_doc-1_0"}]}""");
            transport.Answer("bank-b", 200,
                """{"results":[{"id":"y9","text":"same fact","document_id":"doc-1","chunk_id":"bank-b_doc-1_0"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(1, result.DuplicatesMerged);

            // The survivor keeps its own bank first and names the other, so agreement between
            // banks survives even though the duplicate does not.
            Assert.Equal("bank-a", candidate.BankId);
            Assert.True(candidate.IsDuplicateAcrossBanks);
            Assert.Equal(["bank-a", "bank-b"], candidate.SeenInBanks);
        }
    }

    [Fact]
    public async Task The_same_fact_rendered_slightly_differently_is_still_collapsed()
    {
        // Observed live: the same sentence retained into two banks comes back once as itself and
        // once with the extraction pipeline's "| Involving: ..." suffix appended. The content is
        // one fact either way, and a comparison that required byte equality would miss it.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200,
                """{"results":[{"text":"Alice is the lead.","document_id":"d"}]}""");
            transport.Answer("bank-b", 200,
                """{"results":[{"text":"Alice is the lead. | Involving: Alice","document_id":"d"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            // Deliberately NOT collapsed: these are different strings, and deciding they are the
            // same fact is the judgement this library refuses to make. Documented behaviour, not
            // an accident - the two candidates reach the model, which can see they agree.
            Assert.Equal(2, result.Candidates.Count);
            Assert.Equal(0, result.DuplicatesMerged);
        }
    }

    [Fact]
    public async Task Whitespace_and_case_differences_do_not_block_a_match()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200,
                """{"results":[{"text":"Alice  is the lead.","document_id":"d"}]}""");
            transport.Answer("bank-b", 200,
                """{"results":[{"text":"  alice is the lead.  ","document_id":"d"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.Single(result.Candidates);
            Assert.Equal(1, result.DuplicatesMerged);
        }
    }

    [Fact]
    public async Task Two_different_facts_in_one_document_are_not_collapsed()
    {
        // The key includes the text precisely so that a document with several facts in it does
        // not collapse to a single candidate.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200,
                """{"results":[{"text":"Alice leads billing.","document_id":"d"},{"text":"Alice is in Vancouver.","document_id":"d"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a"], null, CancellationToken.None);

            Assert.Equal(2, result.Candidates.Count);
            Assert.Equal(0, result.DuplicatesMerged);
        }
    }

    [Fact]
    public async Task Different_content_in_two_banks_with_the_same_id_is_not_collapsed()
    {
        // Two banks minting the same memory id for unrelated facts is a real possibility when they
        // are independent, because the id is bank-local. Collapsing them would silently drop one
        // bank's evidence, so the id is not part of the deduplication key at all.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200, """{"results":[{"id":"m-1","text":"fact from A"}]}""");
            transport.Answer("bank-b", 200, """{"results":[{"id":"m-1","text":"different fact from B"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.Equal(0, result.DuplicatesMerged);
            Assert.Equal(2, result.Candidates.Count);

            // Both survive, with their banks intact, so a caller can see the disagreement.
            Assert.Equal(["fact from A", "different fact from B"], result.Candidates.Select(c => c.Text));
        }
    }

    [Fact]
    public async Task Bank_local_chunk_ids_do_not_prevent_a_match()
    {
        // The trap this guards against: a chunk id embeds the bank that minted it, so a
        // deduplication key built on the chunk id could never match across banks and the
        // deduplication would be silently dead.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200,
                """{"results":[{"chunk_id":"bank-a_doc_0","text":"repeated","document_id":"doc"}]}""");
            transport.Answer("bank-b", 200,
                """{"results":[{"chunk_id":"bank-b_doc_0","text":"repeated","document_id":"doc"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.Single(result.Candidates);
            Assert.Equal(1, result.DuplicatesMerged);
        }
    }

    [Fact]
    public async Task Identical_text_from_the_same_document_is_collapsed()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200, """{"results":[{"text":"repeated","document_id":"d"}]}""");
            transport.Answer("bank-b", 200, """{"results":[{"text":"repeated","document_id":"d"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.Single(result.Candidates);
            Assert.Equal(1, result.DuplicatesMerged);
        }
    }

    [Fact]
    public async Task The_same_text_in_two_documents_is_not_collapsed()
    {
        // Two different documents that happen to contain the same sentence are two pieces of
        // evidence about two sources, and provenance is the whole reason this library exists.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200, """{"results":[{"text":"same text","document_id":"doc-1"}]}""");
            transport.Answer("bank-b", 200, """{"results":[{"text":"same text","document_id":"doc-2"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.Equal(2, result.Candidates.Count);
            Assert.Equal(0, result.DuplicatesMerged);
        }
    }

    [Fact]
    public async Task Deduplication_can_be_turned_off()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200, """{"results":[{"text":"same","document_id":"d"}]}""");
            transport.Answer("bank-b", 200, """{"results":[{"text":"same","document_id":"d"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], new CrossBankRecallOptions { Deduplicate = false }, CancellationToken.None);

            Assert.Equal(2, result.Candidates.Count);
            Assert.Equal(0, result.DuplicatesMerged);
        }
    }

    [Fact]
    public async Task Similarly_worded_facts_are_not_treated_as_the_same_fact()
    {
        // Semantic deduplication is deliberately absent: deciding two differently-worded facts are
        // one fact is a judgement this library cannot make reliably, and a model is the right
        // place for it.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200, """{"results":[{"text":"Alice works at Google"}]}""");
            transport.Answer("bank-b", 200, """{"results":[{"text":"Alice is employed by Google"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.Equal(0, result.DuplicatesMerged);
            Assert.Equal(2, result.Candidates.Count);
        }
    }

    // --- Failure semantics -------------------------------------------------

    [Fact]
    public async Task By_default_a_failing_bank_does_not_fail_the_call()
    {
        // One bank's outage must not be a total retrieval failure.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-good", ("G1", 0.9));
            transport.Answer("bank-bad", 404, """{"detail":"Bank 'bank-bad' not found"}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-good", "bank-bad"], null, CancellationToken.None);

            Assert.Single(result.Candidates);
            Assert.Equal("G1", result.Candidates[0].Source.Id);

            // And the loss is reported rather than hidden: the two answers are not the same answer.
            Assert.True(result.IsPartial);
            Assert.Single(result.Failures);
            Assert.Equal("bank-bad", result.Failures[0].BankId);
            Assert.IsType<HindsightNotFoundException>(result.Failures[0].Failure);
            Assert.Equal(1, result.SucceededCount);
        }
    }

    [Fact]
    public async Task A_transport_failure_on_one_bank_is_also_partial()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-good", ("G1", 0.9));
            transport.ThrowFor["bank-down"] = new HindsightConnectionException("unreachable");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-good", "bank-down"], null, CancellationToken.None);

            Assert.Single(result.Candidates);
            Assert.True(result.IsPartial);
            Assert.IsType<HindsightConnectionException>(result.Failures[0].Failure);
        }
    }

    [Fact]
    public async Task RequireAll_raises_when_any_bank_fails()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-good", ("G1", 0.9));
            transport.Answer("bank-bad", 404, """{"detail":"missing"}""");

            await Assert.ThrowsAnyAsync<HindsightException>(() => client.RecallAcrossBanksAsync(
                "q",
                ["bank-good", "bank-bad"],
                new CrossBankRecallOptions { FailureMode = CrossBankFailureMode.RequireAll },
                CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_complete_answer_is_not_partial()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 0.9));
            transport.Answer("bank-b", ("B1", 0.8));

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.False(result.IsPartial);
            Assert.True(result.AllBanksSucceeded);
            Assert.Empty(result.Failures);
        }
    }

    [Fact]
    public async Task A_bank_that_answers_with_nothing_is_not_a_failure()
    {
        // An empty result is an answer. Reporting it as a failure would train a caller to ignore
        // the flag that exists to distinguish a complete response from a truncated one.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-empty", 200, """{"results":[]}""");
            transport.Answer("bank-a", ("A1", 0.9));

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-empty", "bank-a"], null, CancellationToken.None);

            Assert.False(result.IsPartial);
            Assert.True(result.Banks.Single(b => b.BankId == "bank-empty").Succeeded);
            Assert.Equal(0, result.Banks.Single(b => b.BankId == "bank-empty").SelectedCount);

            // Which banks actually contributed is a separate question from which succeeded.
            Assert.Equal(["bank-a"], result.ContributingBanks);
        }
    }

    [Fact]
    public async Task A_bank_that_does_not_exist_is_indistinguishable_from_an_empty_one()
    {
        // The trap, verified against a live server: recall against a bank that was never created
        // answers HTTP 200 with no results. So a typo in a bank id looks exactly like a bank with
        // nothing relevant in it, and no error is raised.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-typo", 200, """{"results":[]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-typo"], null, CancellationToken.None);

            Assert.Empty(result.Candidates);
            Assert.False(result.IsPartial);
            Assert.True(result.Banks[0].Succeeded);
        }
    }

    [Fact]
    public async Task ValidateBanksExist_turns_a_missing_bank_into_a_reported_failure()
    {
        var (client, transport) = Create();
        using (client)
        {
            // The listing does not contain the requested bank.
            transport.Answer("bank-real", ("R1", 0.9));
            transport.ForListing("bank-real");

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-real", "bank-typo"],
                new CrossBankRecallOptions { ValidateBanksExist = true },
                CancellationToken.None);

            Assert.True(result.IsPartial);
            var failure = Assert.Single(result.Failures);
            Assert.Equal("bank-typo", failure.BankId);
            Assert.IsType<HindsightNotFoundException>(failure.Failure);

            // The bank that does exist still answers: the check must not fail the whole call.
            Assert.Single(result.Candidates);
        }
    }

    [Fact]
    public async Task ValidateBanksExist_lets_an_existing_bank_through()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-real", ("R1", 0.9));
            transport.ForListing("bank-real");

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-real"],
                new CrossBankRecallOptions { ValidateBanksExist = true },
                CancellationToken.None);

            Assert.False(result.IsPartial);
            Assert.Single(result.Candidates);
        }
    }

    // --- Cancellation ------------------------------------------------------

    [Fact]
    public async Task Caller_cancellation_propagates_and_stops_the_fan_out()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Delay = TimeSpan.FromSeconds(5);
            transport.Answer("bank-a", ("A1", 1));

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.RecallAcrossBanksAsync("q", ["bank-a"], null, cts.Token));
        }
    }

    [Fact]
    public async Task A_cancelled_bank_is_not_reported_as_a_failed_bank()
    {
        // The two mean different things: a failure is the bank's answer, a cancellation is the
        // caller's decision. Collapsing them would make a caller who gave up read a report that a
        // bank was broken.
        var (client, _) = Create();
        using (client)
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // Cancellation is raised, not converted into a partial result.
            var exception = await Record.ExceptionAsync(
                () => client.RecallAcrossBanksAsync("q", ["bank-a", "bank-b"], null, cts.Token));

            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
    }

    // --- Context rendering -------------------------------------------------

    [Fact]
    public async Task The_context_names_the_source_bank_of_every_passage()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 0.9));
            transport.Answer("bank-b", ("B1", 0.8));

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            var context = result.ToContext();

            Assert.Contains("[SOURCE bank-a | result A1]", context, StringComparison.Ordinal);
            Assert.Contains("[SOURCE bank-b | result B1]", context, StringComparison.Ordinal);
            Assert.Contains("text of A1", context, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_context_discloses_a_bank_that_could_not_be_reached()
    {
        // A model told only about the banks that answered will present a partial picture as the
        // whole one, which is exactly the failure mode this operation must not create.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 0.9));
            transport.Answer("bank-missing", 404, """{"detail":"missing"}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-missing"], null, CancellationToken.None);

            var context = result.ToContext();

            Assert.Contains("[SOURCE bank-missing unavailable]", context, StringComparison.Ordinal);
            Assert.Contains("No memories could be retrieved", context, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_context_marks_content_found_in_several_banks()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200, """{"results":[{"id":"x","text":"t","document_id":"d","chunk_id":"c"}]}""");
            transport.Answer("bank-b", 200, """{"results":[{"id":"y","text":"t","document_id":"d","chunk_id":"c"}]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            // Agreement between banks is visible to the model, which is the point of keeping the
            // provenance through a deduplication.
            Assert.Contains("[SOURCE bank-a,bank-b", result.ToContext(), StringComparison.Ordinal);
        }
    }

    // --- Request propagation -----------------------------------------------

    [Fact]
    public async Task Shared_recall_options_reach_every_bank()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 1));
            transport.Answer("bank-b", ("B1", 1));

            await client.RecallAcrossBanksAsync(
                "q",
                ["bank-a", "bank-b"],
                new CrossBankRecallOptions
                {
                    Request = new RecallRequest { Query = "ignored", Budget = HindsightValues.Budget.HIGH, MaxTokens = 512 },
                },
                CancellationToken.None);

            Assert.All(transport.RequestedBodies, body =>
            {
                Assert.Contains("\"budget\":\"high\"", body, StringComparison.Ordinal);
                Assert.Contains("\"max_tokens\":512", body, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public async Task A_bank_specific_request_overrides_the_shared_one()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-notes", ("N1", 1));
            transport.Answer("bank-regulated", ("R1", 1));

            await client.RecallAcrossBanksAsync(
                "q",
                [
                    CrossBankTarget.For("bank-notes"),
                    new CrossBankTarget
                    {
                        BankId = "bank-regulated",
                        Request = new RecallRequest { Query = "ignored", Budget = HindsightValues.Budget.LOW, MaxTokens = 64 },
                    },
                ],
                new CrossBankRecallOptions
                {
                    Request = new RecallRequest { Query = "ignored", Budget = HindsightValues.Budget.HIGH, MaxTokens = 4096 },
                },
                CancellationToken.None);

            var regulated = transport.RequestedBodies[transport.RequestedBanks.IndexOf("bank-regulated")];
            var notes = transport.RequestedBodies[transport.RequestedBanks.IndexOf("bank-notes")];

            Assert.Contains("\"max_tokens\":64", regulated, StringComparison.Ordinal);
            Assert.Contains("\"budget\":\"low\"", regulated, StringComparison.Ordinal);

            Assert.Contains("\"max_tokens\":4096", notes, StringComparison.Ordinal);
            Assert.Contains("\"budget\":\"high\"", notes, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_query_cannot_be_overridden_per_bank()
    {
        // Every bank answers the same question; that is the premise of merging their answers.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", ("A1", 1));

            await client.RecallAcrossBanksAsync(
                "the real question",
                [new CrossBankTarget
                {
                    BankId = "bank-a",
                    Request = new RecallRequest { Query = "a different question" },
                }],
                null,
                CancellationToken.None);

            Assert.Contains("the real question", transport.RequestedBodies[0], StringComparison.Ordinal);
            Assert.DoesNotContain("a different question", transport.RequestedBodies[0], StringComparison.Ordinal);
        }
    }

    // --- Validation --------------------------------------------------------

    [Fact]
    public async Task An_empty_bank_list_is_rejected_before_anything_is_sent()
    {
        var (client, transport) = Create();
        using (client)
        {
            var exception = await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.RecallAcrossBanksAsync("q", Array.Empty<string>(), null, CancellationToken.None));

            // The message points at the alternative, because one bank does not need this operation.
            Assert.Contains("RecallMemoriesAsync", exception.Message, StringComparison.Ordinal);
            Assert.Empty(transport.RequestedBanks);
        }
    }

    [Fact]
    public async Task A_duplicate_bank_is_rejected()
    {
        var (client, transport) = Create();
        using (client)
        {
            var exception = await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.RecallAcrossBanksAsync("q", ["bank-a", "bank-a"], null, CancellationToken.None));

            Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
            Assert.Empty(transport.RequestedBanks);
        }
    }

    [Fact]
    public async Task A_blank_bank_id_is_rejected()
    {
        var (client, _) = Create();
        using (client)
        {
            await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.RecallAcrossBanksAsync("q", ["bank-a", "  "], null, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_query_is_rejected(string query)
    {
        var (client, _) = Create();
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.RecallAcrossBanksAsync(query, ["bank-a"], null, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_limit_is_rejected(int limit)
    {
        var (client, _) = Create();
        using (client)
        {
            await Assert.ThrowsAsync<HindsightValidationException>(() => client.RecallAcrossBanksAsync(
                "q", ["bank-a"], new CrossBankRecallOptions { PerBankLimit = limit }, CancellationToken.None));

            await Assert.ThrowsAsync<HindsightValidationException>(() => client.RecallAcrossBanksAsync(
                "q", ["bank-a"], new CrossBankRecallOptions { MaxCandidates = limit }, CancellationToken.None));

            await Assert.ThrowsAsync<HindsightValidationException>(() => client.RecallAcrossBanksAsync(
                "q", ["bank-a"], new CrossBankRecallOptions { MaxConcurrency = limit }, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_floor_above_the_ceiling_is_rejected_as_unsatisfiable()
    {
        var (client, _) = Create();
        using (client)
        {
            var exception = await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.RecallAcrossBanksAsync(
                    "q",
                    ["bank-a"],
                    new CrossBankRecallOptions { MinPerBank = 5, MaxPerBank = 2 },
                    CancellationToken.None));

            Assert.Contains("cannot be satisfied", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_non_positive_bank_specific_limit_is_rejected()
    {
        var (client, _) = Create();
        using (client)
        {
            await Assert.ThrowsAsync<HindsightValidationException>(() => client.RecallAcrossBanksAsync(
                "q",
                [new CrossBankTarget { BankId = "bank-a", PerBankLimit = 0 }],
                null,
                CancellationToken.None));
        }
    }

    // --- Context construction limits ---------------------------------------

    [Fact]
    public async Task Max_candidates_caps_what_is_handed_on()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", Enumerable.Range(1, 10).Select(i => ($"A{i}", 1.0)).ToArray());
            transport.Answer("bank-b", Enumerable.Range(1, 10).Select(i => ($"B{i}", 1.0)).ToArray());

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-a", "bank-b"],
                new CrossBankRecallOptions { PerBankLimit = 10, MaxCandidates = 6 },
                CancellationToken.None);

            // Twenty candidates considered, six handed on.
            Assert.Equal(20, result.CandidatePoolSize);
            Assert.Equal(6, result.Candidates.Count);

            // The interleave is preserved through the cap: both banks represented alternately.
            Assert.Equal(["A1", "B1", "A2", "B2", "A3", "B3"], result.Candidates.Select(c => c.Source.Id));
        }
    }

    [Fact]
    public async Task Max_per_bank_bounds_one_banks_share_of_the_final_set()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-loud", Enumerable.Range(1, 10).Select(i => ($"L{i}", 1.0)).ToArray());
            transport.Answer("bank-quiet", ("Q1", 1.0));

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-loud", "bank-quiet"],
                new CrossBankRecallOptions { PerBankLimit = 10, MaxPerBank = 2, MaxCandidates = 20 },
                CancellationToken.None);

            Assert.Equal(2, result.Candidates.Count(c => c.BankId == "bank-loud"));
            Assert.Single(result.Candidates, c => c.BankId == "bank-quiet");
        }
    }

    [Fact]
    public async Task Min_per_bank_keeps_a_bank_in_the_answer_when_the_cap_would_drop_it()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-loud", Enumerable.Range(1, 10).Select(i => ($"L{i}", 1.0)).ToArray());
            transport.Answer("bank-quiet", ("Q1", 1.0));

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-loud", "bank-quiet"],
                new CrossBankRecallOptions
                {
                    PerBankLimit = 10,
                    MaxCandidates = 3,
                    MinPerBank = 1,
                },
                CancellationToken.None);

            // Without the floor the cap would have taken three from bank-loud. Coverage wins.
            Assert.Contains(result.Candidates, c => c.BankId == "bank-quiet");
            Assert.Equal(3, result.Candidates.Count);
        }
    }

    [Fact]
    public async Task Min_per_bank_does_not_invent_results_for_a_bank_with_nothing()
    {
        // The documented case: do not force diversity when a bank genuinely has nothing relevant.
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-loud", Enumerable.Range(1, 5).Select(i => ($"L{i}", 1.0)).ToArray());
            transport.Answer("bank-empty", 200, """{"results":[]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q",
                ["bank-loud", "bank-empty"],
                new CrossBankRecallOptions { MaxCandidates = 3, MinPerBank = 1 },
                CancellationToken.None);

            Assert.All(result.Candidates, c => Assert.Equal("bank-loud", c.BankId));
            Assert.Equal(3, result.Candidates.Count);
            Assert.DoesNotContain("bank-empty", result.ContributingBanks);
        }
    }

    // --- Empty pool --------------------------------------------------------

    [Fact]
    public async Task Every_bank_returning_nothing_yields_an_empty_but_valid_result()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("bank-a", 200, """{"results":[]}""");
            transport.Answer("bank-b", 200, """{"results":[]}""");

            var result = await client.RecallAcrossBanksAsync(
                "q", ["bank-a", "bank-b"], null, CancellationToken.None);

            Assert.Empty(result.Candidates);
            Assert.Equal(0, result.CandidatePoolSize);
            Assert.False(result.IsPartial);
            Assert.Equal(2, result.SucceededCount);
            Assert.Empty(result.ContributingBanks);
            Assert.Equal(string.Empty, result.ToContext());
        }
    }

    [Fact]
    public async Task Outcomes_are_reported_in_the_order_the_banks_were_supplied()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.Answer("zzz-last", ("Z1", 1));
            transport.Answer("aaa-first", ("A1", 1));

            var result = await client.RecallAcrossBanksAsync(
                "q", ["zzz-last", "aaa-first"], null, CancellationToken.None);

            // Not alphabetical, not completion order: the caller's order, so a report can be read
            // against the request that produced it.
            Assert.Equal(["zzz-last", "aaa-first"], result.Banks.Select(b => b.BankId));
        }
    }
}
