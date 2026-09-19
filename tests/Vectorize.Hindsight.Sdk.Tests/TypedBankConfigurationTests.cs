using System.Text.Json;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// The typed bank-configuration surface: the patch, and the typed read model.
/// </summary>
/// <remarks>
/// <para>
/// The API represents an update as an open map of wire keys to JSON values. That is what makes a
/// newer server accept a field this client does not model, but as an interface it made a caller
/// spell <c>"enable_auto_consolidation"</c> and build a <c>JsonElement</c> for a boolean. These
/// tests pin the typed alternative: the same wire shape, reached by name.
/// </para>
/// <para>
/// The two halves are asserted differently on purpose. The patch is asserted on the bytes, because
/// omission and an explicit null are different requests; the read model is asserted on a payload
/// shaped like the server's, because the typed view has to survive a field it does not know and a
/// field that is absent.
/// </para>
/// </remarks>
public sealed class TypedBankConfigurationTests
{
    [Fact]
    public void A_patch_serializes_only_the_members_that_were_set()
    {
        var patch = new BankConfigurationPatch
        {
            EnableAutoConsolidation = false,
            RecallMaxTokens = 4096,
        };

        var updates = patch.ToUpdate().Updates;

        Assert.False(updates["enable_auto_consolidation"].GetBoolean());
        Assert.Equal(4096, updates["recall_max_tokens"].GetInt32());
        Assert.DoesNotContain("retain_chunk_size", updates.Keys);
        Assert.DoesNotContain("retain_mission", updates.Keys);
    }

    [Fact]
    public void Nested_configuration_values_serialize_into_the_shape_the_server_validates()
    {
        // Every one of these is validated server-side against the same model the request would
        // carry: observation_scope_limits against its parser, reflect_default_options and
        // knowledge_page_default_trigger against the reflect/trigger models, memory_defense
        // against the DefensePolicy schema. A shape that is merely close is rejected at the door.
        var patch = new BankConfigurationPatch
        {
            ObservationScopeLimits = new List<BankObservationScopeLimit>
            {
                new() { Scope = ["project:*", "shared"], Limit = 10 },
            },
            LlmGeminiSafetySettings = new List<GeminiSafetySetting>
            {
                new() { Category = "HARM_CATEGORY_HARASSMENT", Threshold = "BLOCK_NONE" },
            },
            ReflectDefaultOptions = new BankReflectDefaultOptions
            {
                ReflectSearchObservationsMaxTokens = 3000,
                ReflectSearchObservationsIncludeEntities = false,
            },
            KnowledgePageDefaultTrigger = new MentalModelTriggerInput
            {
                RefreshCron = "0 * * * *",
            },
            MemoryDefense = new MemoryDefensePolicy
            {
                Enabled = true,
                Rules =
                [
                    new MemoryDefenseRule
                    {
                        Detector = "sensitive_data",
                        Action = MemoryDefenseAction.Redact,
                    },
                ],
            },
        };

        var updates = patch.ToUpdate().Updates;

        Assert.Equal(10, updates["observation_scope_limits"][0].GetProperty("limit").GetInt32());
        Assert.Equal("project:*", updates["observation_scope_limits"][0].GetProperty("scope")[0].GetString());
        Assert.Equal("HARM_CATEGORY_HARASSMENT", updates["llm_gemini_safety_settings"][0].GetProperty("category").GetString());
        Assert.Equal(3000, updates["reflect_default_options"].GetProperty("reflect_search_observations_max_tokens").GetInt32());
        Assert.False(updates["reflect_default_options"].GetProperty("reflect_search_observations_include_entities").GetBoolean());
        Assert.Equal("0 * * * *", updates["knowledge_page_default_trigger"].GetProperty("refresh_cron").GetString());
        Assert.Equal("sensitive_data", updates["memory_defense"].GetProperty("rules")[0].GetProperty("on").GetString());
        Assert.Equal("redact", updates["memory_defense"].GetProperty("rules")[0].GetProperty("action").GetString());
    }

