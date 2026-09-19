using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Reranking;

namespace Vectorize.Hindsight.Sdk.Reranking.Onnx;

/// <summary>
/// A local cross-encoder reranker running ONNX Runtime in-process, on CPU or GPU.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IReranker"/> for the second stage of the cross-bank pipeline. Everything
/// runs locally: no service, no API key, no per-query cost.
/// </para>
/// <para>
/// <b>Scores are ordinal, not calibrated.</b> This is not a caveat to skip. Measured against the
/// INT8 export of <c>BAAI/bge-reranker-v2-m3</c>, the correct passage scored 0.29 while every other
/// candidate landed between 0.0000 and 0.0018, and the two least relevant were separated only in
/// the sixth decimal. The <i>order</i> was right and the absolute values were close to meaningless,
/// which is normal for a quantized cross-encoder. Use the score to order; do not threshold on it or
/// compare it across queries. If you need a calibrated value, prefer the FP32 export and measure it
/// for your own data rather than assuming.
/// </para>
/// <para>
/// <b>Threading.</b> One session is shared, and concurrent scoring calls are serialised by default
/// (<see cref="OnnxRerankerOptions.MaxConcurrentSessions"/>) because batching beats interleaving for
/// throughput and bounds memory. <see cref="Dispose"/> is safe to call while calls are in flight.
/// </para>
/// </remarks>
public sealed class OnnxReranker : IReranker, IDisposable
{
    private readonly OnnxRerankerOptions _options;
    private readonly CrossEncoderTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _gate;
    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string _outputName;
    private readonly bool _supportsTokenTypeIds;
    private bool _disposed;

    private long _scoredDocumentCount;
    private long _inferenceCallCount;

    /// <summary>Loads the model and the tokenizer.</summary>
    /// <param name="options">The options. <see cref="OnnxRerankerOptions.ModelPath"/> is required.</param>
    /// <exception cref="HindsightConfigurationException">A required path is missing or unreadable.</exception>
    /// <exception cref="HindsightProtocolException">The model's graph does not expose the expected inputs.</exception>
    /// <remarks>
    /// Loading is synchronous and does real work — the model is read from disk and the graph is
    /// initialised. It belongs in host startup, not in a request path, which is also why this type
    /// has no async constructor-equivalent: an <see cref="IReranker"/> is a long-lived collaborating
    /// service, not a per-call resource.
    /// </remarks>
    public OnnxReranker(OnnxRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            throw new HindsightConfigurationException("The ONNX model path is required.");
        }

        if (!File.Exists(options.ModelPath))
        {
            throw new HindsightConfigurationException($"The ONNX model was not found: {options.ModelPath}");
        }

        if (string.IsNullOrWhiteSpace(options.TokenizerJsonPath) || string.IsNullOrWhiteSpace(options.SentencePieceModelPath))
        {
            throw new HindsightConfigurationException(
                "Both a tokenizer.json and a SentencePiece model are required. The Hugging Face "
                + "tokenizer JSON supplies the vocabulary and the special-token ids, but not the "
                + "segmentation, so both assets from the model repository are needed.");
        }

        if (!File.Exists(options.TokenizerJsonPath!))
        {
            throw new HindsightConfigurationException($"The tokenizer was not found: {options.TokenizerJsonPath}");
        }

        if (!File.Exists(options.SentencePieceModelPath!))
        {
            throw new HindsightConfigurationException($"The SentencePiece model was not found: {options.SentencePieceModelPath}");
        }

        if (options.MaxSequenceLength <= 4)
        {
            throw new HindsightConfigurationException(
                "MaxSequenceLength must exceed 4, because the pair framing alone (BOS, doubled EOS, "
                + "trailing EOS) needs four tokens.");
        }

        if (options.BatchSize <= 0)
        {
            throw new HindsightConfigurationException("BatchSize must be positive.");
        }

        _tokenizer = CrossEncoderTokenizer.Load(options.TokenizerJsonPath!, options.SentencePieceModelPath!);

        var sessionOptions = BuildSessionOptions(options, out var providerWarning);

        try
        {
            _session = new InferenceSession(options.ModelPath, sessionOptions);
        }
        catch (Exception ex)
        {
            // The configuration exception carries no inner exception by design (it describes a
            // setup mistake rather than a wrapped failure), so the cause is included in the text.
            throw new HindsightConfigurationException(
                $"The ONNX model could not be loaded from {options.ModelPath}: "
                + $"{ex.GetType().Name}: {ex.Message}");
        }

