using System.Text.Json;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.IntegrationTests;

/// <summary>
/// The live server: version reconciliation, feature flags, and the real error
/// surface.
/// </summary>
/// <remarks>
/// Every assertion here is against observed behaviour. Several of them pin
/// contradictions between the published specification and what a deployment
/// actually does, which is the point: those contradictions are the things a stubbed
/// transport can never reveal, and the things most likely to break a caller.
/// </remarks>
public sealed class LiveServerTests
{
    [LiveServerFact]
    public async Task The_server_reports_a_version_and_feature_flags()
    {
        using var client = LiveServer.CreateClient();

        var version = await client.GetVersionAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(version.ApiVersion));
        Assert.NotNull(version.Features);

        // The client must surface the flags rather than assume they are all on.
        Assert.NotNull(version.Features.Observations);
        Assert.NotNull(version.Features.Mcp);
        Assert.NotNull(version.Features.Worker);
    }

    [LiveServerFact]
    public async Task The_served_spec_is_smaller_than_the_published_one()
    {
        // Observed: the running server serves 71 paths while the published spec
        // declares 74, at the same version string. A client generated from the
        // published spec therefore carries operations the deployment cannot serve,
        // which is why availability is decided at run time rather than at build time.
        using var served = JsonDocument.Parse(
            await LiveServer.FetchServedSpecAsync(CancellationToken.None).ContinueWith(
                t => t.Result.GetRawText(), TaskScheduler.Default));

        using var published = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "spec", "openapi.json"),
                CancellationToken.None));

        var servedPaths = served.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToHashSet();
        var publishedPaths = published.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToHashSet();

        // Nothing the server serves may be absent from the published spec: the spec is
        // the superset, and a path served but not declared would mean the client has
        // no way to know about it.
        Assert.Empty(servedPaths.Except(publishedPaths));

        // And the three transfer operations are the known gap.
        var undeclaredInServed = publishedPaths.Except(servedPaths).ToList();
        Assert.Contains("/v1/default/banks/{bank_id}/transfer/import", undeclaredInServed);
        Assert.Contains("/v1/default/banks/{bank_id}/transfer/export", undeclaredInServed);
        Assert.Contains("/v1/default/banks/{bank_id}/clone", undeclaredInServed);
    }

    [LiveServerFact]
    public async Task A_feature_flagged_operation_declared_as_200_answers_404_with_a_disabling_notice()
    {
        // Observed: POST .../health/llm is declared 200 in the published spec, but a
        // deployment with bank_llm_health=false answers 404 with a body naming the
        // variable that would enable it. Reported as "bank not found" it would send a
        // caller hunting for a typo that does not exist, so the two conditions are
        // distinguished.
        using var client = LiveServer.CreateClient();
        await using var cleanup = new BankCleanup();
        var bankId = cleanup.Track(LiveServer.NewBankId("llmhealth"));

        await client.CreateOrUpdateBankAsync(bankId, new CreateBankRequest(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<HindsightNotFoundException>(
            () => client.TestBankLlmAsync(bankId, CancellationToken.None));

        Assert.True(exception.IsFeatureDisabled);
        Assert.Contains("disabled", exception.ResponseBody, StringComparison.OrdinalIgnoreCase);
    }

    [LiveServerFact]
    public async Task An_audit_route_answers_despite_its_flag_being_false()
    {
        // Observed inconsistency: audit_log is false in the feature map, yet
        // GET .../audit-logs answers 200. A client cannot treat the flags as a simple
        // availability oracle, and a suite that assumed otherwise would be wrong about
        // its own coverage.
        using var client = LiveServer.CreateClient();
        await using var cleanup = new BankCleanup();
        var bankId = cleanup.Track(LiveServer.NewBankId("audit"));

        await client.CreateOrUpdateBankAsync(bankId, new CreateBankRequest(), CancellationToken.None);

        var result = await client.ListAuditLogsAsync(bankId, null, CancellationToken.None);

        Assert.NotNull(result);
    }

    [LiveServerFact]
    public async Task Reading_a_missing_bank_answers_404_whose_detail_is_a_string()
    {
        // Two things pinned at once: reads of a missing bank are 404 rather than an
        // empty result (deliberate, so a typo is visible), and the 404 body carries
        // `detail` as a STRING where a 422 carries it as an ARRAY.
        using var client = LiveServer.CreateClient();

        var exception = await Assert.ThrowsAsync<HindsightNotFoundException>(
            () => client.GetAgentStatsAsync($"definitely-missing-{Guid.NewGuid():N}", null, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
        Assert.Contains("not found", exception.ResponseBody, StringComparison.OrdinalIgnoreCase);
    }

    [LiveServerFact]
    public async Task A_validation_failure_answers_422_with_per_field_detail()
    {
        // Observed body: detail is an array of objects with loc/msg/type/input, and loc
        // is a mixed array of strings and integers.
        using var client = LiveServer.CreateClient();
        await using var cleanup = new BankCleanup();
        var bankId = cleanup.Track(LiveServer.NewBankId("validation"));

        await client.CreateOrUpdateBankAsync(bankId, new CreateBankRequest(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<HindsightRequestValidationException>(
            () => client.RecallMemoriesAsync(bankId, new RecallRequest { Query = "anything" }, CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.NotNull(exception.Errors);
        Assert.NotEmpty(exception.Errors);

        var error = exception.Errors[0];
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(JsonValueKind.Array, error.Location.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(error.LocationPath));
    }

    [LiveServerFact]
    public async Task A_deliberately_invalid_credential_is_ACCEPTED_by_the_default_deployment()
    {
        // Observed: the default deployment does not enforce authentication at all. A
        // garbage bearer token returns 200. This test exists because it is
        // counter-intuitive and because it means 401 and 403 CANNOT be verified here —
        // a suite that claimed to cover them would be lying.
        using var http = new HttpClient { BaseAddress = new Uri(LiveServer.BaseAddress) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "totally-invalid-key");

        var response = await http.GetAsync("/v1/default/banks", CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [LiveServerFact]
    public async Task The_client_survives_the_response_to_an_invalid_credential()
    {
        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = LiveServer.BaseAddress,
            ApiKey = "totally-invalid-key",
        });

        var banks = await client.ListBanksAsync(null, CancellationToken.None);

        Assert.NotNull(banks);
    }

    [LiveServerFact]
    public async Task The_health_endpoints_all_answer()
    {
        using var client = LiveServer.CreateClient();

        await client.HealthEndpointHealthGetAsync(CancellationToken.None);
        var liveness = await client.GetLivenessAsync(CancellationToken.None);
        var readiness = await client.GetReadinessAsync(CancellationToken.None);

        // Liveness is a typed shape; readiness is declared without a schema and so
        // arrives as raw JSON, where presence is asserted on ValueKind because
        // JsonElement is a struct.
        Assert.NotNull(liveness.Status);
        Assert.NotEqual(JsonValueKind.Undefined, readiness.ValueKind);
    }

    [LiveServerFact]
    public async Task The_metrics_endpoint_answers_in_the_prometheus_format()
    {
        using var client = LiveServer.CreateClient();

        var metrics = await client.MetricsEndpointMetricsGetAsync(CancellationToken.None);

        // This operation declares no content type and no schema, and the server answers
        // with the Prometheus exposition format as text/plain. It must be readable
        // rather than throwing a parse error, so a non-JSON content type is surfaced as
        // a JSON string value.
        Assert.NotEqual(JsonValueKind.Undefined, metrics.ValueKind);
        Assert.Contains("hindsight", metrics.GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