    [Fact]
    public void A_strategy_carries_only_its_own_overrides()
    {
        // A strategy is a set of field overrides applied on top of the resolved bank config, so
        // its payload must be the strategy's own members - not a nested copy of the whole bank
        // configuration with a hundred nulls in it.
        var patch = new BankConfigurationPatch
        {
            RetainDefaultStrategy = "documents",
            RetainStrategies = new Dictionary<string, BankConfigurationPatch>
            {
                ["documents"] = new BankConfigurationPatch
                {
                    RetainExtractionMode = "chunks",
                    RetainChunkSize = 800,
                },
            },
        };

        var strategies = patch.ToUpdate().Updates["retain_strategies"];

        Assert.Equal("documents", patch.ToUpdate().Updates["retain_default_strategy"].GetString());
        Assert.Equal("chunks", strategies.GetProperty("documents").GetProperty("retain_extraction_mode").GetString());
        Assert.Equal(800, strategies.GetProperty("documents").GetProperty("retain_chunk_size").GetInt32());
        Assert.False(strategies.GetProperty("documents").TryGetProperty("reflect_mission", out _));
    }

    [Fact]
    public void Clearing_an_override_sends_an_explicit_null_rather_than_omitting_it()
    {
        // The two are different requests. Omitting a member leaves the current override in place;
        // null is the server's tombstone for "remove this override and inherit again". A caller
        // who sets an enum member back to a default has no way to express the second, which is why
        // the tri-state exists.
        var patch = new BankConfigurationPatch
        {
            EnableAutoConsolidation = BankConfigValue<bool>.Clear(),
        };

        var update = patch.ToUpdate();

        Assert.True(update.Updates.ContainsKey("enable_auto_consolidation"));
        Assert.Equal(JsonValueKind.Null, update.Updates["enable_auto_consolidation"].ValueKind);
    }

    [Fact]
    public void An_unset_patch_sends_no_updates_at_all()
    {
        // Not a no-op the caller can be surprised by: an empty updates map is what an
        // unset patch means, and the server treats it as no change.
        var update = new BankConfigurationPatch().ToUpdate();

        Assert.Empty(update.Updates);
    }

    [Fact]
    public void A_strategy_map_can_itself_be_cleared()
    {
        var patch = new BankConfigurationPatch
        {
            RetainStrategies = BankConfigValue<Dictionary<string, BankConfigurationPatch>>.Clear(),
        };

        var update = patch.ToUpdate();

        Assert.Equal(JsonValueKind.Null, update.Updates["retain_strategies"].ValueKind);
    }

    [Fact]
    public async Task The_typed_overload_sends_the_same_wire_body_as_the_map_overload()
    {
        // The typed surface is a composition over the generated operation, so what it sends must
        // be exactly what the map overload would have sent. Asserted on the transport, because the
        // path and body are the contract.
        var transport = new StubTransport();
        using var client = new HindsightClient(new HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        });

        transport.EnqueueJson(200, """{"bank_id":"b","config":{},"overrides":{}}""");

        await client.UpdateBankConfigAsync(
            "b",
            new BankConfigurationPatch { EnableAutoConsolidation = false },
            CancellationToken.None);

