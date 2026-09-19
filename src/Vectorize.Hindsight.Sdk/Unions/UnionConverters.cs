// Hand-written converters for the four unions.
//
// Every converter declares HandleNull => true. That is not optional: for a
// reference type the default (false) makes the serializer handle a null token
// itself and skip the converter entirely, so a JSON null inside a collection or
// a map silently becomes a C# null rather than the library's own null value.
// Taking responsibility for the null token is what keeps "member absent"
// distinguishable from "member present and null".
//
// Each converter preserves an unrecognised arm as raw JSON instead of throwing.
// A server that adds a shape must not fail a whole payload — the same failure
// mode [JsonPolymorphic] has on an unknown discriminator, and the reason these
// are written by hand.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vectorize.Hindsight.Sdk.Models;

internal static class UnionJson
{
    internal const string Discriminator = "type";

    /// <summary>
    /// Reads the discriminator, or null when the element is not an object or the
    /// discriminator is absent or not a string.
    /// </summary>
    internal static string? ReadDiscriminator(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.TryGetProperty(Discriminator, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    internal static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        return false;
    }

    internal static bool TryGetElement(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    internal static JsonElement NullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}

/// <summary>Reads and writes <see cref="HindsightMemoryContent"/>.</summary>
internal sealed class HindsightMemoryContentConverter : JsonConverter<HindsightMemoryContent>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override HindsightMemoryContent? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        return root.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => HindsightMemoryContent.FromText(root.GetString()!),
            JsonValueKind.Array => ReadBlocks(root),
            _ => HindsightMemoryContent.FromRaw(root.Clone()),
        };
    }

    private static HindsightMemoryContent ReadBlocks(JsonElement array)
    {
        var blocks = new List<HindsightContentBlock?>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            blocks.Add(HindsightContentBlockConverter.ReadBlock(element));
        }

        return HindsightMemoryContent.FromBlocks(blocks);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, HindsightMemoryContent value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Text is not null)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        if (value.Blocks is not null)
        {
            writer.WriteStartArray();
            foreach (var block in value.Blocks)
            {
                HindsightContentBlockConverter.WriteBlock(writer, block);
            }

            writer.WriteEndArray();
            return;
        }

        if (value.Raw is { } raw)
        {
            raw.WriteTo(writer);
            return;
        }

        writer.WriteNullValue();
    }
}

