// Hand-written: the four union types the generator cannot express as a single DTO.
//
// These live outside src/…/Models/ on purpose. Everything under Models/ is
// generated and safe to delete and regenerate; a regeneration deletes the
// directory, so hand-written code must not sit inside it.
//
// Each union carries a `Raw` arm. The specification's unions are extensible —
// Base64AttachmentSource's own doc comment names `url` and `blob_id` as the
// next shapes — so an unrecognised arm is preserved as raw JSON rather than
// failing the whole payload. This is deliberately not [JsonPolymorphic], which
// throws on an unknown discriminator.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vectorize.Hindsight.Sdk.Models;

// ---------------------------------------------------------------------------
// MemoryItem.content — a plain string, or ordered multimodal blocks
// ---------------------------------------------------------------------------

/// <summary>
/// A <see cref="MemoryItem"/>'s content: either a plain string, or an ordered
/// list of content blocks so an attachment sits inline where the caller wrote it.
/// </summary>
/// <remarks>
/// The block form requires a vision-capable retain model. A retain carrying
/// images against a text-only model is rejected by the server rather than
/// silently dropping them, so a caller gets a real error instead of a memory
/// that quietly lost its image.
/// </remarks>
[JsonConverter(typeof(HindsightMemoryContentConverter))]
public sealed class HindsightMemoryContent
{
    internal HindsightMemoryContent(
        string? text,
        IReadOnlyList<HindsightContentBlock?>? blocks,
        JsonElement? raw)
    {
        Text = text;
        Blocks = blocks;
        Raw = raw;
    }

    /// <summary>The plain-string form, when that is what was supplied.</summary>
    public string? Text { get; }

    /// <summary>The block form, ordered as the caller wrote it.</summary>
    public IReadOnlyList<HindsightContentBlock?>? Blocks { get; }

    /// <summary>The original JSON, when the value matched neither known arm.</summary>
    public JsonElement? Raw { get; }

    /// <summary>True when this is the plain-string form.</summary>
    public bool IsText => Text is not null;

    /// <summary>True when this is the ordered-block form.</summary>
    public bool IsBlocks => Blocks is not null;

    /// <summary>Creates the plain-string form.</summary>
    /// <param name="text">The raw content.</param>
    public static HindsightMemoryContent FromText(string text) => new(text, null, null);

    /// <summary>Creates the ordered-block form.</summary>
    /// <param name="blocks">The blocks, in order.</param>
    public static HindsightMemoryContent FromBlocks(params IReadOnlyList<HindsightContentBlock?> blocks) =>
        new(null, blocks, null);

    /// <summary>Creates the escape hatch for an unmodelled shape.</summary>
    /// <param name="raw">The original JSON.</param>
    public static HindsightMemoryContent FromRaw(JsonElement raw) => new(null, null, raw);

    /// <summary>Implicit conversion from a plain string.</summary>
    /// <param name="text">The raw content.</param>
    public static implicit operator HindsightMemoryContent(string text) => FromText(text);
}

/// <summary>One element of a multimodal item: text, an image, or a file.</summary>
/// <remarks>A discriminated union on <c>type</c>.</remarks>
[JsonConverter(typeof(HindsightContentBlockConverter))]
public sealed class HindsightContentBlock
{
    internal HindsightContentBlock(
        string? kind,
        HindsightTextContentBlock? text,
        HindsightImageContentBlock? image,
        HindsightFileContentBlock? file,
        JsonElement? raw)
    {
        Kind = kind;
        Text = text;
        Image = image;
        File = file;
        Raw = raw;
    }

    /// <summary>
    /// The discriminator: <c>text</c>, <c>image</c>, <c>file</c>, or a value this
    /// library does not model.
    /// </summary>
    public string? Kind { get; }

    /// <summary>The <c>text</c> arm.</summary>
    public HindsightTextContentBlock? Text { get; }

    /// <summary>The <c>image</c> arm.</summary>
    public HindsightImageContentBlock? Image { get; }

    /// <summary>The <c>file</c> arm.</summary>
    public HindsightFileContentBlock? File { get; }

    /// <summary>The original JSON for an unmodelled arm.</summary>
    public JsonElement? Raw { get; }

    /// <summary>Creates a text block.</summary>
    /// <param name="text">The text.</param>
    public static HindsightContentBlock FromText(string text) =>
        new(HindsightContentBlockKind.Text, new HindsightTextContentBlock { Text = text }, null, null, null);

    /// <summary>Creates an image block.</summary>
    /// <param name="mediaType">MIME type, e.g. <c>image/png</c>.</param>
    /// <param name="base64Data">Base64 bytes, with no data-URI prefix.</param>
    public static HindsightContentBlock FromImage(string mediaType, string base64Data) =>
        new(HindsightContentBlockKind.Image, null,
            new HindsightImageContentBlock
            {
                Source = new HindsightAttachmentSource { MediaType = mediaType, Data = base64Data },
            },
            null, null);