        Assert.Equal(HttpMethod.Patch, transport.Requests[0].Method);
        Assert.Equal("/v1/default/banks/b/config", transport.Requests[0].Path);
        Assert.Equal(
            """{"updates":{"enable_auto_consolidation":false}}""",
            transport.RequestBodies[0]);
    }

    [Fact]
    public async Task The_typed_overload_rejects_a_null_patch_without_sending_anything()
    {
        var transport = new StubTransport();
        using var client = new HindsightClient(new HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        });

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.UpdateBankConfigAsync("b", (BankConfigurationPatch)null!, CancellationToken.None));

        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void A_server_payload_deserializes_into_the_typed_read_model()
    {
        // Shaped like a real response: the resolved config carries fields this client does not
        // model, and both halves carry the fields it does. The typed view reads what it knows and
        // ignores the rest.
        const string payload = """
        {
          "bank_id": "obsidian_rob",
          "config": {
            "enable_auto_consolidation": false,
            "consolidation_llm_parallelism": 2,
            "recall_max_tokens": 4096,
            "retain_extraction_mode": "concise",
            "a_field_added_after_this_client_shipped": {"nested": true}
          },
          "overrides": {
            "enable_auto_consolidation": false
          }
        }
        """;

        var response = JsonSerializer.Deserialize(
            payload,
            new HindsightJson(new HindsightClientOptions().ConfigureJson).Info<BankConfigResponse>())!;

        Assert.False(response.Configuration.EnableAutoConsolidation);
        Assert.Equal(2, response.Configuration.ConsolidationLlmParallelism);
        Assert.Equal(4096, response.Configuration.RecallMaxTokens);
        Assert.Equal("concise", response.Configuration.RetainExtractionMode);
        Assert.Null(response.Configuration.RetainMission);

        // The overrides view is the bank-only subset, so a field absent from it is null even
        // though the resolved config carries it. Conflating the two would report inherited values
        // as if the bank had chosen them.
        Assert.False(response.OverrideConfiguration.EnableAutoConsolidation);
        Assert.Null(response.OverrideConfiguration.RecallMaxTokens);
    }

    [Fact]
    public void The_raw_map_remains_reachable_alongside_the_typed_view()
    {
        // The escape hatch has to survive the typed view, or a server field added later becomes
        // unreachable and the typed surface becomes a ceiling.
        const string payload = """{"bank_id":"b","config":{"new_field":42},"overrides":{}}""";

        var response = JsonSerializer.Deserialize(
            payload,
            new HindsightJson(new HindsightClientOptions().ConfigureJson).Info<BankConfigResponse>())!;

        Assert.Equal(42, response.Config["new_field"].GetInt32());
        Assert.Null(response.Configuration.MemoryDefense);
    }

    [Fact]
    public void The_typed_surface_covers_every_configurable_field_the_spec_declares()
    {
        // The claim being tested is completeness: every field the specification puts on a bank
        // template config has a named, typed member on both the read model and the patch, so a
        // caller never has to fall back to a wire key. A field the generator missed would show up
        // here rather than in a caller's code.
        var declared = typeof(BankTemplateConfig).GetProperties().Select(p => p.Name).Order();
        var readModel = typeof(BankConfiguration).GetProperties().Select(p => p.Name).Order();
        var patch = typeof(BankConfigurationPatch).GetProperties().Select(p => p.Name).Order();

        Assert.Equal(declared, readModel);
        Assert.Equal(declared, patch);
    }

    [Fact]
    public void Every_patch_member_is_tri_state_so_clearing_is_expressible()
    {
        // A member that is not tri-state can be set but never cleared, which would silently make
        // the typed surface less capable than the map it replaces.
        var offenders = typeof(BankConfigurationPatch).GetProperties()
            .Where(p => !p.PropertyType.IsGenericType ||
                        p.PropertyType.GetGenericTypeDefinition() != typeof(BankConfigValue<>))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_read_member_never_exposes_the_write_side_tri_state()
    {
        // The defect the live run caught. `RetainStrategies` was typed from the write model, so
        // reading a strategy handed back BankConfigValue<int> where a caller expects an int - a
        // wrapper whose Value is object?, which makes the ordinary read
        // `strategies["documents"].RetainChunkSize == 800` fail to compile. The read model and the
        // patch may differ in type; what they must never do is leak a write-side concern into a read.
        var offenders = typeof(BankConfiguration).GetProperties()
            .SelectMany(p => p.PropertyType.IsGenericType &&
                             p.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                ? p.PropertyType.GetGenericArguments().Skip(1)
                : [p.PropertyType])
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BankConfigValue<>))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_strategy_reads_back_as_values_not_as_a_patch()
    {
        // The behavioural half of the rule above: assert it through the serializer, so the
        // property type and the deserialized value are both exercised.
        const string payload = """
        {
          "bank_id": "b",
          "config": {
            "retain_strategies": {
              "documents": { "retain_extraction_mode": "chunks", "retain_chunk_size": 800 }
            }
          },
          "overrides": {}
        }
        """;

        var response = JsonSerializer.Deserialize(
            payload,
            new HindsightJson(new HindsightClientOptions().ConfigureJson).Info<BankConfigResponse>())!;

        var strategy = response.Configuration.RetainStrategies!["documents"];

        Assert.Equal("chunks", strategy.RetainExtractionMode);
        Assert.Equal(800, strategy.RetainChunkSize);
    }
}
