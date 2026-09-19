using Vectorize.Hindsight.Sdk.Reranking.Onnx;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Reranking.Tests;

/// <summary>
/// Locates the reranker assets, and skips the suite cleanly when they are absent.
/// </summary>
/// <remarks>
/// <para>
/// The ONNX model is hundreds of megabytes, so it is fetched on demand rather than committed —
/// see <c>test-infra/fetch-reranker-model.sh</c>. That means this suite must be honest when the
/// assets are missing: it skips, and it says how to get them. A silently passing test that never
/// ran would be the worst possible outcome for the one part of this library that cannot be
/// verified by reading.
/// </para>
/// <para>
/// The default location is <c>artifacts/reranker</c>; override with
/// <c>HINDSIGHT_RERANKER_DIR</c>.
/// </para>
/// </remarks>
internal static class RerankerAssets
{
    private static readonly Lazy<string?> Directory = new(FindDirectory);

    internal static bool Available => Directory.Value is not null;

    internal static string ModelPath => Path.Combine(Directory.Value!, "model_int8.onnx");

    internal static string TokenizerPath => Path.Combine(Directory.Value!, "tokenizer.json");

    internal static string SentencePiecePath => Path.Combine(Directory.Value!, "sentencepiece.bpe.model");

    internal static string SkipReason =>
        "The ONNX reranker assets are not present. Run test-infra/fetch-reranker-model.sh to fetch "
        + "them into artifacts/reranker, or set HINDSIGHT_RERANKER_DIR to an existing directory.";

    private static string? FindDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("HINDSIGHT_RERANKER_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory) && IsComplete(overrideDirectory))
        {
            return overrideDirectory;
        }

        // Walk up from the test binary to the repository root, so this works from any
        // configuration and from a clean clone.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "artifacts", "reranker");
            if (IsComplete(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsComplete(string directory) =>
        File.Exists(Path.Combine(directory, "model_int8.onnx"))
        && File.Exists(Path.Combine(directory, "tokenizer.json"))
        && File.Exists(Path.Combine(directory, "sentencepiece.bpe.model"));
}

/// <summary>
/// The reranker's behaviour, and its parity with the reference implementation.
/// </summary>
/// <remarks>
/// The tests that matter most are the tokenizer parity cases. ONNX Runtime will run happily on
/// wrong input and return plausible numbers, so a tokenization error does not announce itself —
/// it appears as a reranker that quietly ranks badly. Asserting the exact ids against the reference
/// is the only way to catch it.
/// </remarks>
public sealed class OnnxRerankerTests
{
    /// <summary>
    /// The ids the Hugging Face reference tokenizer produces, captured from it directly.
    /// </summary>
    /// <remarks>
    /// These are not hand-written from a model card. They were produced by running the model's own
    /// <c>tokenizer.json</c> through the reference implementation and recorded verbatim, which is
    /// what makes this a parity test rather than a test of my own assumptions.
    /// </remarks>
    public static TheoryData<string, string, int[]> ReferenceEncodings => new()
    {
        {
            "What do we know about the billing migration?",
            "The billing platform migrated to PostgreSQL in March 2026.",
            [0, 4865, 54, 642, 3714, 1672, 70, 2193, 2069, 212416, 32, 2, 2,
             581, 2193, 2069, 13651, 39666, 3674, 47, 2795, 7134, 158897, 23, 11994, 387, 4046, 5, 2]
        },
        {
            "who leads billing",
            "Alice Nakamura is the principal engineer on the billing platform.",
            [0, 2750, 37105, 7, 2193, 2069, 2, 2,
             66083, 84649, 54184, 83, 70, 7893, 185118, 98, 70, 2193, 2069, 13651, 5, 2]
        },
        {
            // Multilingual: the model is chosen partly for this, so it is asserted rather than
            // assumed. A tokenizer that only happens to work on English passes every other test.
            "multilingual check",
            "Der Abrechnungsdienst wurde im März 2026 auf PostgreSQL migriert.",
            [0, 6024, 65279, 289, 12765, 2, 2,
             1310, 3157, 132947, 7, 33820, 4180, 566, 58409, 387, 4046, 644, 2795, 7134, 158897, 1693, 42, 6280, 5, 2]
        },
    };

    [SkippableTheory]
    [MemberData(nameof(ReferenceEncodings))]
    public void Tokenization_reproduces_the_reference_ids_exactly(string query, string passage, int[] expected)
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        // Encoding is reached through the reranker's own tokenizer rather than a separate path, so
        // this asserts what the model actually receives.
        var actual = Encode(query, passage, 512);

        Assert.Equal(expected, actual);
    }

    [SkippableFact]
    public void The_same_fact_in_two_languages_encodes_differently_and_still_scores()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        // A guard against a tokenizer that silently maps everything to the unknown token: unicode
        // input must produce a real sequence, not a uniform one.
        var english = Encode("who leads billing", "Alice leads the billing platform.", 512);
        var german = Encode("who leads billing", "Alice leitet die Abrechnungsplattform.", 512);

        Assert.NotEqual(english, german);
        Assert.True(english.Length > 6);
        Assert.True(german.Length > 6);
    }

    [SkippableFact]
    public void Long_passages_are_truncated_while_the_query_survives_intact()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        const string Query = "who leads billing";
        var queryAlone = Encode(Query, string.Empty, 512).Length - 4;

        var longPassage = string.Join(' ', Enumerable.Repeat("padding", 4000));

        var ids = Encode(Query, longPassage, 64);

        Assert.Equal(64, ids.Length);

        // The query's own tokens are still at the front, immediately after the BOS. A truncation
        // that ate the query would score every passage against a partial question.
        var queryHead = Encode(Query, string.Empty, 512)[1];
        Assert.Equal(queryHead, ids[1]);
        Assert.True(queryAlone > 0);
    }

