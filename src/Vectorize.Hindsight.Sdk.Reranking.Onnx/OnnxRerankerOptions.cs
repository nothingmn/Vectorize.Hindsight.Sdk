using Microsoft.ML.OnnxRuntime;

namespace Vectorize.Hindsight.Sdk.Reranking.Onnx;

/// <summary>Where to run the model.</summary>
public enum OnnxExecutionProvider
{
    /// <summary>CPU. The default, and the right starting point for a few dozen candidates.</summary>
    Cpu = 0,

    /// <summary>
    /// The GPU, via the CUDA provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires the CUDA native libraries (<c>onnxruntime</c>'s CUDA provider, cuDNN and cuBLAS)
    /// to be present on the machine. Referencing <c>Microsoft.ML.OnnxRuntime.Gpu</c> instead of the
    /// CPU package supplies the provider, but the CUDA libraries are a separate install either way.
    /// </para>
    /// <para>
    /// <b>An unavailable GPU falls back to CPU with a warning rather than failing.</b> A reranker
    /// that refuses to start because a GPU is missing turns an optional ranking improvement into a
    /// total failure, which is not a trade worth making. Check
    /// <see cref="OnnxReranker.ProviderInUse"/> to see what actually happened.
    /// </para>
    /// </remarks>
    Cuda = 1,

    /// <summary>Let ONNX Runtime choose, preferring an accelerated provider when one is available.</summary>
    Auto = 2,
}

/// <summary>How to load a local ONNX reranker.</summary>
/// <remarks>
/// Every setting here has a working default except the model path and the tokenizer assets, which
/// cannot be invented. See <see cref="OnnxRerankerOptions.ModelPath"/>.
/// </remarks>
public sealed class OnnxRerankerOptions
{
    /// <summary>The ONNX model file. Required.</summary>
    /// <remarks>
    /// A cross-encoder that scores a query and a passage together. Tested against the INT8 and FP32
    /// exports of <c>BAAI/bge-reranker-v2-m3</c>.
    /// </remarks>
    public required string ModelPath { get; init; }

    /// <summary>
    /// The tokenizer asset for the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Point this at the model's <c>tokenizer.json</c> (Hugging Face format). The vocabulary and the
    /// special-token ids are read from it, so the ids this library produces are the model's ids.
    /// </para>
    /// <para>
    /// Required when <see cref="SentencePieceModelPath"/> is not supplied, because the
    /// Hugging Face tokenizer JSON does not carry the segmentation model. With an XLM-R derived
    /// reranker both are needed and both come from the same repository.
    /// </para>
    /// </remarks>
    public string? TokenizerJsonPath { get; init; }

    /// <summary>
    /// The SentencePiece model used for segmentation.
    /// </summary>
    /// <remarks>
    /// For an XLM-R derived reranker this is <c>sentencepiece.bpe.model</c> from the model
    /// repository. See the note on <see cref="OnnxReranker"/> about why both assets are needed and
    /// why the ids are remapped.
    /// </remarks>
    public string? SentencePieceModelPath { get; init; }

    /// <summary>
    /// The longest sequence to send, in tokens. Longer sequences are truncated.
    /// </summary>
    /// <remarks>
    /// 512 by default, which is the model's own limit and matches the documented starting point.
    /// Truncation is applied to the passage, keeping the query intact, because a query cut in half
    /// scores every passage wrongly while a passage cut in half still carries most of its meaning.
    /// </remarks>
    public int MaxSequenceLength { get; init; } = 512;

    /// <summary>How many query/passage pairs to score in one model call.</summary>
    /// <remarks>
    /// <para>
    /// Batching matters more for latency than almost any other setting, because a cross-encoder
    /// evaluates each pair separately and a batch amortises the model's fixed per-call cost.
    /// </para>
    /// <para>
    /// Candidates are grouped by length before batching, so a short passage is not padded up to
    /// the longest one in the whole pool. That is why this is a batch size rather than a padding
    /// setting.
    /// </para>
    /// </remarks>
    public int BatchSize { get; init; } = 16;

    /// <summary>Where to run inference. CPU by default.</summary>
    public OnnxExecutionProvider ExecutionProvider { get; init; } = OnnxExecutionProvider.Cpu;

    /// <summary>The CUDA device id, when <see cref="ExecutionProvider"/> is CUDA.</summary>
    public int DeviceId { get; init; }

    /// <summary>How many intra-op threads to use on the CPU. Zero lets ONNX Runtime decide.</summary>
    public int IntraOpThreads { get; init; }

    /// <summary>
    /// Whether to apply a sigmoid to the model's raw output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True by default: a BGE cross-encoder emits a single logit, and a sigmoid maps it to a 0..1
    /// relevance score as the model card describes. Set false to keep the raw logit, which is what
    /// you want if you intend to combine it with other terms — a monotonic transform does not
    /// change the ordering, and the raw value stays linear and additive.
    /// </para>
    /// <para>
    /// Either way the score is ordinal, not calibrated. See <see cref="IReranker"/>.
    /// </para>
    /// </remarks>
    public bool ApplySigmoid { get; init; } = true;

    /// <summary>
    /// The name of the model's input for token ids, when auto-detection does not find it.
    /// </summary>
    /// <remarks>
    /// The graph's own input names are used by default — verified against the BGE export, which
    /// names them <c>input_ids</c> and <c>attention_mask</c> and, notably, has <b>no</b>
    /// <c>token_type_ids</c>. Override only for an export that names them differently.
    /// </remarks>
    public string? InputIdsName { get; init; }

    /// <summary>The name of the model's attention-mask input, when auto-detection does not find it.</summary>
    public string? AttentionMaskName { get; init; }

    /// <summary>The name of the model's single-score output.</summary>
    /// <remarks>
    /// The first output is used by default. A cross-encoder reranker has exactly one logit per
    /// pair, and a mismatch is detected and reported rather than silently misread.
    /// </remarks>
    public string? OutputName { get; init; }

    /// <summary>
    /// How many rerank calls may use the session at once.
    /// </summary>
    /// <remarks>
    /// One by default. An ONNX Runtime session is thread-safe but its throughput is best when calls
    /// are batched rather than interleaved, and serialising by default also bounds memory: a large
    /// intra-op thread pool times several concurrent calls can exhaust a machine. Raise it only if
    /// measurements say the session is idle.
    /// </remarks>
    public int MaxConcurrentSessions { get; init; } = 1;

    /// <summary>
    /// Whether to validate the model's inputs against the configured names at construction.
    /// </summary>
    /// <remarks>
    /// On by default. A wrong input name is otherwise only discovered on the first scoring call,
    /// which for a reranker means the first real query.
    /// </remarks>
    public bool ValidateGraphAtStartup { get; init; } = true;

    /// <summary>Optional diagnostics sink for load and scoring notes.</summary>
    public Microsoft.Extensions.Logging.ILogger? Logger { get; init; }
}
