using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Vectorize.Hindsight.Sdk.Reranking.Onnx;

/// <summary>
/// Turns a query and a passage into the exact token ids a Hugging Face cross-encoder expects.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the part that goes silently wrong.</b> ONNX Runtime will happily run a model on
/// wrong input and return numbers that look like scores, so a tokenization error does not surface
/// as an exception — it surfaces as a reranker that quietly ranks badly. Everything here exists to
/// make the ids correct and to prove it against the reference implementation.
/// </para>
/// <para>
/// <b>Why two tokenizer assets are needed.</b> An XLM-R derived reranker (BGE v2-m3 included) uses
/// SentencePiece for segmentation, which lives in the model's <c>sentencepiece.bpe.model</c>. The
/// <i>ids</i>, however, are the ones in <c>tokenizer.json</c>. Those two disagree in a specific
/// way: the Hugging Face vocabulary inserts a <c>&lt;pad&gt;</c> at index 1, which shifts every
/// subsequent piece by one, and it orders the special tokens differently
/// (<c>&lt;s&gt; &lt;pad&gt; &lt;/s&gt; &lt;unk&gt;</c> against SentencePiece's
/// <c>&lt;unk&gt; &lt;s&gt; &lt;/s&gt;</c>).
/// </para>
/// <para>
/// So the segmentation is taken from SentencePiece and each resulting piece is mapped to its id by
/// looking that piece up in the vocabulary from <c>tokenizer.json</c>. The mapping is therefore
/// <i>derived from the model's own artifacts</i> rather than hardcoded as an offset — verified to
/// reproduce the reference ids exactly on English and multilingual input.
/// </para>
/// <para>
/// A SentencePiece piece carries its leading space as the metaspace marker <c>U+2581</c>, and
/// <see cref="EncodedToken.Value"/> preserves that marker, which is what makes the lookup an exact
/// string match rather than a guess about whitespace.
/// </para>
/// </remarks>
internal sealed class CrossEncoderTokenizer
{
    private const int UnknownIdFallback = 3;

    private readonly SentencePieceTokenizer _segmenter;
    private readonly Dictionary<string, int> _pieceToId;
    private readonly string _unknownPiece;

    private CrossEncoderTokenizer(
        SentencePieceTokenizer segmenter,
        Dictionary<string, int> pieceToId,
        string unknownPiece)
    {
        _segmenter = segmenter;
        _pieceToId = pieceToId;
        _unknownPiece = unknownPiece;
    }

    /// <summary>The id of the <c>&lt;unk&gt;</c> piece in this vocabulary.</summary>
    internal int UnknownId { get; private init; }

    /// <summary>Loads the vocabulary and the segmentation model.</summary>
    /// <param name="tokenizerJsonPath">The Hugging Face <c>tokenizer.json</c>.</param>
    /// <param name="sentencePieceModelPath">The SentencePiece model.</param>
    internal static CrossEncoderTokenizer Load(string tokenizerJsonPath, string sentencePieceModelPath)
    {
        using var jsonStream = File.OpenRead(tokenizerJsonPath);
        using var document = JsonDocument.Parse(jsonStream);

        var vocabulary = document.RootElement.GetProperty("model").GetProperty("vocab");
        var pieceToId = new Dictionary<string, int>(StringComparer.Ordinal);

        // The entries are [piece, score] pairs, and the ARRAY INDEX is the id. Reading a second
        // element as an id would be wrong and throws, which is the kind of mistake worth stating.
        var index = 0;
        foreach (var entry in vocabulary.EnumerateArray())
        {
            pieceToId[entry[0].GetString()!] = index++;
        }

        // The unknown token is named in the model's own special-token configuration rather than
        // assumed, so a differently configured export still resolves.
        var unknownPiece = document.RootElement.TryGetProperty("model", out var model)
            && model.TryGetProperty("unk_token", out var unkToken)
            && unkToken.ValueKind == JsonValueKind.String
                ? unkToken.GetString()!
                : "<unk>";

        using var spStream = File.OpenRead(sentencePieceModelPath);
        var segmenter = SentencePieceTokenizer.Create(
            spStream,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            specialTokens: null);

        return new CrossEncoderTokenizer(segmenter, pieceToId, unknownPiece)
        {
            UnknownId = pieceToId.TryGetValue(unknownPiece, out var unkId) ? unkId : UnknownIdFallback,
        };
    }