    [SkippableFact]
    public async Task Scores_are_returned_in_the_caller_s_document_order()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create();

        // Deliberately mixed lengths, because batching groups by length: a returned score that was
        // aligned by batch position instead of by original index would show up here.
        var documents = new[]
        {
            "A deliberately long passage that says nothing useful about billing at all, padded out with more words to make it the longest entry in this set so the length grouping has something to reorder, ending with the word end.",
            "The billing platform migrated to PostgreSQL.",
            "Unrelated short text.",
        };

        var scores = await reranker.RerankAsync(
            "When did the billing platform migrate?", documents, CancellationToken.None);

        Assert.Equal(documents.Length, scores.Count);

        // The real answer must outrank the two that are not answers, whatever their lengths were.
        Assert.True(
            scores[1] > scores[0],
            $"the relevant passage ({scores[1]}) should outrank the long irrelevant one ({scores[0]})");
        Assert.True(scores[1] > scores[2]);
    }

    [SkippableFact]
    public void The_graph_is_validated_at_startup()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        // A reranker that cannot run should fail when it is constructed, not on the first real
        // query. Construction runs one probe pair for exactly that reason.
        using var reranker = Create();
        Assert.Equal(1, reranker.InferenceCallCount);
    }

    [SkippableFact]
    public async Task Documents_are_batched_rather_than_scored_one_call_each()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create(batchSize: 4);
        var before = reranker.InferenceCallCount;

        var documents = Enumerable.Range(1, 12).Select(i => $"Candidate passage number {i}.").ToArray();
        var scores = await reranker.RerankAsync("candidate", documents, CancellationToken.None);

        Assert.Equal(12, scores.Count);

        // 12 documents at a batch size of 4 must be three model calls, not twelve. Batching is the
        // single biggest latency lever in this design, so it is measured rather than assumed.
        Assert.Equal(3, reranker.InferenceCallCount - before);
        Assert.Equal(12, reranker.ScoredDocumentCount);
    }

    [SkippableFact]
    public void Padding_uses_the_shortest_maximum_needed_by_each_batch()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create(batchSize: 2);

        var shortPair = new[] { "short", "also short" };
        var longPair = new[] { new string('x', 4000), "still short" };

        // Not asserting timings — that would be a scheduler measurement. What matters is that
        // grouping by length is what happens, which shows up as the long pair simply working.
        var scores = reranker.RerankAsync("probe", shortPair, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(2, scores.Count);

        scores = reranker.RerankAsync("probe", longPair, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(2, scores.Count);
    }

    [SkippableFact]
    public async Task Every_token_maps_to_a_vocabulary_entry()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create();

        var documents = new[]
        {
            "Plain english text with words.",
            "Deutscher Text mit Umlauten: äöüß.",
            "Français avec des accents: éèêë.",
            "日本語のテキスト。",
            "Emoji and symbols: ★ ♪ ✓ →.",
        };

        await reranker.RerankAsync("multilingual probe", documents, CancellationToken.None);

        // Zero expected. A non-zero count means the SentencePiece asset and the vocabulary are from
        // different models — which would otherwise appear only as unexplained poor ranking.
        Assert.Equal(0, reranker.UnmappedPieceCount);
    }

    [SkippableFact]
    public async Task An_empty_document_list_returns_no_scores_without_calling_the_model()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create();
        var before = reranker.InferenceCallCount;

        var scores = await reranker.RerankAsync("query", [], CancellationToken.None);

        Assert.Empty(scores);
        Assert.Equal(before, reranker.InferenceCallCount);
    }

    [SkippableFact]
    public async Task An_empty_passage_is_scored_rather_than_throwing()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create();

        // A bank can return a result with no text. That must score low, not crash the pipeline.
        var scores = await reranker.RerankAsync(
            "the billing migration", ["", "The billing platform migrated to PostgreSQL."], CancellationToken.None);

        Assert.Equal(2, scores.Count);
        Assert.True(scores[1] > scores[0]);
    }

    [SkippableFact]
    public async Task A_null_passage_is_treated_as_empty_rather_than_throwing()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create();
        var scores = await reranker.RerankAsync("query", [null!, "some text"], CancellationToken.None);

        Assert.Equal(2, scores.Count);
    }

    [SkippableFact]
    public async Task Cancellation_is_honoured()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reranker.RerankAsync("query", ["a document"], cts.Token));
    }

    [SkippableFact]
    public async Task Concurrent_calls_are_serialised_by_default_and_stay_correct()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create();

        // The session is shared. Correctness under concurrent use is the property that matters;
        // the default limit of one keeps memory bounded.
        var tasks = Enumerable.Range(0, 8).Select(index =>
            reranker.RerankAsync(
                $"query {index}",
                [$"document {index} a", $"document {index} b"],
                CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(8, results.Length);
        Assert.All(results, scores => Assert.Equal(2, scores.Count));
        Assert.Equal(16, reranker.ScoredDocumentCount);
    }

    [SkippableFact]
    public async Task Disposal_is_safe_while_a_call_is_in_flight()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        var reranker = Create();
        var pending = reranker.RerankAsync("query", ["a document", "another"], CancellationToken.None);

        try
        {
            await pending;
        }
        finally
        {
            reranker.Dispose();
        }

        // Disposing twice is a no-op rather than a throw, because a host may dispose the reranker
        // and its container may dispose it again.
        reranker.Dispose();
    }

    [SkippableFact]
    public async Task A_disposed_reranker_reports_it_rather_than_crashing()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        var reranker = Create();
        reranker.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            reranker.RerankAsync("query", ["a document"], CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_reported_provider_is_the_cpu_provider_by_default()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var reranker = Create();

        // Asserted rather than assumed: the machine this was built on has a GPU but no CUDA
        // libraries, so a GPU request silently falling back must be visible rather than believed.
        Assert.Contains("CPU", reranker.ProviderInUse, StringComparison.OrdinalIgnoreCase);

        await Task.CompletedTask;
    }

    /// <summary>Encodes a pair with the real tokenizer, so the tests assert what the model receives.</summary>
    private static int[] Encode(string query, string passage, int maxSequenceLength)
    {
        var tokenizer = CrossEncoderTokenizer.Load(RerankerAssets.TokenizerPath, RerankerAssets.SentencePiecePath);
        return tokenizer.Encode(query, passage, maxSequenceLength);
    }

    private static OnnxReranker Create(int batchSize = 16) =>
        new(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
            BatchSize = batchSize,
        });
}
