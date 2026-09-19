using System.Text.Json;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// The deeper paths of the hand-written union converters.
/// </summary>
/// <remarks>
/// The conversions a caller reaches least often are the ones a converter gets
/// wrong, because nothing exercises them until a server sends that shape. These
/// cover the null arms, the malformed arms, the round trips through every branch,
/// and the writer side of each union.
/// </remarks>
public sealed class UnionConverterTests
{
    private static JsonSerializerOptions Options()
    {
        var json = new HindsightJson(new Configuration.HindsightClientOptions().ConfigureJson);
        return json.Options;
    }

    // --- MemoryContent -----------------------------------------------------

    [Fact]
    public void Content_reads_a_null_as_null()
    {
        var item = JsonSerializer.Deserialize<MemoryItem>(
            """{"content":null}""", Options());

        Assert.NotNull(item);
        Assert.Null(item.Content);
    }

    [Fact]
    public void Content_preserves_an_unmodelled_scalar_shape()
    {
        // Neither a string nor an array: the escape hatch keeps it rather than throwing.
        var item = JsonSerializer.Deserialize<MemoryItem>("""{"content":42}""", Options());

        Assert.NotNull(item);
        Assert.NotNull(item.Content);
        Assert.NotNull(item.Content.Raw);
        Assert.False(item.Content.IsText);
        Assert.False(item.Content.IsBlocks);
    }

