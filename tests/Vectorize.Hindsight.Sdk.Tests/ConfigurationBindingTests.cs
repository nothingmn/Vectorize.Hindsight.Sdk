using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vectorize.Hindsight.Sdk.DependencyInjection;
using Vectorize.Hindsight.Sdk.Errors;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Configuration binding, including the values that must be rejected.
/// </summary>
/// <remarks>
/// A malformed configuration value is the kind of failure that surfaces in a
/// deployment and not in a test, because a host that reads a typo'd duration binds
/// something unusable and fails at the first request. Every rejection path is
/// asserted here so a bad value fails at startup with a message that names the key.
/// </remarks>
public sealed class ConfigurationBindingTests
{
    private static IConfiguration Build(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private static HindsightClient Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddHindsightFromConfiguration(configuration);
        return services.BuildServiceProvider().GetRequiredService<HindsightClient>();
    }

    [Fact]
    public void A_named_section_overload_binds_that_section()
    {
        // The four-argument overload defaults to the "Hindsight" section name.
        var configuration = Build(("Custom:BaseAddress", "https://custom.example.com"));

        var services = new ServiceCollection();
        services.AddHindsightFromConfiguration(configuration, "Custom");

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Equal("https://custom.example.com", client.Options.BaseAddress);
    }

    [Theory]
    [InlineData("MaxAttempts", "not-a-number")]
    [InlineData("MaxAttempts", "1.5")]
    public void A_non_integer_attempt_count_is_rejected_and_names_the_key(string key, string value)
    {
        var configuration = Build(($"Hindsight:{key}", value));

        var exception = Assert.Throws<HindsightConfigurationException>(() => Resolve(configuration));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains(value, exception.Message, StringComparison.Ordinal);
        Assert.Contains("an integer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_jitter_fraction_is_rejected()
    {
        var configuration = Build(("Hindsight:RetryJitterFraction", "half"));

        var exception = Assert.Throws<HindsightConfigurationException>(() => Resolve(configuration));

        Assert.Contains("RetryJitterFraction", exception.Message, StringComparison.Ordinal);
        Assert.Contains("a number", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fractional_jitter_fraction_is_accepted()
    {
        var configuration = Build(("Hindsight:RetryJitterFraction", "0.5"));

        var client = Resolve(configuration);

        Assert.Equal(0.5, client.Options.RetryJitterFraction);
    }

    [Theory]
    [InlineData("Timeout")]
    [InlineData("InitialRetryDelay")]
    [InlineData("MaxRetryDelay")]
    [InlineData("MaxRetryAfter")]
    public void A_non_duration_where_a_duration_is_expected_is_rejected(string key)
    {
        var configuration = Build(($"Hindsight:{key}", "soon"));

        var exception = Assert.Throws<HindsightConfigurationException>(() => Resolve(configuration));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains("a duration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Durations_are_bound_in_both_the_long_and_short_forms()
    {
        var configuration = Build(
            ("Hindsight:Timeout", "00:00:42"),
            ("Hindsight:InitialRetryDelay", "00:00:00.250"));

        var client = Resolve(configuration);

        Assert.Equal(TimeSpan.FromSeconds(42), client.Options.Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(250), client.Options.InitialRetryDelay);
    }

    [Fact]
    public void An_absent_key_leaves_its_default_untouched()
    {
        // Only keys that are present are applied, so a partial configuration is a
        // normal thing rather than an error.
        var configuration = Build(("Hindsight:Tenant", "acme"));

        var client = Resolve(configuration);

        Assert.Equal("acme", client.Options.Tenant);
        Assert.Equal(TimeSpan.FromSeconds(100), client.Options.Timeout);
        Assert.Equal(3, client.Options.MaxAttempts);
    }

    [Fact]
    public void An_empty_value_is_treated_as_absent_rather_than_as_a_string()
    {
        var configuration = Build(("Hindsight:Tenant", string.Empty));

        var client = Resolve(configuration);

        Assert.Equal("default", client.Options.Tenant);
    }

    [Fact]
    public void An_api_key_in_configuration_is_not_bound_into_the_options()
    {
        // The key is resolved through IApiKeyProvider rather than copied into the options
        // object, where an options dump could render it.
        var configuration = Build(("Hindsight:ApiKey", "should-not-appear-in-options"));

        var client = Resolve(configuration);

        Assert.Null(client.Options.ApiKey);
        Assert.NotNull(client.Options.ApiKeyProvider);
    }

    [Fact]
    public async Task The_bound_key_provider_resolves_from_configuration()
    {
        var configuration = Build(("Hindsight:ApiKey", "from-config"));
        var client = Resolve(configuration);

        var key = await client.Options.ApiKeyProvider!.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("from-config", key);
    }

    [Fact]
    public void The_configure_callback_runs_after_binding_so_it_can_override()
    {
        var configuration = Build(("Hindsight:Tenant", "from-config"));

        var services = new ServiceCollection();
        // The callback overload takes the section name, then the callback: binding
        // happens first and the callback runs after, so it can override a bound value.
        services.AddHindsightFromConfiguration(
            configuration,
            HindsightServiceCollectionExtensions.DefaultConfigurationSectionName,
            options => options.Tenant = "from-callback");

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Equal("from-callback", client.Options.Tenant);
    }

    [Fact]
    public void A_null_configuration_is_rejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => services.AddHindsightFromConfiguration(null!));
    }

    [Fact]
    public void A_blank_section_name_is_rejected()
    {
        var configuration = Build();

        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(
            () => services.AddHindsightFromConfiguration(configuration, "  "));
    }

    [Fact]
    public void A_null_service_collection_is_rejected()
    {
        var configuration = Build();

        Assert.Throws<ArgumentNullException>(
            () => HindsightServiceCollectionExtensions.AddHindsightFromConfiguration(null!, configuration));
    }

    [Fact]
    public void The_warmup_helper_rejects_a_null_service_provider()
    {
        Assert.Throws<ArgumentNullException>(
            () => HindsightClientWarmup.WarmAsync(null!, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void The_warmup_helper_rejects_a_provider_with_no_client_registered()
    {
        // Resolving a client that was never registered must fail clearly rather than
        // silently doing nothing, which would report a healthy host that has no client.
        // The package raises its own configuration exception rather than a raw
        // resolution failure, so the message tells the caller what to do.
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<HindsightConfigurationException>(
            () => HindsightClientWarmup.WarmAsync(provider, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Contains("AddHindsight", exception.Message, StringComparison.Ordinal);
    }
}