    /// <summary>Creates a file block.</summary>
    /// <param name="mediaType">MIME type, e.g. <c>application/pdf</c>.</param>
    /// <param name="base64Data">Base64 bytes, with no data-URI prefix.</param>
    /// <param name="filename">Optional filename, shown by providers that display one.</param>
    public static HindsightContentBlock FromFile(string mediaType, string base64Data, string? filename = null) =>
        new(HindsightContentBlockKind.File, null, null,
            new HindsightFileContentBlock
            {
                Source = new HindsightAttachmentSource { MediaType = mediaType, Data = base64Data },
                Filename = filename,
            },
            null);

    /// <summary>Creates the escape hatch for an unmodelled arm.</summary>
    /// <param name="raw">The original JSON.</param>
    public static HindsightContentBlock FromRaw(JsonElement raw) =>
        new(null, null, null, null, raw);
}

/// <summary>The discriminator values recognised for a content block.</summary>
public static class HindsightContentBlockKind
{
    /// <summary>A run of text.</summary>
    public const string Text = "text";

    /// <summary>An inline image.</summary>
    public const string Image = "image";

    /// <summary>A non-image attachment.</summary>
    public const string File = "file";
}

/// <summary>A run of text within a multimodal item.</summary>
public sealed class HindsightTextContentBlock
{
    /// <summary>The discriminator. Always <c>text</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = HindsightContentBlockKind.Text;

    /// <summary>The text.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>An image within a multimodal item.</summary>
public sealed class HindsightImageContentBlock
{
    /// <summary>The discriminator. Always <c>image</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = HindsightContentBlockKind.Image;

    /// <summary>The inline bytes.</summary>
    [JsonPropertyName("source")]
    public HindsightAttachmentSource? Source { get; set; }
}

/// <summary>A non-image attachment within a multimodal item.</summary>
public sealed class HindsightFileContentBlock
{
    /// <summary>The discriminator. Always <c>file</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = HindsightContentBlockKind.File;

    /// <summary>The inline bytes.</summary>
    [JsonPropertyName("source")]
    public HindsightAttachmentSource? Source { get; set; }

    /// <summary>Original filename, shown by providers that display one.</summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }
}

/// <summary>Inline attachment bytes, base64-encoded.</summary>
/// <remarks>
/// The only source type in this version; <c>url</c> and <c>blob_id</c> are
/// declared as the natural next ones, which is why <c>type</c> is carried
/// explicitly rather than inferred from the fields present.
/// </remarks>
public sealed class HindsightAttachmentSource
{
    /// <summary>The source discriminator. Always <c>base64</c> in this version.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = HindsightAttachmentSourceKind.Base64;

    /// <summary>MIME type, e.g. <c>image/png</c> or <c>application/pdf</c>.</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>Base64-encoded bytes, with no <c>data:</c> URI prefix.</summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

/// <summary>The attachment source discriminators this version defines.</summary>
public static class HindsightAttachmentSourceKind
{
    /// <summary>Inline base64 bytes.</summary>
    public const string Base64 = "base64";
}

// ---------------------------------------------------------------------------
// The tag-group expression tree
// ---------------------------------------------------------------------------

/// <summary>
/// One node of a tag-filter expression: a leaf, or a compound
/// <c>and</c>/<c>or</c>/<c>not</c> group.
/// </summary>
/// <remarks>A compound group's children are themselves tag groups, so this is recursive.</remarks>
[JsonConverter(typeof(HindsightTagGroupConverter))]
public sealed class HindsightTagGroup
{
    internal HindsightTagGroup(
        TagGroupKind kind,
        HindsightTagGroupLeaf? leaf,
        IReadOnlyList<HindsightTagGroup>? children,
        HindsightTagGroup? child,
        JsonElement? raw)
    {
        Kind = kind;
        Leaf = leaf;
        Children = children;
        Child = child;
        Raw = raw;
    }

    /// <summary>Which arm this node is.</summary>
    public TagGroupKind Kind { get; }

    /// <summary>The leaf filter, when <see cref="Kind"/> is <see cref="TagGroupKind.Leaf"/>.</summary>
    public HindsightTagGroupLeaf? Leaf { get; }

    /// <summary>Children, when <see cref="Kind"/> is <see cref="TagGroupKind.And"/> or <see cref="TagGroupKind.Or"/>.</summary>
    public IReadOnlyList<HindsightTagGroup>? Children { get; }

    /// <summary>The child, when <see cref="Kind"/> is <see cref="TagGroupKind.Not"/>.</summary>
    public HindsightTagGroup? Child { get; }