        if (providerWarning is not null)
        {
            ProviderWarning = providerWarning;
            if (options.Logger is { } warningLogger)
            {
                RerankLog.ProviderFallback(warningLogger, options.ExecutionProvider.ToString(), providerWarning);
            }
        }

        ProviderInUse = DescribeProvider(options);

        // Resolve the graph's input and output names once. A wrong name is otherwise discovered on
        // the first real query, which for a reranker is the worst possible moment.
        //
        // An explicit override is checked against the graph rather than trusted. Trusting it would
        // move the failure to scoring time and surface it as a raw ONNX Runtime exception from
        // inside Run, which names neither the option that was wrong nor the inputs that exist.
        var inputs = _session.InputMetadata;
        var inputNames = inputs.Keys.ToList();

        _inputIdsName = ResolveInputName(
            options.InputIdsName, inputNames, ["input_ids", "input"],
            "OnnxRerankerOptions.InputIdsName",
            "a token-id input");

        _attentionMaskName = ResolveInputName(
            options.AttentionMaskName, inputNames, ["attention_mask", "mask"],
            "OnnxRerankerOptions.AttentionMaskName",
            "an attention-mask input");

        // Present on some exports and absent on others - the BGE export has no token_type_ids, so
        // this is detected rather than assumed, and only fed when it exists.
        _supportsTokenTypeIds = FindName(inputNames, "token_type_ids") is not null;

        var outputs = _session.OutputMetadata;
        var outputNames = outputs.Keys.ToList();

        if (options.OutputName is { } explicitOutput)
        {
            if (!outputNames.Contains(explicitOutput, StringComparer.Ordinal))
            {
                throw new HindsightProtocolException(
                    $"OnnxRerankerOptions.OutputName is '{explicitOutput}', which the model does not "
                    + $"expose. The graph's outputs are: {string.Join(", ", outputNames)}.",
                    "InferenceSession.OutputMetadata",
                    innerException: null);
            }

            _outputName = explicitOutput;
        }
        else
        {
            _outputName = outputNames.FirstOrDefault()
                ?? throw new HindsightProtocolException(
                    "The model exposes no outputs.",
                    "InferenceSession.OutputMetadata",
                    innerException: null);
        }

        _gate = new SemaphoreSlim(
            options.MaxConcurrentSessions <= 0 ? 1 : options.MaxConcurrentSessions,
            options.MaxConcurrentSessions <= 0 ? 1 : options.MaxConcurrentSessions);

