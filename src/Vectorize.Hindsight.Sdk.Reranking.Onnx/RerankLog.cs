using Microsoft.Extensions.Logging;

namespace Vectorize.Hindsight.Sdk.Reranking.Onnx;

/// <summary>
/// The reranker's log messages, source-generated.
/// </summary>
/// <remarks>
/// <para>
/// Follows the library's convention: every message carries only bounded, non-caller-supplied
/// values — a provider name, counts, a length, a batch size. No message takes a query, a passage,
/// or a caller's identifier as a parameter, which is what keeps redaction structural rather than a
/// discipline someone has to remember. Reranked content is exactly the kind of thing that must
/// never reach a log.
/// </para>
/// <para>
/// Source-generated rather than a plain <c>LogWarning</c> call, which is how the rest of the
/// library logs and what keeps the messages allocation-free when the level is disabled.
/// </para>
/// </remarks>
internal static partial class RerankLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "ONNX reranker ready: provider={Provider} vocab={VocabularySize} maxLength={MaxSequenceLength} batch={BatchSize}.")]
    internal static partial void Ready(
        ILogger logger, string provider, int vocabularySize, int maxSequenceLength, int batchSize);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "ONNX reranker could not use {RequestedProvider}: {Reason}. Falling back to CPU; results stay correct and slower.")]
    internal static partial void ProviderFallback(
        ILogger logger, string requestedProvider, string reason);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "ONNX reranker has mapped {UnmappedCount} token piece(s) to the unknown token. The SentencePiece asset and the tokenizer vocabulary may belong to different models.")]
    internal static partial void UnmappedPieces(ILogger logger, long unmappedCount);
}