/// <summary>Reads and writes <see cref="HindsightContentBlock"/>.</summary>
internal sealed class HindsightContentBlockConverter : JsonConverter<HindsightContentBlock>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override HindsightContentBlock? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ReadBlock(document.RootElement);
    }

    /// <summary>
    /// Reads one block. A null element inside the array is a value, not an
    /// absence: it is kept as an unmodelled arm rather than crashing the read.
    /// </summary>
    internal static HindsightContentBlock? ReadBlock(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return UnionJson.ReadDiscriminator(element) switch
        {
            HindsightContentBlockKind.Text => ReadText(element),
            HindsightContentBlockKind.Image when TryReadSource(element, out var imageSource) =>
                HindsightContentBlock.FromImage(imageSource.MediaType ?? string.Empty, imageSource.Data ?? string.Empty),
            HindsightContentBlockKind.File when TryReadSource(element, out var fileSource) =>
                HindsightContentBlock.FromFile(
                    fileSource.MediaType ?? string.Empty,
                    fileSource.Data ?? string.Empty,
                    UnionJson.TryGetString(element, "filename", out var name) ? name : null),
            _ => HindsightContentBlock.FromRaw(element.Clone()),
        };
    }

    private static HindsightContentBlock ReadText(JsonElement element)
    {
        UnionJson.TryGetString(element, "text", out var text);
        return HindsightContentBlock.FromText(text ?? string.Empty);
    }

    private static bool TryReadSource(JsonElement element, out HindsightAttachmentSource source)
    {
        source = new HindsightAttachmentSource();
        if (!UnionJson.TryGetElement(element, "source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (UnionJson.TryGetString(sourceElement, UnionJson.Discriminator, out var kind) && kind is not null)
        {
            source.Type = kind;
        }

        if (UnionJson.TryGetString(sourceElement, "media_type", out var mediaType))
        {
            source.MediaType = mediaType;
        }

        if (UnionJson.TryGetString(sourceElement, "data", out var data))
        {
            source.Data = data;
        }

        return true;
    }

    internal static void WriteBlock(Utf8JsonWriter writer, HindsightContentBlock? block)
    {
        if (block is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (block.Kind)
        {
            case HindsightContentBlockKind.Text when block.Text is not null:
                writer.WriteStartObject();
                writer.WriteString(UnionJson.Discriminator, HindsightContentBlockKind.Text);
                writer.WriteString("text", block.Text.Text ?? string.Empty);
                writer.WriteEndObject();
                return;

            case HindsightContentBlockKind.Image when block.Image is not null:
                WriteAttachmentBlock(writer, HindsightContentBlockKind.Image, block.Image.Source, null);
                return;

            case HindsightContentBlockKind.File when block.File is not null:
                WriteAttachmentBlock(writer, HindsightContentBlockKind.File, block.File.Source, block.File.Filename);
                return;

            default:
                WriteRawOrNull(writer, block.Raw);
                return;
        }
    }

    private static void WriteAttachmentBlock(
        Utf8JsonWriter writer, string kind, HindsightAttachmentSource? source, string? filename)
    {
        writer.WriteStartObject();
        writer.WriteString(UnionJson.Discriminator, kind);
        writer.WritePropertyName("source");
        writer.WriteStartObject();
        writer.WriteString(UnionJson.Discriminator, source?.Type ?? HindsightAttachmentSourceKind.Base64);
        writer.WriteString("media_type", source?.MediaType ?? string.Empty);
        writer.WriteString("data", source?.Data ?? string.Empty);
        writer.WriteEndObject();
        if (filename is not null)
        {
            writer.WriteString("filename", filename);
        }

        writer.WriteEndObject();
    }

    private static void WriteRawOrNull(Utf8JsonWriter writer, JsonElement? raw)
    {
        if (raw is { } value)
        {
            value.WriteTo(writer);
            return;
        }

        writer.WriteNullValue();
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, HindsightContentBlock value, JsonSerializerOptions options) =>
        WriteBlock(writer, value);
}

/// <summary>Reads and writes the recursive <see cref="HindsightTagGroup"/> tree.</summary>
internal sealed class HindsightTagGroupConverter : JsonConverter<HindsightTagGroup>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override HindsightTagGroup? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ReadGroup(document.RootElement);
    }

    internal static HindsightTagGroup? ReadGroup(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return HindsightTagGroup.FromRaw(element.Clone());
        }

        // A leaf is distinguished by carrying `tags`; each compound arm carries a
        // single key naming its operator.
        if (UnionJson.TryGetElement(element, "tags", out _))
        {
            return HindsightTagGroup.FromLeaf(ReadLeaf(element));
        }

        if (UnionJson.TryGetElement(element, "and", out var andElement) &&
            andElement.ValueKind == JsonValueKind.Array)
        {
            return HindsightTagGroup.All(ReadChildren(andElement));
        }

        if (UnionJson.TryGetElement(element, "or", out var orElement) &&
            orElement.ValueKind == JsonValueKind.Array)
        {
            return HindsightTagGroup.Any(ReadChildren(orElement));
        }

        if (UnionJson.TryGetElement(element, "not", out var notElement))
        {
            var child = ReadGroup(notElement);
            return child is null
                ? HindsightTagGroup.FromRaw(element.Clone())
                : HindsightTagGroup.Not(child);
        }

        return HindsightTagGroup.FromRaw(element.Clone());
    }

    private static List<HindsightTagGroup> ReadChildren(JsonElement array)
    {
        var children = new List<HindsightTagGroup>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            if (ReadGroup(element) is { } child)
            {
                children.Add(child);
            }
        }

        return children;
    }

    private static HindsightTagGroupLeaf ReadLeaf(JsonElement element)
    {
        var leaf = new HindsightTagGroupLeaf();

        if (UnionJson.TryGetElement(element, "tags", out var tags) &&
            tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String)
                {
                    leaf.Tags.Add(tag.GetString()!);
                }
            }
        }

        if (UnionJson.TryGetString(element, "match", out var match))
        {
            leaf.Match = match;
        }

        if (UnionJson.TryGetString(element, "resolve", out var resolve))
        {
            leaf.Resolve = resolve;
        }

        return leaf;
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, HindsightTagGroup value, JsonSerializerOptions options) =>
        WriteGroup(writer, value);

    private static void WriteGroup(Utf8JsonWriter writer, HindsightTagGroup value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value.Kind)
        {
            case TagGroupKind.Leaf when value.Leaf is not null:
                WriteLeaf(writer, value.Leaf);
                return;

            case TagGroupKind.And when value.Children is not null:
                WriteCompound(writer, "and", value.Children);
                return;

            case TagGroupKind.Or when value.Children is not null:
                WriteCompound(writer, "or", value.Children);
                return;

            case TagGroupKind.Not when value.Child is not null:
                writer.WriteStartObject();
                writer.WritePropertyName("not");
                WriteGroup(writer, value.Child);
                writer.WriteEndObject();
                return;

            default:
                if (value.Raw is { } raw)
                {
                    raw.WriteTo(writer);
                    return;
                }

                writer.WriteNullValue();
                return;
        }
    }

    private static void WriteLeaf(Utf8JsonWriter writer, HindsightTagGroupLeaf leaf)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        foreach (var tag in leaf.Tags)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
        if (leaf.Match is not null)
        {
            writer.WriteString("match", leaf.Match);
        }

        if (leaf.Resolve is not null)
        {
            writer.WriteString("resolve", leaf.Resolve);
        }

        writer.WriteEndObject();
    }

    private static void WriteCompound(
        Utf8JsonWriter writer, string name, IReadOnlyList<HindsightTagGroup> children)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var child in children)
        {
            WriteGroup(writer, child);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

/// <summary>Reads and writes <see cref="HindsightObservationScopes"/>.</summary>
internal sealed class HindsightObservationScopesConverter : JsonConverter<HindsightObservationScopes>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override HindsightObservationScopes? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (root.ValueKind == JsonValueKind.String)
        {
            return HindsightObservationScopes.FromMode(root.GetString()!);
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            var sets = new List<IReadOnlyList<string>>(root.GetArrayLength());
            foreach (var inner in root.EnumerateArray())
            {
                if (inner.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var tags = new List<string>(inner.GetArrayLength());
                foreach (var tag in inner.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String)
                    {
                        tags.Add(tag.GetString()!);
                    }
                }

                sets.Add(tags);
            }

            return HindsightObservationScopes.FromTagSets(sets);
        }

        return HindsightObservationScopes.FromMode(string.Empty);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, HindsightObservationScopes value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Mode is not null)
        {
            writer.WriteStringValue(value.Mode);
            return;
        }

        if (value.TagSets is not null)
        {
            writer.WriteStartArray();
            foreach (var set in value.TagSets)
            {
                writer.WriteStartArray();
                foreach (var tag in set)
                {
                    writer.WriteStringValue(tag);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            return;
        }

        writer.WriteNullValue();
    }
}
