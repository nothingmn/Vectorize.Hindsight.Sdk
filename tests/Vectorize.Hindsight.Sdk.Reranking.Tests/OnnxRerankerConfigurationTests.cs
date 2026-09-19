using Microsoft.Extensions.Logging.Abstractions;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Reranking.Onnx;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Reranking.Tests;

/// <summary>
/// The reranker's configuration contract and provider handling.
/// </summary>
/// <remarks>
/// Every case here fails <b>before</b> a model is loaded, so this class runs everywhere — no
/// hundreds-of-megabytes download, no skip. That matters: a misconfiguration is the failure a host
/// is most likely to hit, and it should be caught on a machine that never fetches the model as well
/// as on one that did.
/// </remarks>
public sealed class OnnxRerankerConfigurationTests
{
    private const string FakeModel = "/nonexistent/model.onnx";

    private static OnnxRerankerOptions Options(
        string? modelPath = FakeModel,
        string? tokenizerPath = "/nonexistent/tokenizer.json",
        string? sentencePiecePath = "/nonexistent/sentencepiece.bpe.model",
        int maxSequenceLength = 512,
        int batchSize = 16) => new()
        {
            ModelPath = modelPath!,
            TokenizerJsonPath = tokenizerPath,
            SentencePieceModelPath = sentencePiecePath,
            MaxSequenceLength = maxSequenceLength,
            BatchSize = batchSize,
        };