    /// <summary>The original JSON for an unmodelled node.</summary>
    public JsonElement? Raw { get; }

    /// <summary>Creates a leaf node.</summary>
    /// <param name="leaf">The leaf filter.</param>
    public static HindsightTagGroup FromLeaf(HindsightTagGroupLeaf leaf) =>
        new(TagGroupKind.Leaf, leaf, null, null, null);

    /// <summary>Creates an <c>and</c> node: every child must match.</summary>
    /// <param name="children">The child filters.</param>
    public static HindsightTagGroup All(params IReadOnlyList<HindsightTagGroup> children) =>
        new(TagGroupKind.And, null, children, null, null);

    /// <summary>Creates an <c>or</c> node: at least one child must match.</summary>
    /// <param name="children">The child filters.</param>
    public static HindsightTagGroup Any(params IReadOnlyList<HindsightTagGroup> children) =>
        new(TagGroupKind.Or, null, children, null, null);

    /// <summary>Creates a <c>not</c> node: the child must not match.</summary>
    /// <param name="child">The child filter.</param>
    public static HindsightTagGroup Not(HindsightTagGroup child) =>
        new(TagGroupKind.Not, null, null, child, null);

    /// <summary>Creates the escape hatch for a node shape this library does not model.</summary>
    /// <param name="raw">The original JSON.</param>
    public static HindsightTagGroup FromRaw(JsonElement raw) =>
        new(TagGroupKind.Unknown, null, null, null, raw);

    /// <summary>Convenience leaf from a tag list.</summary>
    /// <param name="match">A match mode from <see cref="HindsightValues.TagGroupLeafMatch"/>.</param>
    /// <param name="tags">The tags.</param>
    public static HindsightTagGroup Tags(string match, params IReadOnlyList<string> tags) =>
        FromLeaf(new HindsightTagGroupLeaf { Tags = [.. tags], Match = match });
}

/// <summary>Which arm of <see cref="HindsightTagGroup"/> a node is.</summary>
public enum TagGroupKind
{
    /// <summary>A leaf tag filter.</summary>
    Leaf,

    /// <summary>Every child must match.</summary>
    And,

    /// <summary>At least one child must match.</summary>
    Or,

    /// <summary>The child must not match.</summary>
    Not,

    /// <summary>A shape this library does not model.</summary>
    Unknown,
}

/// <summary>A leaf tag filter: a tag list plus how to match it.</summary>
public sealed class HindsightTagGroupLeaf
{
    /// <summary>The tags to match.</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// How to match: see <see cref="HindsightValues.TagGroupLeafMatch"/>. Left
    /// unset the server defaults to <c>any_strict</c>.
    /// </summary>
    [JsonPropertyName("match")]
    public string? Match { get; set; }

    /// <summary>
    /// <c>exact</c> or <c>fuzzy</c> tag resolution; see
    /// <see cref="HindsightValues.TagGroupLeafResolve"/>. Left unset the server
    /// defaults to <c>exact</c>.
    /// </summary>
    [JsonPropertyName("resolve")]
    public string? Resolve { get; set; }
}

// ---------------------------------------------------------------------------
// observation_scopes — a named mode, or explicit tag sets
// ---------------------------------------------------------------------------

/// <summary>
/// The <c>observation_scopes</c> value: a named mode such as
/// <c>all_combinations</c> or <c>per_tag</c>, or an explicit list of tag sets
/// giving one consolidation pass per inner list.
/// </summary>
[JsonConverter(typeof(HindsightObservationScopesConverter))]
public sealed class HindsightObservationScopes
{
    internal HindsightObservationScopes(string? mode, IReadOnlyList<IReadOnlyList<string>>? tagSets)
    {
        Mode = mode;
        TagSets = tagSets;
    }

    /// <summary>The named form, when that is what was supplied.</summary>
    public string? Mode { get; }

    /// <summary>The explicit tag-set form, when that is what was supplied.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? TagSets { get; }

    /// <summary>True when this is the named form.</summary>
    public bool IsMode => Mode is not null;

    /// <summary>Creates the named form.</summary>
    /// <param name="mode">e.g. <c>all_combinations</c>, <c>per_tag</c>, <c>combined</c>, <c>shared</c>.</param>
    public static HindsightObservationScopes FromMode(string mode) => new(mode, null);

    /// <summary>Creates the explicit tag-set form.</summary>
    /// <param name="tagSets">One entry per consolidation pass.</param>
    public static HindsightObservationScopes FromTagSets(params IReadOnlyList<IReadOnlyList<string>> tagSets) =>
        new(null, tagSets);

    /// <summary>Implicit conversion from the named form.</summary>
    /// <param name="mode">The mode name.</param>
    public static implicit operator HindsightObservationScopes(string mode) => FromMode(mode);
}
