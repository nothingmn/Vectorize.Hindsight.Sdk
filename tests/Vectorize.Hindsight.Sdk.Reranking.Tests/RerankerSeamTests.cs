using Vectorize.Hindsight.Sdk.Reranking;
using Vectorize.Hindsight.Sdk.Reranking.Providers;
using Vectorize.Hindsight.Sdk.Reranking.TypeSafe;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Reranking.Tests;

/// <summary>
/// The reranker seam: the identity default, and the TypeSafe Choice provider.
/// </summary>
/// <remarks>
/// The TypeSafe reranker is exercised through its <see cref="IChoiceAsker"/> seam, so every path is
/// reachable with no network and no API key. That also means these tests assert the mapping from a
/// Choice distribution onto candidate scores — which is where this provider can go quietly wrong —
/// rather than asserting that a remote service behaved.
/// </remarks>
public sealed class RerankerSeamTests
{
    /// <summary>An asker that answers with a fixed distribution, and records what it was asked.</summary>
    private sealed class StubAsker : IChoiceAsker
    {
        private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, double>> _answer;

        internal StubAsker(
            Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, double>> answer) =>
            _answer = answer;

        internal int CallCount { get; private set; }

        internal string? LastInstructions { get; private set; }

        internal string? LastState { get; private set; }

        internal IReadOnlyDictionary<string, string>? LastOptions { get; private set; }

        public Task<IReadOnlyDictionary<string, double>> AskChoiceAsync(
            string instructions,
            string state,
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastInstructions = instructions;
            LastState = state;
            LastOptions = options;
            return Task.FromResult(_answer(instructions, state, options));
        }
    }

    /// <summary>An asker that always throws.</summary>
    private sealed class FailingAsker : IChoiceAsker
    {
        public Task<IReadOnlyDictionary<string, double>> AskChoiceAsync(
            string instructions,
            string state,
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the service is unreachable");
    }

    // --- The identity default ----------------------------------------------

    [Fact]
    public async Task The_identity_reranker_preserves_the_order_and_reports_itself()
    {
        var documents = new[] { "first", "second", "third" };

        var scores = await IdentityReranker.Instance.RerankAsync(
            "anything", documents, CancellationToken.None);

        Assert.Equal(3, scores.Count);
        Assert.True(IdentityReranker.Instance.IsIdentity);

        // Descending by position, so a caller that sorts by score descending gets the input order
        // back rather than relying on a stable sort to preserve it by accident.
        var reordered = documents
            .Select((document, index) => (document, score: scores[index]))
            .OrderByDescending(pair => pair.score)
            .Select(pair => pair.document);

        Assert.Equal(documents, reordered);
    }

    [Fact]
    public async Task The_identity_reranker_handles_an_empty_list()
    {
        var scores = await IdentityReranker.Instance.RerankAsync("q", [], CancellationToken.None);
        Assert.Empty(scores);
    }

    [Fact]
    public async Task A_choice_reranker_is_not_reported_as_identity()
    {
        // The property the pipeline reads to decide whether to claim a rerank happened.
        var reranker = new TypeSafeChoiceReranker(new StubAsker((_, _, options) => Distribute(options.First().Key)));

        Assert.False(((IReranker)reranker).IsIdentity);

        await reranker.RerankAsync("q", ["a"], CancellationToken.None);
    }

    private static Dictionary<string, double> Distribute(params string[] winners) =>
        winners.ToDictionary(key => key, _ => 1.0 / winners.Length, StringComparer.Ordinal);

    // --- The TypeSafe Choice reranker ---------------------------------------

    [Fact]
    public async Task The_highest_probability_candidate_scores_highest()
    {
        // The core claim: a Choice distribution is usable as a ranking because the candidates
        // competed in one judgement.
        var asker = new StubAsker((_, _, _) => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["candidate_0"] = 0.1,
            ["candidate_1"] = 0.75,
            ["candidate_2"] = 0.15,
        });

        var reranker = new TypeSafeChoiceReranker(asker);

        var scores = await reranker.RerankAsync(
            "who leads billing?", ["passage a", "passage b", "passage c"], CancellationToken.None);

        Assert.Equal(3, scores.Count);
        Assert.Equal(0.75, scores[1], 6);

        // And the ordering the pipeline applies puts candidate 1 first.
        var order = scores.Select((score, index) => (score, index))
            .OrderByDescending(pair => pair.score)
            .Select(pair => pair.index);
        Assert.Equal([1, 2, 0], order);
    }

