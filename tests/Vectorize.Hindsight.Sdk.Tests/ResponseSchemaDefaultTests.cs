using System.Text.Json;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// An unset <c>response_schema</c> must be ABSENT from the wire, never an empty object.
/// </summary>
/// <remarks>
/// <para>
/// The serialiser is configured with <c>WhenWritingNull</c>, so a null member is omitted from the
/// request. Initialising the member to an empty dictionary instead serialised
/// <c>"response_schema": {}</c>, and the server refuses that with
/// <c>422 response_schema must define a non-empty 'properties' object</c> — which meant every mental-model
/// create or update that set a trigger was rejected outright, and a reflect call carrying one too.
/// </para>
/// <para>
/// Each case is asserted in both directions: omitted when unset, present when set. The second half is what
/// stops "delete the member" from passing as a fix, and it is the shape a caller who actually wants a
/// schema depends on.
/// </para>
/// </remarks>
public sealed class ResponseSchemaDefaultTests
{
    private static (HindsightClient Client, StubTransport Transport) Create()
    {
        var transport = new StubTransport()
            .EnqueueJson(200, "{}")
            .EnqueueJson(200, "{}")
            .EnqueueJson(200, "{}")
            .EnqueueJson(200, "{}")
            .EnqueueJson(200, "{}")
            .EnqueueJson(200, "{}");

        var client = new HindsightClient(new HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };

        return (client, transport);
    }

    private static Dictionary<string, JsonElement> ObjectSchema() => new(StringComparer.Ordinal)
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
    };

    [Fact]
    public async Task An_unset_response_schema_is_omitted_from_a_reflect_request()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.ReflectAsync("b", new ReflectRequest { Query = "what changed?" }, CancellationToken.None);

            var body = transport.RequestBodies[0] ?? string.Empty;
            Assert.DoesNotContain("response_schema", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_set_response_schema_is_sent_on_a_reflect_request()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.ReflectAsync(
                "b",
                new ReflectRequest { Query = "what changed?", ResponseSchema = ObjectSchema() },
                CancellationToken.None);

            var body = transport.RequestBodies[0] ?? string.Empty;
            Assert.Contains("response_schema", body, StringComparison.Ordinal);
            Assert.Contains("object", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_unset_trigger_schema_is_omitted_from_a_mental_model_create()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.CreateMentalModelAsync(
                "b",
                new CreateMentalModelRequest
                {
                    Id = "m1",
                    Name = "a model",
                    SourceQuery = "what matters?",
                    Trigger = new MentalModelTriggerInput { RefreshAfterConsolidation = true },
                },
                CancellationToken.None);

            var body = transport.RequestBodies[0] ?? string.Empty;

            // The trigger itself is sent; only the unset schema is absent.
            Assert.Contains("\"trigger\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("response_schema", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_unset_trigger_schema_is_omitted_from_a_mental_model_update()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.UpdateMentalModelAsync(
                "b",
                "m1",
                new UpdateMentalModelRequest { Trigger = new MentalModelTriggerInput() },
                CancellationToken.None);

            var body = transport.RequestBodies[0] ?? string.Empty;
            Assert.Contains("\"trigger\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("response_schema", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_set_trigger_schema_is_sent_on_a_mental_model_create()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.CreateMentalModelAsync(
                "b",
                new CreateMentalModelRequest
                {
                    Id = "m1",
                    Name = "a model",
                    SourceQuery = "what matters?",
                    Trigger = new MentalModelTriggerInput { ResponseSchema = ObjectSchema() },
                },
                CancellationToken.None);

            var body = transport.RequestBodies[0] ?? string.Empty;
            Assert.Contains("response_schema", body, StringComparison.Ordinal);
        }
    }
}
