using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Vectorize.Hindsight.Sdk.IntegrationTests;

/// <summary>
/// Shared harness for the live integration suite.
/// </summary>
/// <remarks>
/// <para>
/// The suite talks to a real server, so it needs a real address and it needs to
/// fail clearly when the server is not there. It never talks to a production
/// instance: the address comes from the environment and defaults to the local test
/// rig only.
/// </para>
/// <para>
/// Tests that spend an external LLM quota are separated from the rest. Retain and
/// reflect cost money and are rate-limited, while reads, bank CRUD, config,
/// directives, mental models, knowledge-base structure, operations, webhooks, tags
/// and stats cost nothing. The optional ones are skipped unless
/// <c>HINDSIGHT_INTEGRATION_LLM=1</c> is set, so the suite is useful without
/// burning quota and complete when it matters.
/// </para>
/// </remarks>
public static class LiveServer
{
    /// <summary>The address of the server under test.</summary>
    public static string BaseAddress { get; } =
        Environment.GetEnvironmentVariable("HINDSIGHT_BASE_URL") ?? "http://127.0.0.1:8888";

    /// <summary>
    /// Whether LLM-backed tests should run.
    /// </summary>
    /// <remarks>
    /// Off by default. Quota is metered and shared with a production instance, so
    /// the tests that spend it have to be asked for explicitly.
    /// </remarks>
    public static bool LlmEnabled { get; } =
        Environment.GetEnvironmentVariable("HINDSIGHT_INTEGRATION_LLM") == "1";

    /// <summary>A unique bank id, so runs never collide with each other or with prior state.</summary>
    public static string NewBankId(string purpose) => $"sdk-it-{purpose}-{Guid.NewGuid():N}"[..40];

    /// <summary>Creates a client pointed at the live server.</summary>
    public static HindsightClient CreateClient() =>
        new(new Configuration.HindsightClientOptions { BaseAddress = BaseAddress });

    /// <summary>
    /// Fetches <c>/version</c> directly, without the client, so the suite can decide
    /// what to run from the server's own report rather than from an assumption.
    /// </summary>
    public static async Task<JsonElement> FetchVersionAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        var json = await http.GetStringAsync("/version", cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Fetches the server's own OpenAPI document, which is the authoritative
    /// description of what THIS deployment serves.
    /// </summary>
    public static async Task<JsonElement> FetchServedSpecAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        var json = await http.GetStringAsync("/openapi.json", cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Whether the server is reachable. Used to skip rather than fail.
    /// </summary>
    public static async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync("/health/ready", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Skips a test when the local server is not running, rather than failing it.
/// </summary>
/// <remarks>
/// A suite that fails because a developer has not started the rig trains people to
/// ignore failures. A suite that silently passes without running anything is worse.
/// Skipping with a visible reason is the honest middle.
/// </remarks>
public sealed class LiveServerFact : FactAttribute
{
    public LiveServerFact()
    {
        if (!LiveServer.IsReachableAsync(CancellationToken.None).GetAwaiter().GetResult())
        {
            Skip = $"No Hindsight server at {LiveServer.BaseAddress}. Start the rig with test-infra/up.sh.";
        }
    }
}

/// <summary>Skips an LLM-backed test unless quota spending was explicitly enabled.</summary>
public sealed class LlmFactAttribute : FactAttribute
{
    public LlmFactAttribute()
    {
        if (!LiveServer.LlmEnabled)
        {
            Skip = "LLM quota is metered and shared. Set HINDSIGHT_INTEGRATION_LLM=1 to run this.";
        }
    }
}

/// <summary>
/// Deletes the banks a test created, so the suite is repeatable.
/// </summary>
internal sealed class BankCleanup : IAsyncDisposable
{
    private readonly List<string> _bankIds = [];

    internal string Track(string bankId)
    {
        _bankIds.Add(bankId);
        return bankId;
    }

    public async ValueTask DisposeAsync()
    {
        using var client = LiveServer.CreateClient();
        foreach (var bankId in _bankIds)
        {
            try
            {
                await client.DeleteBankAsync(bankId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Errors.HindsightException)
            {
                // Teardown must not mask a test failure by throwing itself. A bank left
                // behind is untidy; an exception here would hide the real result.
            }
        }
    }
}
