using System.Text.Json;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// The request pipeline: success, retry, error mapping and cancellation.
/// </summary>
/// <remarks>
/// No network, no credentials. The transport stub makes every path reachable.
/// </remarks>
public sealed class PipelineTests
{
    private static HindsightClient CreateClient(StubTransport transport, Action<Configuration.HindsightClientOptions>? configure = null)
    {
        var options = new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        };
        configure?.Invoke(options);

        var client = new HindsightClient(options)
        {
            // No real waiting in tests.
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };
        return client;
    }

    [Fact]
    public async Task Success_deserializes_and_records_the_request()
    {
        var transport = new StubTransport().EnqueueJson(200, """{"banks":[],"total":0,"limit":100,"offset":0}""");
        using var client = CreateClient(transport);

        var result = await client.ListBanksAsync(null, CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Get, transport.Requests[0].Method);
        Assert.Equal("/v1/default/banks", transport.Requests[0].Path);
    }

    [Fact]
    public async Task Tenant_is_configurable_and_appears_in_the_path()
    {
        var transport = new StubTransport().EnqueueJson(200, """{"banks":[],"total":0,"limit":100,"offset":0}""");
        using var client = CreateClient(transport, o => o.Tenant = "acme");

        await client.ListBanksAsync(null, CancellationToken.None);

        Assert.Equal("/v1/acme/banks", transport.Requests[0].Path);
    }

    [Fact]
    public async Task Retryable_status_is_retried_then_succeeds()
    {
        var transport = new StubTransport()
            .EnqueueJson(503, """{"detail":"busy"}""")
            .EnqueueJson(200, """{"banks":[],"total":0,"limit":100,"offset":0}""");
        using var client = CreateClient(transport);

        var result = await client.ListBanksAsync(null, CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(2, transport.SendCount);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Client_error_is_never_retried(int status)
    {
        // A 4xx cannot be improved by repetition: the request or the credential is
        // wrong. Retrying 400/401/403/404/422 only adds traffic.
        var transport = new StubTransport().EnqueueJson(status, """{"detail":"nope"}""");
        using var client = CreateClient(transport);

        await Assert.ThrowsAnyAsync<HindsightApiException>(
            () => client.ListBanksAsync(null, CancellationToken.None));

        Assert.Equal(1, transport.SendCount);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(529)]
    public async Task Retryable_status_is_retried_up_to_the_limit(int status)
    {
        var transport = new StubTransport()
            .EnqueueJson(status, """{"detail":"transient"}""")
            .EnqueueJson(status, """{"detail":"transient"}""")
            .EnqueueJson(status, """{"detail":"transient"}""");
        using var client = CreateClient(transport, o => o.MaxAttempts = 3);

        await Assert.ThrowsAnyAsync<HindsightApiException>(
            () => client.ListBanksAsync(null, CancellationToken.None));

        Assert.Equal(3, transport.SendCount);
    }

    [Fact]
    public async Task MaxAttempts_of_one_disables_retrying()
    {
        var transport = new StubTransport().EnqueueJson(503, """{"detail":"busy"}""");
        using var client = CreateClient(transport, o => o.MaxAttempts = 1);

        await Assert.ThrowsAsync<HindsightServerException>(
            () => client.ListBanksAsync(null, CancellationToken.None));

        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task Transport_failure_is_retried_then_propagated()
    {
        var transport = new StubTransport()
            .EnqueueThrow(new HindsightConnectionException("down"))
            .EnqueueThrow(new HindsightConnectionException("down"))
            .EnqueueThrow(new HindsightConnectionException("down"));
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<HindsightConnectionException>(
            () => client.ListBanksAsync(null, CancellationToken.None));

        Assert.Equal(3, transport.SendCount);
    }

    [Fact]
    public async Task Non_idempotent_call_is_not_retried_on_a_transport_failure()
    {
        // A synchronous retain must not be repeated: its operation_id is ignored on
        // that path, so a retry could duplicate a write. The assertion is on the
        // attempt count rather than the exception type, because the transport stub
        // throws its own exception type and the point under test is the retry
        // decision, not the mapping.
        var transport = new StubTransport()
            .EnqueueThrow(new HindsightConnectionException("transport down"))
            .EnqueueJson(200, """{"success":true,"bank_id":"b","items_count":1,"async":false}""");
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<HindsightConnectionException>(() => client.RetainMemoriesAsync(
            "b",
            new RetainRequest { Items = [new MemoryItem { Content = HindsightMemoryContent.FromText("x") }] },
            CancellationToken.None));

        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task Idempotent_call_is_retried_on_a_transport_failure()
    {
        // The counterpart to the test above: a read may safely be repeated, so a
        // transport failure is retried rather than surfaced on the first attempt.
        var transport = new StubTransport()
            .EnqueueThrow(new HindsightConnectionException("transport down"))
            .EnqueueJson(200, """{"banks":[],"total":0,"limit":100,"offset":0}""");
        using var client = CreateClient(transport);

        var result = await client.ListBanksAsync(null, CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(2, transport.SendCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_connection_failure()
    {
        var transport = new StubTransport().EnqueueJson(200, "{}");
        using var client = CreateClient(transport);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ListBanksAsync(null, cts.Token));
    }

    [Fact]
    public async Task Empty_body_on_a_typed_response_is_a_protocol_error()
    {
        var transport = new StubTransport().EnqueueEmpty(200);
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<HindsightProtocolException>(
            () => client.ListBanksAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Null_body_on_a_typed_response_is_a_protocol_error()
    {
        var transport = new StubTransport().EnqueueJson(200, "null");
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<HindsightProtocolException>(
            () => client.ListBanksAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_body_is_a_protocol_error_and_is_not_retried()
    {
        // A body that did not parse will not parse on attempt two, so retrying would
        // turn a contract violation into repeated traffic.
        var transport = new StubTransport().EnqueueJson(200, "{not json");
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<HindsightProtocolException>(
            () => client.ListBanksAsync(null, CancellationToken.None));

        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task Disposed_client_refuses_to_send()
    {
        var transport = new StubTransport().EnqueueJson(200, "{}");
        var client = CreateClient(transport);
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.ListBanksAsync(null, CancellationToken.None));
    }

    [Fact]
    public void Disposing_the_client_leaves_a_caller_supplied_transport_alone()
    {
        // A transport handed in through options belongs to the caller. Disposing it
        // would break a shared or DI-managed instance, so the client must not.
        var transport = new StubTransport();
        var client = CreateClient(transport);
        client.Dispose();

        Assert.False(transport.Disposed);
    }

    [Fact]
    public void Disposing_the_client_twice_is_harmless()
    {
        var transport = new StubTransport();
        var client = CreateClient(transport);
        client.Dispose();
        client.Dispose();

        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task Declared_json_success_deserializes_into_its_modelled_shape()
    {
        // Several responses declare application/json with no schema at all; those
        // return raw JsonElement. This one declares KnowledgeNode, so it must bind to
        // the model rather than to JsonElement - asserting the distinction is real
        // rather than assumed.
        var transport = new StubTransport().EnqueueJson(
            201, """{"id":"k1","name":"n","kind":"folder","children":[]}""");
        using var client = CreateClient(transport);

        var result = await client.CreateKnowledgeFolderAsync(
            "b", new CreateFolderRequest { Name = "n" }, CancellationToken.None);

        Assert.Equal("k1", result.Id);
        Assert.Equal("n", result.Name);
    }

    [Fact]
    public async Task Declared_empty_success_returns_a_json_element()
    {
        // An operation whose 200 declares no schema returns raw JSON, because there
        // is nothing to bind to and inventing an empty class would be a lie.
        var transport = new StubTransport().EnqueueJson(200, """{"anything":[1,2,3]}""");
        using var client = CreateClient(transport);

        var result = await client.GetBankTemplateSchemaAsync(CancellationToken.None);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }
}
