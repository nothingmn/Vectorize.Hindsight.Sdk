using System.Text.Json;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Serialization of the hand-written union types through the generated context.
/// </summary>
/// <remarks>
/// These are the shapes the generator cannot express, so they are the ones most
/// likely to be missing from the context or to lose their discriminator on the
/// way out. A union that serializes without its <c>type</c> field produces a
/// request the server rejects, which is why this is asserted on the bytes rather
/// than on the object graph.
/// </remarks>
public sealed class UnionSerializationTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new Configuration.HindsightClientOptions();
        var json = new Http.HindsightJson(options.ConfigureJson);
        return json.Options;
    }

    [Fact]
    public void Content_serializes_a_plain_string_as_a_json_string()
    {
        var item = new MemoryItem { Content = HindsightMemoryContent.FromText("hello") };

        var json = JsonSerializer.Serialize(item, Options());

        Assert.Contains("\"content\":\"hello\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_serializes_blocks_with_their_discriminators()
    {
        var item = new MemoryItem
        {
            Content = HindsightMemoryContent.FromBlocks(
                HindsightContentBlock.FromText("look:"),
                HindsightContentBlock.FromImage("image/png", "AAAA")),
        };

        var json = JsonSerializer.Serialize(item, Options());

        Assert.Contains("\"type\":\"text\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"image\"", json, StringComparison.Ordinal);
        Assert.Contains("\"media_type\":\"image/png\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void File_block_carries_its_filename_when_set()
    {
        var item = new MemoryItem
        {
            Content = HindsightMemoryContent.FromBlocks(
                HindsightContentBlock.FromFile("application/pdf", "BBBB", "report.pdf")),
        };

        var json = JsonSerializer.Serialize(item, Options());

        Assert.Contains("\"type\":\"file\"", json, StringComparison.Ordinal);
        Assert.Contains("\"filename\":\"report.pdf\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_round_trips_through_the_blocks_form()
    {
        var original = new MemoryItem
        {
            Content = HindsightMemoryContent.FromBlocks(
                HindsightContentBlock.FromText("t"),
                HindsightContentBlock.FromImage("image/png", "AAAA")),
        };

        var json = JsonSerializer.Serialize(original, Options());
        var restored = JsonSerializer.Deserialize<MemoryItem>(json, Options());

        Assert.NotNull(restored);
        Assert.True(restored.Content.IsBlocks);
        Assert.Equal(2, restored.Content.Blocks!.Count);
        Assert.Equal("text", restored.Content.Blocks[0]!.Kind);
        Assert.Equal("image", restored.Content.Blocks[1]!.Kind);
        Assert.Equal("image/png", restored.Content.Blocks[1]!.Image!.Source!.MediaType);
    }

    [Fact]
    public void Tag_group_round_trips_a_nested_expression()
    {
        var group = HindsightTagGroup.All(
            HindsightTagGroup.FromLeaf(new HindsightTagGroupLeaf
            {
                Tags = ["a"],
                Match = HindsightValues.TagGroupLeafMatch.ALL,
            }),
            HindsightTagGroup.Not(HindsightTagGroup.Tags(HindsightValues.TagGroupLeafMatch.ALL, "b")));

        var json = JsonSerializer.Serialize(group, Options());
        var restored = JsonSerializer.Deserialize<HindsightTagGroup>(json, Options());

        Assert.Contains("\"and\":[", json, StringComparison.Ordinal);
        Assert.Contains("\"not\":{", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal(TagGroupKind.And, restored.Kind);
        Assert.Equal(2, restored.Children!.Count);
        Assert.Equal(TagGroupKind.Not, restored.Children[1].Kind);
    }

    [Fact]
    public void Observation_scopes_supports_both_forms()
    {
        var asMode = JsonSerializer.Serialize(
            HindsightObservationScopes.FromMode("all_combinations"), Options());
        Assert.Equal("\"all_combinations\"", asMode);

        var asSets = JsonSerializer.Serialize(
            HindsightObservationScopes.FromTagSets([["x", "y"], ["z"]]), Options());
        Assert.Equal("[[\"x\",\"y\"],[\"z\"]]", asSets);
    }

    [Fact]
    public void Unknown_content_block_arm_is_preserved_rather_than_throwing()
    {
        // A server adding a fourth block kind must not fail the whole payload.
        const string payload = """
            {"content":[{"type":"video","source":{"media_type":"video/mp4","data":"ZZZ"}}]}
            """;

        var item = JsonSerializer.Deserialize<MemoryItem>(payload, Options());

        Assert.NotNull(item);
        var blocks = Assert.IsAssignableFrom<IReadOnlyList<HindsightContentBlock?>>(item.Content.Blocks);
        Assert.Single(blocks);
        Assert.Null(blocks[0]!.Kind);
        Assert.NotNull(blocks[0]!.Raw);

        // And the unmodelled arm survives a round trip unchanged.
        var reserialized = JsonSerializer.Serialize(item, Options());
        Assert.Contains("\"type\":\"video\"", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_inside_a_content_array_is_a_value_not_a_crash()
    {
        // HandleNull => true on every converter is what makes this a read rather
        // than a throw: a JSON null inside a collection is a value, not an absence.
        const string payload = """{"content":[null,{"type":"text","text":"after"}]}""";

        var item = JsonSerializer.Deserialize<MemoryItem>(payload, Options());

        Assert.NotNull(item);
        Assert.Equal(2, item.Content.Blocks!.Count);
        Assert.Null(item.Content.Blocks[0]);
        Assert.Equal("text", item.Content.Blocks[1]!.Kind);
    }

    [Fact]
    public void Unknown_tag_group_node_keeps_its_raw_json()
    {
        const string payload = """{"tags":["a"],"match":"any","future_field":{"nested":1}}""";

        var group = JsonSerializer.Deserialize<HindsightTagGroup>(payload, Options());

        Assert.NotNull(group);
        Assert.Equal(TagGroupKind.Leaf, group.Kind);
        Assert.Equal("a", group.Leaf!.Tags[0]);
    }
}