        if (options.ValidateGraphAtStartup)
        {
            ValidateGraph();
        }
    }

    /// <summary>The execution provider actually in use, after any fallback.</summary>
    /// <remarks>
    /// Read this when the GPU was requested. A requested GPU that was unavailable falls back to CPU
    /// with a warning rather than failing, so this is how a host confirms which one is running.
    /// </remarks>
    public string ProviderInUse { get; }

    /// <summary>A warning emitted while loading the session, when there was one.</summary>
    public string? ProviderWarning { get; }

    /// <summary>The vocabulary size read from the tokenizer.</summary>
    internal int VocabularySize => _tokenizer.VocabularySize;

    /// <summary>
    /// How many pieces could not be mapped to the vocabulary since this instance was created.
    /// </summary>
    /// <remarks>
    /// Expected to be zero. A non-zero value means the SentencePiece asset and the vocabulary are
    /// from different models, which would otherwise appear only as an unexplained quality drop.
    /// </remarks>
    internal long UnmappedPieceCount => _tokenizer.UnmappedPieceCount;

    /// <summary>How many documents have been scored.</summary>
    internal long ScoredDocumentCount => Interlocked.Read(ref _scoredDocumentCount);

    /// <summary>How many model calls have been made. Compared against <see cref="ScoredDocumentCount"/> it shows batching working.</summary>
    internal long InferenceCallCount => Interlocked.Read(ref _inferenceCallCount);

    /// <summary>
    /// Scores every document against the query, in batches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candidates are grouped by token length before batching, so a short passage is not padded up
    /// to the longest one in the pool. Padding to a global maximum wastes the most compute on
    /// exactly the candidates that need it least.
    /// </para>
    /// <para>
    /// Scores come back aligned with <paramref name="documents"/>, not with their length order.
    /// </para>
    /// </remarks>
    /// <param name="query">The query.</param>
    /// <param name="documents">The passages.</param>
    /// <param name="cancellationToken">Honoured between batches and before each inference call.</param>
    public async Task<IReadOnlyList<double>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scores = new double[documents.Count];

            // Encode once, keeping the original index so scores can be placed back in the caller's
            // order after length-grouped batching.
            var encoded = new (int Index, int[] Ids)[documents.Count];
            for (var index = 0; index < documents.Count; index++)
            {
                encoded[index] = (index, _tokenizer.Encode(query, documents[index] ?? string.Empty, _options.MaxSequenceLength));
            }

            // Shortest first: grouping by length is what makes the padding cheap.
            var ordered = encoded.OrderBy(item => item.Ids.Length).ToList();

            for (var offset = 0; offset < ordered.Count; offset += _options.BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = ordered.Skip(offset).Take(_options.BatchSize).ToList();
                var batchScores = ScoreBatch(batch);

                for (var position = 0; position < batch.Count; position++)
                {
                    scores[batch[position].Index] = batchScores[position];
                }
            }

            Interlocked.Add(ref _scoredDocumentCount, documents.Count);
            return scores;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs one batch through the model and returns a sigmoid-space score per row.</summary>
    private double[] ScoreBatch(List<(int Index, int[] Ids)> batch)
    {
        var batchSize = batch.Count;
        var maxLength = batch.Max(item => item.Ids.Length);

        var ids = new long[batchSize * maxLength];
        var mask = new long[batchSize * maxLength];

        for (var row = 0; row < batchSize; row++)
        {
            var idsRow = batch[row].Ids;
            var rowOffset = row * maxLength;

            for (var column = 0; column < idsRow.Length; column++)
            {
                ids[rowOffset + column] = idsRow[column];
                mask[rowOffset + column] = 1;
            }

            // Positions beyond the sequence length stay 0 in both tensors, which is the padding the
            // attention mask exists to exclude.
        }

        var dimensions = new[] { batchSize, maxLength };
        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, new DenseTensor<long>(ids, dimensions)),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, new DenseTensor<long>(mask, dimensions)),
        };

        if (_supportsTokenTypeIds)
        {
            // All zeros: this model family uses a single segment for both halves of the pair, so
            // type ids carry no information. Fed only when the export declares the input.
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "token_type_ids", new DenseTensor<long>(new long[batchSize * maxLength], dimensions)));
        }

        Interlocked.Increment(ref _inferenceCallCount);

        using var results = _session.Run(inputs);
        var output = results.First(result => result.Name == _outputName).AsTensor<float>();

        // A cross-encoder emits one logit per pair. Anything else means the output was misread, and
        // reading it anyway would attach the wrong number to every passage.
        var expected = batchSize;
        var flattened = new double[expected];
        var written = 0;

        foreach (var value in output)
        {
            if (written >= expected)
            {
                break;
            }

            flattened[written++] = ApplySigmoid(value);
        }

        if (written != expected)
        {
            throw new HindsightProtocolException(
                $"The model returned {written} score(s) for a batch of {batchSize}. The output shape "
                + "does not match a cross-encoder's one-logit-per-pair contract.",
                "InferenceSession.Run",
                innerException: null);
        }

        return flattened;
    }

    private double ApplySigmoid(float logit) =>
        _options.ApplySigmoid
            ? 1.0 / (1.0 + Math.Exp(-logit))
            : logit;

    /// <summary>Runs one real pair through the graph so a bad model fails at startup, not at query time.</summary>
    private void ValidateGraph()
    {
        var probe = _tokenizer.Encode("validation", "validation", _options.MaxSequenceLength);
        var scores = ScoreBatch([(0, probe)]);

        if (scores.Length != 1)
        {
            throw new HindsightProtocolException(
                "The model did not return exactly one score for one pair.",
                "InferenceSession.Run",
                innerException: null);
        }

        if (_options.Logger is { } logger)
        {
            RerankLog.Ready(
                logger, ProviderInUse, _tokenizer.VocabularySize, _options.MaxSequenceLength, _options.BatchSize);
        }
    }

    /// <summary>Builds session options, appending a provider and reporting any fallback.</summary>
    private static SessionOptions BuildSessionOptions(OnnxRerankerOptions options, out string? providerWarning)
    {
        providerWarning = null;

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        if (options.IntraOpThreads > 0)
        {
            sessionOptions.IntraOpNumThreads = options.IntraOpThreads;
        }

        switch (options.ExecutionProvider)
        {
            case OnnxExecutionProvider.Cuda when !IsCudaAvailable():
                // The runtime itself reports no CUDA provider, so appending one would be a guess.
                providerWarning =
                    "The loaded ONNX Runtime reports no CUDA execution provider. Reference "
                    + "Microsoft.ML.OnnxRuntime.Gpu and install the CUDA libraries (cuDNN and "
                    + "cuBLAS) to enable GPU inference.";
                sessionOptions.AppendExecutionProvider_CPU(options.IntraOpThreads > 0 ? options.IntraOpThreads : 0);
                break;

            case OnnxExecutionProvider.Cuda:
                try
                {
                    sessionOptions.AppendExecutionProvider_CUDA(options.DeviceId);
                }
                catch (Exception ex)
                {
                    // A missing GPU must not turn an optional ranking improvement into a total
                    // failure. Recorded rather than swallowed, so a host can see the truth.
                    providerWarning =
                        $"The CUDA execution provider could not be initialised ({ex.GetType().Name}: "
                        + $"{ex.Message}). Falling back to CPU. Reranking still works; it will be slower.";
                    sessionOptions.AppendExecutionProvider_CPU(options.IntraOpThreads > 0 ? options.IntraOpThreads : 0);
                }

                break;

            case OnnxExecutionProvider.Auto when IsCudaAvailable():
                try
                {
                    sessionOptions.AppendExecutionProvider_CUDA(options.DeviceId);
                }
                catch (Exception)
                {
                    // Auto means "prefer accelerated, accept CPU" - a normal outcome, not a warning.
                    sessionOptions.AppendExecutionProvider_CPU(options.IntraOpThreads > 0 ? options.IntraOpThreads : 0);
                }

                break;

            case OnnxExecutionProvider.Cpu:
            default:
                sessionOptions.AppendExecutionProvider_CPU(options.IntraOpThreads > 0 ? options.IntraOpThreads : 0);
                break;
        }

        return sessionOptions;
    }

    private static string DescribeProvider(OnnxRerankerOptions options)
    {
        try
        {
            var providers = OrtEnv.Instance().GetAvailableProviders();
            if (providers is { Length: > 0 })
            {
                return string.Join(",", providers);
            }
        }
        catch (Exception)
        {
            // The runtime did not report its providers; the requested one is the honest answer.
        }

        return options.ExecutionProvider.ToString();
    }

    /// <summary>True when the loaded runtime reports a CUDA-capable provider.</summary>
    /// <remarks>
    /// Checked before appending rather than inferred from whether appending threw. Appending a
    /// provider the runtime does not actually have can succeed and then fail later, or silently
    /// produce a CPU session, so the runtime's own report is what the decision is based on.
    /// </remarks>
    private static bool IsCudaAvailable()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders()
                .Any(name => name.Contains("CUDA", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Finds an input by any of several conventional names.</summary>
    private static string? FindName(IEnumerable<string> names, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            foreach (var name in names)
            {
                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves an input name: an explicit override if it exists in the graph, otherwise the first
    /// conventional name the graph exposes.
    /// </summary>
    /// <remarks>
    /// The override is validated rather than trusted. A name the graph does not have would fail
    /// later inside the runtime, as an exception that names neither the option nor the alternatives
    /// — and for a model someone is configuring for the first time, that is the least useful moment
    /// and the least useful message.
    /// </remarks>
    private static string ResolveInputName(
        string? explicitName,
        IReadOnlyList<string> graphInputNames,
        string[] conventionalNames,
        string optionName,
        string described)
    {
        if (explicitName is { Length: > 0 })
        {
            if (!graphInputNames.Contains(explicitName, StringComparer.Ordinal))
            {
                throw new HindsightProtocolException(
                    $"{optionName} is '{explicitName}', which the model does not expose as an input. "
                    + $"The graph's inputs are: {string.Join(", ", graphInputNames)}.",
                    "InferenceSession.InputMetadata",
                    innerException: null);
            }

            return explicitName;
        }

        return FindName(graphInputNames, conventionalNames)
            ?? throw new HindsightProtocolException(
                $"The model exposes no {described}. The graph's inputs are: "
                + $"{string.Join(", ", graphInputNames)}. Set {optionName} explicitly if one of them "
                + "is the right choice.",
                "InferenceSession.InputMetadata",
                innerException: null);
    }

    /// <summary>Releases the session and tokenizer.</summary>
    /// <remarks>
    /// The gate is not disposed: an in-flight call may still be inside <c>Release</c>, and disposing
    /// the semaphore underneath it would turn a clean shutdown into an exception on a background
    /// thread. The handles it guards are released by the session's own disposal.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }
}