    /// <summary>How many pieces the vocabulary holds.</summary>
    internal int VocabularySize => _pieceToId.Count;

    /// <summary>
    /// Encodes a query and a passage as one sequence, in the model's expected pair format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layout is <c>&lt;s&gt; query &lt;/s&gt;&lt;/s&gt; passage &lt;/s&gt;</c>, which is what
    /// the reference <c>text_pair</c> encoding produces for this model family. The doubled
    /// end-of-sequence token is not a typo.
    /// </para>
    /// <para>
    /// When the pair is too long, the passage is truncated and the query is left whole. A query cut
    /// in half changes the meaning of the question and scores every passage wrongly; a passage cut
    /// in half still carries most of its own meaning.
    /// </para>
    /// </remarks>
    /// <param name="query">The query.</param>
    /// <param name="passage">The passage.</param>
    /// <param name="maxSequenceLength">The longest allowed sequence, special tokens included.</param>
    internal int[] Encode(string query, string passage, int maxSequenceLength)
    {
        var queryIds = MapPieces(query);
        var passageIds = MapPieces(passage);

        // 1 (BOS) + 2 (doubled EOS) + 1 (trailing EOS) = 4, then the content shares what is left.
        var budget = maxSequenceLength - 4;
        if (budget < 1)
        {
            // A sequence limit too small to hold even one content token: emit the framing only, and
            // let the caller's validation (which requires a positive, sane limit) be the guard.
            budget = 1;
        }

        List<int> trimmedQuery;
        List<int> trimmedPassage;

        if (queryIds.Count + passageIds.Count <= budget)
        {
            trimmedQuery = queryIds;
            trimmedPassage = passageIds;
        }
        else if (queryIds.Count >= budget)
        {
            // The query alone fills the budget. Truncating it is unavoidable, but taking its head
            // keeps the leading words, which carry the subject of the question.
            trimmedQuery = queryIds.GetRange(0, budget);
            trimmedPassage = [];
        }
        else
        {
            trimmedQuery = queryIds;
            trimmedPassage = passageIds.GetRange(0, budget - queryIds.Count);
        }

        var ids = new List<int>(trimmedQuery.Count + trimmedPassage.Count + 4)
        {
            SpecialTokenId("<s>"),
        };

        ids.AddRange(trimmedQuery);
        ids.Add(EndOfSequenceId());
        ids.Add(EndOfSequenceId());
        ids.AddRange(trimmedPassage);
        ids.Add(EndOfSequenceId());

        return [.. ids];
    }

    /// <summary>Maps a string to ids: segment with SentencePiece, then look each piece up.</summary>
    /// <remarks>
    /// An unmapped piece resolves to the vocabulary's own unknown id rather than a guessed value,
    /// and is counted by <see cref="UnmappedPieceCount"/> so a caller can tell a healthy run from
    /// one that is silently losing tokens.
    /// </remarks>
    private List<int> MapPieces(string text)
    {
        var ids = new List<int>();

        if (string.IsNullOrEmpty(text))
        {
            return ids;
        }

        foreach (var token in _segmenter.EncodeToTokens(text, out _))
        {
            if (_pieceToId.TryGetValue(token.Value, out var id))
            {
                ids.Add(id);
            }
            else
            {
                UnmappedPieceCount++;
                ids.Add(UnknownId);
            }
        }

        return ids;
    }

    /// <summary>
    /// How many pieces could not be mapped to a vocabulary entry since this instance was created.
    /// </summary>
    /// <remarks>
    /// Expected to stay at zero. A non-zero value means the SentencePiece asset and the vocabulary
    /// do not belong to the same model, which is the failure this counter exists to make visible —
    /// it would otherwise show up only as a reranker with worse-than-expected quality.
    /// </remarks>
    internal long UnmappedPieceCount { get; private set; }

    /// <summary>The id of a required special token, resolved from the vocabulary.</summary>
    private int SpecialTokenId(string piece) =>
        _pieceToId.TryGetValue(piece, out var id) ? id : _pieceToId[_unknownPiece];

    private int EndOfSequenceId() => SpecialTokenId("</s>");
}
