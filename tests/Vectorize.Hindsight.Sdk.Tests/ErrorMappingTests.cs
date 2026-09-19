using System.Text;
using System.Text.Json;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Error mapping, asserted against the response shapes a live server actually
/// sends rather than the ones the specification declares.
/// </summary>
/// <remarks>
/// The specification declares only 404 and 422. Everything else here was observed
/// on a running server and is recorded in the repository's live-verification
/// notes, including the detail that <c>detail</c> is a string on a 404 and an
/// array on a 422 — the same key, two different types.
/// </remarks>
public sealed class ErrorMappingTests
{
    private static HindsightClient CreateClient(StubTransport transport)
    {
        var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };
        return client;
    }

    private static Task<Models.BankListResponse> Send(StubTransport transport)
    {
        using var client = CreateClient(transport);
        return client.ListBanksAsync(null, CancellationToken.None);
    }

    [Fact]
    public async Task Bad_request_maps_to_its_own_type()
    {
        var exception = await Assert.ThrowsAsync<HindsightBadRequestException>(
            () => Send(new StubTransport().EnqueueJson(400, """{"detail":"bad"}""")));

        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task Unauthorized_maps_to_authentication_failure()
    {
        var exception = await Assert.ThrowsAsync<HindsightAuthenticationException>(
            () => Send(new StubTransport().EnqueueJson(401, """{"detail":"no key"}""")));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public async Task Forbidden_maps_to_permission_denied()
    {
        var exception = await Assert.ThrowsAsync<HindsightPermissionDeniedException>(
            () => Send(new StubTransport().EnqueueJson(403, """{"detail":"nope"}""")));

        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public async Task Not_found_detail_is_a_plain_string()
    {
        // Observed shape: {"detail":"Bank 'x' not found"}. The same key is an array
        // on a 422, so both must be handled rather than one bound to a single type.
        var exception = await Assert.ThrowsAsync<HindsightNotFoundException>(
            () => Send(new StubTransport().EnqueueJson(404, """{"detail":"Bank 'x' not found"}""")));

        Assert.Contains("Bank 'x' not found", exception.Message, StringComparison.Ordinal);
        Assert.False(exception.IsFeatureDisabled);
    }

    [Fact]
    public async Task Feature_disabled_404_is_distinguishable_from_a_missing_resource()
    {
        // Observed on POST .../health/llm when HINDSIGHT_API_ENABLE_BANK_LLM_HEALTH is
        // false: the specification declares the operation as 200 and the server
        // answers 404. Reported as "bank not found" it would send a caller hunting
        // for a typo that does not exist.
        var body = """
            {"detail":"Bank LLM health check is disabled. Set HINDSIGHT_API_ENABLE_BANK_LLM_HEALTH=true to enable."}
            """;

        var exception = await Assert.ThrowsAsync<HindsightNotFoundException>(
            () => Send(new StubTransport().EnqueueJson(404, body)));

        Assert.True(exception.IsFeatureDisabled);
    }

    [Fact]
    public async Task Conflict_maps_to_its_own_type()
    {
        // Undeclared by the specification, but documented for a reused operation_id
        // and legal for the server to send, so it must not fall through to a
        // generic error.
        var exception = await Assert.ThrowsAsync<HindsightConflictException>(
            () => Send(new StubTransport().EnqueueJson(409, """{"detail":"conflict"}""")));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task Validation_failure_parses_the_per_field_errors()
    {
        // The real observed 422 body, including the mixed-type loc and the untyped
        // input that the specification declares.
        var body = """
            {"detail":[{"type":"missing","loc":["body","query"],"msg":"Field required","input":{}}]}
            """;

        var exception = await Assert.ThrowsAsync<HindsightRequestValidationException>(
            () => Send(new StubTransport().EnqueueJson(422, body)));

        Assert.NotNull(exception.Errors);
        var error = Assert.Single(exception.Errors);
        Assert.Equal("missing", error.ErrorType);
        Assert.Equal("Field required", error.Message);
        Assert.Equal("body.query", error.LocationPath);

        // loc is array<string|integer> and input is untyped, so both stay raw.
        Assert.Equal(JsonValueKind.Array, error.Location.ValueKind);
        Assert.NotNull(error.Input);
        Assert.Equal(JsonValueKind.Object, error.Input!.Value.ValueKind);
    }

    [Fact]
    public async Task Validation_failure_with_an_integer_in_loc_renders_a_path()
    {
        var body = """
            {"detail":[{"type":"value_error","loc":["body","items",0,"content"],"msg":"bad"}]}
            """;

        var exception = await Assert.ThrowsAsync<HindsightRequestValidationException>(
            () => Send(new StubTransport().EnqueueJson(422, body)));

        Assert.Equal("body.items.0.content", Assert.Single(exception.Errors!).LocationPath);
    }

    [Fact]
    public async Task Validation_failure_with_an_unparseable_body_still_reports_the_status()
    {
        // An error path that itself throws is worse than one with a plainer message,
        // so a body that does not match the declared shape must not break the mapping.
        var exception = await Assert.ThrowsAsync<HindsightRequestValidationException>(
            () => Send(new StubTransport().EnqueueJson(422, """{"detail":"just a string"}""")));

        Assert.Null(exception.Errors);
        Assert.Equal(422, exception.StatusCode);
    }

    /// <summary>
    /// Sends with retries disabled, so a retryable status surfaces as its exception.
    /// </summary>
    /// <remarks>
    /// 429 is retryable by design, so with the default three attempts it would be
    /// retried rather than thrown. Disabling retries is how the mapped exception is
    /// reached, and asserting that choice keeps the retry behaviour and the error
    /// mapping tested separately instead of one hiding the other.
    /// </remarks>
    private static async Task<HindsightApiException> SendWithoutRetry(StubTransport transport)
    {
        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
            MaxAttempts = 1,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };

        return await Assert.ThrowsAnyAsync<HindsightApiException>(
            () => client.ListBanksAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Rate_limit_carries_retry_after_when_the_server_sends_one()
    {
        var transport = new StubTransport().EnqueueJson(
            429,
            """{"detail":"slow down"}""",
            new Dictionary<string, string> { ["Retry-After"] = "12" });

        var exception = await SendWithoutRetry(transport);

        var rateLimit = Assert.IsType<HindsightRateLimitException>(exception);
        Assert.Equal(TimeSpan.FromSeconds(12), rateLimit.RetryAfter);
    }

    [Fact]
    public async Task Rate_limit_reports_no_retry_after_when_absent()
    {
        // The observed server sends no Retry-After on any response, so the null case
        // is the normal one and the client must not depend on the header existing.
        var exception = await SendWithoutRetry(
            new StubTransport().EnqueueJson(429, """{"detail":"slow down"}"""));

        Assert.Null(Assert.IsType<HindsightRateLimitException>(exception).RetryAfter);
    }

    [Fact]
    public async Task Rate_limit_is_not_a_server_exception()
    {
        // Deliberately not derived from HindsightServerException: a rate limit is the
        // server declining to serve rather than failing, and collapsing the two would
        // make a caller report an outage where it should slow down.
        var exception = await SendWithoutRetry(
            new StubTransport().EnqueueJson(429, """{"detail":"slow"}"""));

        Assert.IsNotAssignableFrom<HindsightServerException>(exception);
    }

    [Fact]
    public async Task Rate_limit_is_retried_before_it_is_reported()
    {
        // The other half of the pair: with retries enabled and a server that keeps
        // refusing, the wait is taken and the exception is raised only once attempts
        // run out.
        var transport = new StubTransport()
            .EnqueueJson(429, """{"detail":"slow"}""")
            .EnqueueJson(429, """{"detail":"slow"}""")
            .EnqueueJson(429, """{"detail":"slow"}""");

        var exception = await Assert.ThrowsAsync<HindsightRateLimitException>(() => Send(transport));

        Assert.Equal(3, transport.SendCount);
        Assert.Equal(429, exception.StatusCode);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(529)]
    public async Task Server_failures_map_to_the_server_type(int status)
    {
        var transport = new StubTransport()
            .EnqueueJson(status, """{"detail":"broken"}""")
            .EnqueueJson(status, """{"detail":"broken"}""")
            .EnqueueJson(status, """{"detail":"broken"}""");

        var exception = await Assert.ThrowsAsync<HindsightServerException>(() => Send(transport));

        Assert.Equal(status, exception.StatusCode);
    }

    [Fact]
    public async Task Overload_derives_from_the_server_type_so_one_catch_covers_it()
    {
        var exception = new HindsightOverloadedException("busy", 503, "GET /x", null);

        Assert.IsAssignableFrom<HindsightServerException>(exception);
        Assert.IsAssignableFrom<HindsightException>(exception);
    }

    [Fact]
    public async Task Undeclared_status_falls_through_to_a_typed_error_rather_than_throwing()
    {
        // 405 is real and undeclared. An unmapped status must still produce a typed
        // exception, because a mapping that throws is worse than a general one.
        var exception = await Assert.ThrowsAsync<HindsightUnexpectedStatusException>(
            () => Send(new StubTransport().EnqueueJson(405, """{"detail":"Method Not Allowed"}""")));

        Assert.Equal(405, exception.StatusCode);
    }

    [Fact]
    public async Task Every_api_exception_carries_the_route_template_and_not_the_resolved_id()
    {
        // A bank id is caller-chosen and can be identifying, so errors report the
        // route shape rather than the caller's identifier.
        var exception = await Assert.ThrowsAsync<HindsightNotFoundException>(() => Send(
            new StubTransport().EnqueueJson(404, """{"detail":"x"}""")));

        Assert.Equal("GET /v1/default/banks", exception.Endpoint);
    }

    [Fact]
    public async Task Response_body_is_preserved_for_diagnosis()
    {
        var exception = await Assert.ThrowsAsync<HindsightNotFoundException>(
            () => Send(new StubTransport().EnqueueJson(404, """{"detail":"diagnostic text"}""")));

        Assert.Contains("diagnostic text", exception.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_failure_records_that_it_was_a_timeout()
    {
        var timeout = new HindsightConnectionException("timed out") { WasTimeout = true };
        var other = new HindsightConnectionException("refused");

        Assert.True(timeout.WasTimeout);
        Assert.False(other.WasTimeout);
    }
}