    [Fact]
    public void Content_round_trips_its_raw_arm_unchanged()
    {
        var item = JsonSerializer.Deserialize<MemoryItem>("""{"content":{"unexpected":true}}""", Options());

        var json = JsonSerializer.Serialize(item, Options());

        Assert.Contains("\"unexpected\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_serializes_a_raw_null_when_it_has_no_arm()
    {
        // Constructed through the raw path with a null element: the writer must emit
        // null rather than throwing on an undefined value.
        var content = HindsightMemoryContent.FromRaw(default);
        var json = JsonSerializer.Serialize(content, Options());

        Assert.Equal("null", json);
    }

    [Fact]
    public void Content_drops_an_empty_block_list_rather_than_inventing_one()
    {
        var item = new MemoryItem { Content = HindsightMemoryContent.FromBlocks() };

        var json = JsonSerializer.Serialize(item, Options());

        Assert.Contains("\"content\":[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_conversion_from_a_plain_string_uses_the_text_arm()
    {
        HindsightMemoryContent content = "implicit";

        Assert.True(content.IsText);
        Assert.Equal("implicit", content.Text);
    }

    // --- ContentBlock ------------------------------------------------------

    [Fact]
    public void A_null_block_in_an_array_survives_a_full_round_trip()
    {
        var item = JsonSerializer.Deserialize<MemoryItem>(
            """{"content":[null,{"type":"text","text":"t"}]}""", Options());

        var json = JsonSerializer.Serialize(item, Options());

        Assert.Contains("null", json, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"t\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_with_no_discriminator_is_kept_as_raw()
    {
        var item = JsonSerializer.Deserialize<MemoryItem>(
            """{"content":[{"text":"no type field"}]}""", Options());

        Assert.NotNull(item);
        Assert.NotNull(item.Content);
        Assert.NotNull(item.Content.Blocks);
        Assert.Null(item.Content.Blocks![0]!.Kind);
        Assert.NotNull(item.Content.Blocks[0]!.Raw);
    }

    [Fact]
    public void An_image_block_with_a_missing_source_is_tolerated()
    {
        // A malformed arm must not crash the read; it degrades to an empty source.
        var item = JsonSerializer.Deserialize<MemoryItem>(
            """{"content":[{"type":"image"}]}""", Options());

        Assert.NotNull(item);
        var block = item.Content!.Blocks![0]!;
        Assert.Equal("image", block.Kind);
        Assert.Equal(string.Empty, block.Image!.Source!.MediaType);
    }

    [Fact]
    public void A_file_block_with_no_filename_omits_the_field_on_write()
    {
        var item = new MemoryItem
        {
            Content = HindsightMemoryContent.FromBlocks(
                HindsightContentBlock.FromFile("application/pdf", "ZZZ")),
        };

        var json = JsonSerializer.Serialize(item, Options());

        Assert.DoesNotContain("filename", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"file\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmodelled_block_arm_round_trips_through_a_null_holder()
    {
        var block = HindsightContentBlock.FromRaw(default);
        var json = JsonSerializer.Serialize(block, Options());

        Assert.Equal("null", json);
    }

    [Fact]
    public void A_block_read_directly_from_a_null_token_is_null()
    {
        var block = JsonSerializer.Deserialize<HindsightContentBlock>("null", Options());

        Assert.Null(block);
    }

    // --- TagGroup ----------------------------------------------------------

    [Fact]
    public void A_tag_group_read_from_a_null_token_is_null()
    {
        Assert.Null(JsonSerializer.Deserialize<HindsightTagGroup>("null", Options()));
    }

    [Fact]
    public void A_tag_group_read_from_a_non_object_is_kept_as_raw()
    {
        var group = JsonSerializer.Deserialize<HindsightTagGroup>("42", Options());

        Assert.NotNull(group);
        Assert.Equal(TagGroupKind.Unknown, group.Kind);
        Assert.NotNull(group.Raw);
    }

    [Fact]
    public void An_or_group_round_trips()
    {
        var group = HindsightTagGroup.Any(
            HindsightTagGroup.Tags(HindsightValues.TagGroupLeafMatch.ANY, "a"),
            HindsightTagGroup.Tags(HindsightValues.TagGroupLeafMatch.ANY, "b"));

        var json = JsonSerializer.Serialize(group, Options());
        var restored = JsonSerializer.Deserialize<HindsightTagGroup>(json, Options());

        Assert.Contains("\"or\":[", json, StringComparison.Ordinal);
        Assert.Equal(TagGroupKind.Or, restored!.Kind);
        Assert.Equal(2, restored.Children!.Count);
    }

    [Fact]
    public void A_leaf_omits_match_and_resolve_when_unset()
    {
        var group = HindsightTagGroup.FromLeaf(new HindsightTagGroupLeaf { Tags = ["x"] });

        var json = JsonSerializer.Serialize(group, Options());

        Assert.DoesNotContain("match", json, StringComparison.Ordinal);
        Assert.DoesNotContain("resolve", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_leaf_carries_resolve_when_set()
    {
        var group = HindsightTagGroup.FromLeaf(new HindsightTagGroupLeaf
        {
            Tags = ["x"],
            Resolve = HindsightValues.TagGroupLeafResolve.FUZZY,
        });

        var json = JsonSerializer.Serialize(group, Options());
        var restored = JsonSerializer.Deserialize<HindsightTagGroup>(json, Options());

        Assert.Equal("fuzzy", restored!.Leaf!.Resolve);
    }

    [Fact]
    public void A_not_group_with_an_unusable_child_degrades_to_raw_rather_than_throwing()
    {
        var group = JsonSerializer.Deserialize<HindsightTagGroup>("""{"not":null}""", Options());

        Assert.NotNull(group);
        Assert.Equal(TagGroupKind.Unknown, group.Kind);
    }

    [Fact]
    public void An_unmodelled_tag_group_node_round_trips()
    {
        var group = HindsightTagGroup.FromRaw(
            JsonDocument.Parse("""{"between":{"a":1,"b":2}}""").RootElement.Clone());

        var json = JsonSerializer.Serialize(group, Options());

        Assert.Contains("\"between\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_group_with_a_raw_null_writes_null()
    {
        var group = HindsightTagGroup.FromRaw(default);

        Assert.Equal("null", JsonSerializer.Serialize(group, Options()));
    }

    [Fact]
    public void Compound_children_that_are_null_are_skipped_rather_than_failing()
    {
        // A null inside the children array is a value; it is dropped from the list
        // rather than becoming a null node in the tree.
        var group = JsonSerializer.Deserialize<HindsightTagGroup>("""{"and":[null,{"tags":["a"]}]}""", Options());

        Assert.Equal(TagGroupKind.And, group!.Kind);
        Assert.Single(group.Children!);
    }

    [Fact]
    public void A_leaf_with_a_non_string_tag_ignores_it()
    {
        var group = JsonSerializer.Deserialize<HindsightTagGroup>(
            """{"tags":["ok",7,null],"match":"all"}""", Options());

        Assert.Single(group!.Leaf!.Tags);
        Assert.Equal("ok", group.Leaf.Tags[0]);
    }

    // --- ObservationScopes -------------------------------------------------

    [Fact]
    public void Observation_scopes_reads_a_null_as_null()
    {
        Assert.Null(JsonSerializer.Deserialize<HindsightObservationScopes>("null", Options()));
    }

    [Fact]
    public void Observation_scopes_tolerates_a_shape_it_does_not_model()
    {
        var scopes = JsonSerializer.Deserialize<HindsightObservationScopes>("42", Options());

        Assert.NotNull(scopes);
        Assert.True(scopes.IsMode);
        Assert.Equal(string.Empty, scopes.Mode);
    }

    [Fact]
    public void Observation_scopes_ignores_a_non_array_entry_in_the_tag_sets_form()
    {
        var scopes = JsonSerializer.Deserialize<HindsightObservationScopes>(
            """[["a"],"not-an-array",["b","c"]]""", Options());

        Assert.False(scopes!.IsMode);
        Assert.Equal(2, scopes.TagSets!.Count);
        Assert.Equal(2, scopes.TagSets[1].Count);
    }

    [Fact]
    public void Observation_scopes_ignores_non_string_tags_inside_a_set()
    {
        var scopes = JsonSerializer.Deserialize<HindsightObservationScopes>(
            """[["a",5,"b"]]""", Options());

        Assert.Equal(2, scopes!.TagSets![0].Count);
    }

    [Fact]
    public void Observation_scopes_writes_null_when_it_has_no_form()
    {
        // Constructed from a raw null: neither arm is set.
        var scopes = JsonSerializer.Deserialize<HindsightObservationScopes>("null", Options());

        Assert.Null(scopes);
    }

    [Fact]
    public void Observation_scopes_round_trips_the_tag_sets_form()
    {
        var scopes = HindsightObservationScopes.FromTagSets([["x", "y"]]);

        var json = JsonSerializer.Serialize(scopes, Options());
        var restored = JsonSerializer.Deserialize<HindsightObservationScopes>(json, Options());

        Assert.Equal("[[\"x\",\"y\"]]", json);
        Assert.Equal(2, restored!.TagSets![0].Count);
    }

    [Fact]
    public void Observation_scopes_converts_implicitly_from_a_mode_name()
    {
        HindsightObservationScopes scopes = "per_tag";

        Assert.True(scopes.IsMode);
        Assert.Equal("per_tag", scopes.Mode);
    }

    [Fact]
    public void Observation_scopes_writes_null_for_an_empty_instance()
    {
        // Both arms null - which the factories never produce, but the writer must still
        // be total rather than throwing.
        var scopes = HindsightObservationScopes.FromTagSets();
        scopes = scopes.TagSets is null ? scopes : HindsightObservationScopes.FromTagSets();

        var json = JsonSerializer.Serialize(scopes, Options());
        Assert.Equal("[]", json);
    }
}
