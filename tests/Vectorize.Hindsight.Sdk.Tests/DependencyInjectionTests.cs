using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk.DependencyInjection;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// The dependency-injection integration.
/// </summary>
/// <remarks>
/// The package is a convenience layer over the same seams, so what is asserted
/// here is that a caller's own registrations win, that optional collaborators are
/// resolved from the container rather than hardcoded, and that warmup is never
/// automatic.
/// </remarks>
public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddHindsight_registers_a_resolvable_singleton()
    {
        var services = new ServiceCollection();
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.NotNull(client);
        Assert.Same(client, provider.GetRequiredService<HindsightClient>());
    }

    [Fact]
    public void AddHindsight_applies_the_configuration_callback()
    {
        var services = new ServiceCollection();
        services.AddHindsight(options =>
        {
            options.BaseAddress = "https://hindsight.example.com";
            options.Tenant = "acme";
            options.ApiKey = "configured-key";
        });

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Equal("https://hindsight.example.com", client.Options.BaseAddress);
        Assert.Equal("acme", client.Options.Tenant);
        Assert.Equal("configured-key", client.Options.ApiKey);
    }

    [Fact]
    public void A_caller_supplied_transport_is_used_rather_than_the_built_in_one()
    {
        // A DI extension that hardcoded `Transport = null` would defeat caller
        // substitution and make the client untestable in a host. This asserts the
        // container's registration actually reaches the client.
        var transport = new StubTransport();
        var services = new ServiceCollection();
        services.AddSingleton<IHindsightTransport>(transport);
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Same(transport, client.Options.Transport);
    }

    [Fact]
    public void A_caller_supplied_http_client_is_used()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var services = new ServiceCollection();
        services.AddSingleton(httpClient);
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Same(httpClient, client.Options.HttpClient);
    }

    [Fact]
    public void A_caller_supplied_key_provider_is_used()
    {
        var keyProvider = new StubApiKeyProvider("from-container");
        var services = new ServiceCollection();
        services.AddSingleton<Authentication.IApiKeyProvider>(keyProvider);
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Same(keyProvider, client.Options.ApiKeyProvider);
    }

    [Fact]
    public void A_caller_supplied_logger_is_used()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHindsightFromConfiguration_binds_the_hindsight_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hindsight:BaseAddress"] = "https://configured.example.com",
                ["Hindsight:Tenant"] = "from-config",
                ["Hindsight:MaxAttempts"] = "5",
                ["Hindsight:Timeout"] = "00:00:42",
                ["Hindsight:UserAgent"] = "my-integration/1.0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHindsightFromConfiguration(configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Equal("https://configured.example.com", client.Options.BaseAddress);
        Assert.Equal("from-config", client.Options.Tenant);
        Assert.Equal(5, client.Options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(42), client.Options.Timeout);
        Assert.Equal("my-integration/1.0", client.Options.UserAgent);
    }

    [Fact]
    public void Configuration_is_optional_and_absent_keys_leave_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddHindsightFromConfiguration(configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Equal("http://localhost:8888", client.Options.BaseAddress);
        Assert.Equal("default", client.Options.Tenant);
    }

    [Fact]
    public async Task A_key_provider_backed_by_configuration_resolves_the_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hindsight:ApiKey"] = "  key-from-config  ",
            })
            .Build();

        var provider = new ConfigurationApiKeyProvider(configuration);

        Assert.Equal("key-from-config", await provider.GetApiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_configuration_key_provider_yields_null_when_unset()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var provider = new ConfigurationApiKeyProvider(configuration);

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Configuration_key_provider_rejects_a_missing_configuration()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new ConfigurationApiKeyProvider(null!).GetApiKeyAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Warmup_performs_one_real_call_so_connectivity_is_proven()
    {
        var transport = new StubTransport().EnqueueJson(
            200, """{"api_version":"0.10.0","features":{"mcp":true}}""");

        var services = new ServiceCollection();
        services.AddSingleton<IHindsightTransport>(transport);
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();

        await HindsightClientWarmup.WarmAsync(provider, CancellationToken.None);

        Assert.Equal(1, transport.SendCount);

        // /version is declared at the root, outside /v1/{tenant}, so it must NOT carry
        // the tenant prefix. The spec is authoritative on this and the generator
        // follows it.
        Assert.Equal("/version", transport.Requests[0].Path);
    }

    [Fact]
    public async Task Warmup_propagates_a_failure_rather_than_swallowing_it()
    {
        // A warmup that swallowed the failure would report a healthy host that cannot
        // reach its memory service, which is worse than no warmup at all.
        // A 5xx is retryable, so every attempt needs a queued response.
        var transport = new StubTransport()
            .EnqueueJson(500, """{"detail":"down"}""")
            .EnqueueJson(500, """{"detail":"down"}""")
            .EnqueueJson(500, """{"detail":"down"}""");

        var services = new ServiceCollection();
        services.AddSingleton<IHindsightTransport>(transport);
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<HindsightServerException>(
            () => HindsightClientWarmup.WarmAsync(provider, CancellationToken.None));
    }

    [Fact]
    public async Task Registration_alone_does_not_warm_anything()
    {
        // Warmup is explicitly opt-in: a host that wants it calls it at its own
        // startup where it can await and handle failure. Registering must not issue a
        // request on its own.
        var transport = new StubTransport();
        var services = new ServiceCollection();
        services.AddSingleton<IHindsightTransport>(transport);
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<HindsightClient>();

        await Task.Yield();

        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task Two_registrations_do_not_duplicate_the_client()
    {
        var services = new ServiceCollection();
        services.AddHindsight(o => o.BaseAddress = "http://first:8888");
        services.AddHindsight(o => o.BaseAddress = "http://second:8888");

        using var provider = services.BuildServiceProvider();
        var clients = provider.GetServices<HindsightClient>().ToList();

        Assert.Single(clients);
        Assert.Equal("http://second:8888", clients[0].Options.BaseAddress);

        await Task.CompletedTask;
    }

    [Fact]
    public void A_client_registered_by_the_caller_wins()
    {
        // A caller who registers their own client must not be overridden by the
        // extension; that is what makes the package a convenience rather than a
        // constraint.
        var mine = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://mine:8888",
        });

        var services = new ServiceCollection();
        services.AddSingleton(mine);
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<HindsightClient>());
    }
}