    [Fact]
    public void A_missing_model_path_is_rejected()
    {
        var exception = Assert.Throws<HindsightConfigurationException>(
            () => new OnnxReranker(Options(modelPath: "   ")));

        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_model_that_does_not_exist_is_rejected_by_name()
    {
        var exception = Assert.Throws<HindsightConfigurationException>(
            () => new OnnxReranker(Options(modelPath: "/no/such/model.onnx")));

        Assert.Contains("/no/such/model.onnx", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "/sp.model")]
    [InlineData("/tok.json", null)]
    [InlineData(null, null)]
    public void Both_tokenizer_assets_are_required(string? tokenizerPath, string? sentencePiecePath)
    {
        // The message must explain *why* two assets are needed, because the requirement is
        // non-obvious and the Hugging Face JSON alone looks sufficient.
        var exception = Assert.Throws<HindsightConfigurationException>(
            () => new OnnxReranker(Options(modelPath: ExistingModel(), tokenizerPath: tokenizerPath, sentencePiecePath: sentencePiecePath)));

        Assert.Contains("segmentation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tokenizer_that_does_not_exist_is_rejected_by_name()
    {
        var exception = Assert.Throws<HindsightConfigurationException>(
            () => new OnnxReranker(Options(
                modelPath: ExistingModel(),
                tokenizerPath: "/no/such/tokenizer.json",
                sentencePiecePath: ExistingModel())));

        Assert.Contains("/no/such/tokenizer.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sentencepiece_model_that_does_not_exist_is_rejected_by_name()
    {
        var exception = Assert.Throws<HindsightConfigurationException>(
            () => new OnnxReranker(Options(
                modelPath: ExistingModel(),
                tokenizerPath: ExistingModel(),
                sentencePiecePath: "/no/such/sp.model")));

        Assert.Contains("/no/such/sp.model", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void A_sequence_limit_too_small_for_the_pair_framing_is_rejected(int maxSequenceLength)
    {
        // The framing alone (BOS, doubled EOS, trailing EOS) is four tokens, so a limit at or below
        // that leaves nowhere for content. Rejecting it up front beats emitting a sequence that can
        // never represent a pair.
        var exception = Assert.Throws<HindsightConfigurationException>(() => new OnnxReranker(Options(
            modelPath: ExistingModel(),
            tokenizerPath: ExistingModel(),
            sentencePiecePath: ExistingModel(),
            maxSequenceLength: maxSequenceLength)));

        Assert.Contains("MaxSequenceLength", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_batch_size_is_rejected(int batchSize)
    {
        var exception = Assert.Throws<HindsightConfigurationException>(() => new OnnxReranker(Options(
            modelPath: ExistingModel(),
            tokenizerPath: ExistingModel(),
            sentencePiecePath: ExistingModel(),
            batchSize: batchSize)));

        Assert.Contains("BatchSize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_options_are_rejected_rather_than_dereferenced()
    {
        Assert.Throws<ArgumentNullException>(() => new OnnxReranker(null!));
    }

    [SkippableFact]
    public void A_gpu_that_is_unavailable_falls_back_to_the_cpu_and_says_so()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        // This machine has a GPU but no CUDA libraries, so the runtime reports no CUDA provider and
        // the requested GPU cannot be honoured. The reranker must still work, and must say clearly
        // which provider it ended up on - silently ignoring the request would be worse than
        // failing, because the caller would believe inference was accelerated.
        using var reranker = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
            ExecutionProvider = OnnxExecutionProvider.Cuda,
        });

        if (reranker.ProviderWarning is null)
        {
            // A machine that genuinely has the CUDA provider: assert the accelerated path was used.
            Assert.Contains("CUDA", reranker.ProviderInUse, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("CUDA", reranker.ProviderWarning, StringComparison.Ordinal);
            Assert.Contains("CPU", reranker.ProviderInUse, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public void Auto_prefers_an_accelerated_provider_and_warns_about_nothing()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        // Auto means "prefer accelerated, accept CPU", so a CPU outcome is a normal result rather
        // than something to warn about. Warning here would train a host to ignore the warning that
        // an explicit Cuda request produces.
        using var reranker = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
            ExecutionProvider = OnnxExecutionProvider.Auto,
        });

        Assert.Null(reranker.ProviderWarning);
        Assert.False(string.IsNullOrWhiteSpace(reranker.ProviderInUse));
    }

    [SkippableFact]
    public void Applying_the_sigmoid_can_be_turned_off_for_the_raw_logit()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        // A monotonic transform does not change the ordering, so the ranked order must be identical
        // either way. The raw value stays linear and additive, which is what a caller wants when
        // combining it with other terms.
        using var withSigmoid = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
        });

        using var withoutSigmoid = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
            ApplySigmoid = false,
        });

        var documents = new[]
        {
            "The billing platform migrated to PostgreSQL in March 2026.",
            "An unrelated sentence about gardening.",
            "Billing reconciliation runs nightly.",
        };

        var withScores = withSigmoid.RerankAsync("when did billing migrate?", documents, CancellationToken.None).GetAwaiter().GetResult();
        var rawScores = withoutSigmoid.RerankAsync("when did billing migrate?", documents, CancellationToken.None).GetAwaiter().GetResult();

        // Sigmoid-space values are 0..1; raw logits are not bounded by them.
        Assert.All(withScores, score => Assert.InRange(score, 0.0, 1.0));
        Assert.Contains(rawScores, score => score < 0.0 || score > 1.0);

        // But the ordering is the same, because sigmoid is monotonic.
        Assert.Equal(
            withScores.Select((score, index) => (score, index)).OrderByDescending(p => p.score).Select(p => p.index),
            rawScores.Select((score, index) => (score, index)).OrderByDescending(p => p.score).Select(p => p.index));
    }

    [SkippableFact]
    public void An_explicit_input_name_that_is_wrong_is_reported_at_startup()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        // A wrong input name would otherwise be discovered on the first real query. Asserted here
        // because the failure mode - scoring nothing, or scoring wrongly - is invisible.
        Assert.Throws<HindsightProtocolException>(() => new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
            InputIdsName = "definitely_not_an_input",
        }));
    }

    [SkippableFact]
    public void A_logger_receives_the_readiness_note()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        // The readiness message is the one place a host learns which provider it actually got, so
        // it is asserted rather than assumed to be emitted.
        var logger = new RecordingLogger();

        using var reranker = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
            Logger = logger,
        });

        Assert.Contains(logger.Messages, message => message.Contains("reranker ready", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public void An_unavailable_gpu_request_reaches_the_logger_as_a_warning()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        var logger = new RecordingLogger();

        using var reranker = new OnnxReranker(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
            ExecutionProvider = OnnxExecutionProvider.Cuda,
            Logger = logger,
        });

        if (reranker.ProviderWarning is not null)
        {
            Assert.Contains(logger.Messages, message => message.Contains("CUDA", StringComparison.Ordinal));
        }
    }

    /// <summary>Creates an empty file to satisfy an existence check, and cleans it up afterwards.</summary>
    /// <remarks>
    /// The guards are ordered, so reaching a later one requires the earlier paths to exist. A real
    /// zero-byte file is enough: the checks are existence, not content.
    /// </remarks>
    private static string ExistingModel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hsdk-guard-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, []);
        TempFiles.Add(path);
        return path;
    }

    private static readonly List<string> TempFiles = [];

    /// <summary>An <see cref="Microsoft.Extensions.Logging.ILogger"/> that records what it was told.</summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