    [Fact]
    public async Task Candidates_are_labelled_by_position_and_the_passages_travel_in_the_state()
    {
        // Option names are sent to the model and a whole passage as a key would be counted as part
        // of the question, so keys are positions and the text belongs in the state. This asserts the
        // mapping is exact, which is the thing that must not go wrong.
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.First()));

        var reranker = new TypeSafeChoiceReranker(asker);
        await reranker.RerankAsync("q", ["first passage", "second passage"], CancellationToken.None);

        Assert.NotNull(asker.LastOptions);
        Assert.Equal(["candidate_0", "candidate_1"], asker.LastOptions!.Keys);

        Assert.Contains("first passage", asker.LastState!, StringComparison.Ordinal);
        Assert.Contains("[1]", asker.LastState!, StringComparison.Ordinal);
        Assert.Contains("second passage", asker.LastState!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_call_scores_every_candidate()
    {
        // The reason this provider is worth having: one remote call ranks the whole pool, rather
        // than one call per candidate.
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.ToArray()));

        var reranker = new TypeSafeChoiceReranker(asker);
        var documents = Enumerable.Range(1, 20).Select(index => $"passage {index}").ToArray();

        await reranker.RerankAsync("q", documents, CancellationToken.None);

        Assert.Equal(1, asker.CallCount);
    }

    [Fact]
    public async Task A_missing_option_is_zero_rather_than_an_error()
    {
        // The service may legitimately omit an option it judged impossible, which is a real answer.
        var asker = new StubAsker((_, _, _) => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["candidate_1"] = 0.9,
        });

        var reranker = new TypeSafeChoiceReranker(asker);
        var scores = await reranker.RerankAsync("q", ["a", "b", "c"], CancellationToken.None);

        Assert.Equal(0.0, scores[0]);
        Assert.Equal(0.9, scores[1], 6);
        Assert.Equal(0.0, scores[2]);
    }

    [Fact]
    public async Task A_negative_probability_is_clamped_rather_than_inverting_the_order()
    {
        // A negative score would sort a candidate below one the model ignored entirely, which
        // inverts the meaning of the distribution.
        var asker = new StubAsker((_, _, _) => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["candidate_0"] = -0.5,
            ["candidate_1"] = 0.5,
        });

        var reranker = new TypeSafeChoiceReranker(asker);
        var scores = await reranker.RerankAsync("q", ["a", "b"], CancellationToken.None);

        Assert.Equal(0.0, scores[0]);
        Assert.Equal(0.5, scores[1], 6);
    }

    [Fact]
    public async Task Candidates_beyond_the_question_limit_follow_and_are_not_dropped()
    {
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.First()));

        var reranker = new TypeSafeChoiceReranker(
            asker,
            new TypeSafeRerankerOptions { MaxCandidatesPerQuestion = 2 });

        var scores = await reranker.RerankAsync("q", ["a", "b", "c", "d"], CancellationToken.None);

        // Every candidate gets a score; the caller's candidate set is never silently truncated.
        Assert.Equal(4, scores.Count);
        Assert.Equal(2, asker.LastOptions!.Count);

        // And the ones outside the question sort below everything actually considered.
        Assert.True(scores[0] > scores[2]);
        Assert.True(scores[0] > scores[3]);
        Assert.Equal(2, reranker.LastDiagnostics!.CandidateCount);
    }

    [Fact]
    public async Task The_question_carries_the_query_and_the_candidate_count()
    {
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.First()));

        var reranker = new TypeSafeChoiceReranker(asker);
        await reranker.RerankAsync("when did billing migrate?", ["a", "b"], CancellationToken.None);

        Assert.Contains("when did billing migrate?", asker.LastInstructions!, StringComparison.Ordinal);
        Assert.Contains("2 passages", asker.LastInstructions!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_guidance_is_appended_without_replacing_the_question()
    {
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.First()));

        var reranker = new TypeSafeChoiceReranker(asker, new TypeSafeRerankerOptions
        {
            Instructions = "Which passage is most authoritative?",
            Criteria = ["Prefer policy statements over anecdotes."],
        });

        await reranker.RerankAsync("q", ["a"], CancellationToken.None);

        Assert.Contains("most authoritative", asker.LastInstructions!, StringComparison.Ordinal);
        Assert.Contains("Prefer policy statements", asker.LastInstructions!, StringComparison.Ordinal);

        // The query is appended regardless, so a caller's wording cannot omit it.
        Assert.Contains("q", asker.LastInstructions!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_noul_mode_scores_each_candidate_independently()
    {
        var asker = new StubAsker((_, _, _) => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["relevant"] = 0.8,
            ["irrelevant"] = 0.2,
        });

        var reranker = new TypeSafeChoiceReranker(asker, new TypeSafeRerankerOptions
        {
            Mode = TypeSafeRerankMode.Noul,
        });

        var scores = await reranker.RerankAsync("q", ["a", "b", "c"], CancellationToken.None);

        Assert.Equal(3, scores.Count);
        Assert.All(scores, score => Assert.Equal(0.8, score, 6));

        // One call per candidate, which is exactly why Choice is the default: these three equal
        // scores rank nothing.
        Assert.Equal(3, asker.CallCount);
    }

    [Fact]
    public async Task Diagnostics_report_when_the_answer_did_not_discriminate()
    {
        // The failure that does not raise: a successful call whose distribution put all probability
        // on one candidate, so the resulting order is not evidence of anything.
        var asker = new StubAsker((_, _, _) => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["candidate_0"] = 1.0,
            ["candidate_1"] = 0.0,
            ["candidate_2"] = 0.0,
        });

        var reranker = new TypeSafeChoiceReranker(asker);
        await reranker.RerankAsync("q", ["a", "b", "c"], CancellationToken.None);

        var diagnostics = reranker.LastDiagnostics!;
        Assert.Equal(TypeSafeRerankMode.Choice, diagnostics.Mode);
        Assert.Equal(3, diagnostics.CandidateCount);
        Assert.Equal(1, diagnostics.OptionsWithProbability);

        Assert.False(reranker.LastRerankDiscriminated());
    }

    [Fact]
    public async Task Diagnostics_report_an_answer_that_did_discriminate()
    {
        var asker = new StubAsker((_, _, _) => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["candidate_0"] = 0.6,
            ["candidate_1"] = 0.3,
            ["candidate_2"] = 0.1,
        });

        var reranker = new TypeSafeChoiceReranker(asker);
        await reranker.RerankAsync("q", ["a", "b", "c"], CancellationToken.None);

        Assert.Equal(3, reranker.LastDiagnostics!.OptionsWithProbability);
        Assert.True(reranker.LastRerankDiscriminated());
    }

    [Fact]
    public async Task An_empty_candidate_list_calls_nothing()
    {
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.First()));
        var reranker = new TypeSafeChoiceReranker(asker);

        var scores = await reranker.RerankAsync("q", [], CancellationToken.None);

        Assert.Empty(scores);
        Assert.Equal(0, asker.CallCount);
    }

    [Fact]
    public async Task A_blank_query_is_rejected_before_any_call()
    {
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.First()));
        var reranker = new TypeSafeChoiceReranker(asker);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => reranker.RerankAsync("   ", ["a"], CancellationToken.None));

        Assert.Equal(0, asker.CallCount);
    }

    [Fact]
    public async Task A_failing_asker_propagates_rather_than_returning_a_flat_distribution()
    {
        // Silently returning zeros would look like a successful rerank that found nothing relevant.
        // The cross-bank pipeline is what turns this into a reported fallback.
        var reranker = new TypeSafeChoiceReranker(new FailingAsker());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reranker.RerankAsync("q", ["a"], CancellationToken.None));
    }

    [Fact]
    public async Task A_null_passage_is_treated_as_empty_rather_than_throwing()
    {
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.First()));
        var reranker = new TypeSafeChoiceReranker(asker);

        var scores = await reranker.RerankAsync("q", [null!, "real"], CancellationToken.None);
        Assert.Equal(2, scores.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_candidate_limit_is_rejected(int limit)
    {
        Assert.Throws<Hindsight.Sdk.Errors.HindsightConfigurationException>(
            () => new TypeSafeChoiceReranker(new FailingAsker(), new TypeSafeRerankerOptions
            {
                MaxCandidatesPerQuestion = limit,
            }));
    }

    [Fact]
    public void A_limit_above_the_service_maximum_is_rejected()
    {
        // 255 is a service limit, not a preference. Exceeding it would fail remotely rather than
        // locally, and the error would name the service rather than the option.
        var exception = Assert.Throws<Hindsight.Sdk.Errors.HindsightConfigurationException>(
            () => new TypeSafeChoiceReranker(new FailingAsker(), new TypeSafeRerankerOptions
            {
                MaxCandidatesPerQuestion = TypeSafeChoiceReranker.MaxChoiceOptions + 1,
            }));

        Assert.Contains("255", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var asker = new StubAsker((_, _, options) => Distribute(options.Keys.First()));
        var reranker = new TypeSafeChoiceReranker(asker);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reranker.RerankAsync("q", ["a"], cts.Token));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new TypeSafeChoiceReranker(null!));
    }
}
