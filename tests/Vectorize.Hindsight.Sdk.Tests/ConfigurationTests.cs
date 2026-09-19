using System.Text.Json;
using Vectorize.Hindsight.Sdk.Authentication;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.Errors;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Options validation, api-key resolution, and the retry policy as a pure function.
/// </summary>
public sealed class ConfigurationTests
{
    [Fact]
    public void Defaults_are_usable_without_any_configuration()
    {
        var options = new HindsightClientOptions();

        Assert.Equal("http://localhost:8888", options.BaseAddress);
        Assert.Equal("default", options.Tenant);
        Assert.Null(options.ApiKey);
        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.InitialRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), options.MaxRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MaxRetryAfter);
        Assert.Equal(0.25, options.RetryJitterFraction);
        Assert.Empty(options.Headers);
    }

    [Fact]
    public void A_client_constructs_with_no_options_and_no_key()
    {
        // The specification marks authorization as optional, and a default deployment
        // accepts a request without it, so requiring a key would be wrong.
        using var client = new HindsightClient();

        Assert.Equal("http://localhost:8888", client.BaseAddress);
    }

    [Fact]
    public void A_client_constructs_from_a_bare_base_address()
    {
        using var client = new HindsightClient("https://hindsight.example.com");

        Assert.Equal("https://hindsight.example.com", client.BaseAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    [InlineData("ftp://example.com")]
    public void An_unusable_base_address_fails_at_construction(string baseAddress)
    {
        // A constructor cannot await, but it can validate, and a bad address should
        // fail where the caller can still handle it rather than at first use.
        var exception = Assert.Throws<HindsightConfigurationException>(
            () => new HindsightClient(new HindsightClientOptions { BaseAddress = baseAddress }));

        Assert.Contains("BaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_tenant_fails_at_construction()
    {
        Assert.Throws<HindsightConfigurationException>(
            () => new HindsightClient(new HindsightClientOptions { Tenant = "  " }));
    }

    [Fact]
    public void MaxAttempts_below_one_fails_at_construction()
    {
        // Zero would mean "never send", which is never what a caller means.
        var exception = Assert.Throws<HindsightConfigurationException>(
            () => new HindsightClient(new HindsightClientOptions { MaxAttempts = 0 }));

        Assert.Contains("MaxAttempts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_initial_delay_fails_at_construction()
    {
        Assert.Throws<HindsightConfigurationException>(
            () => new HindsightClient(new HindsightClientOptions
            {
                InitialRetryDelay = TimeSpan.FromSeconds(-1),
            }));
    }

    [Fact]
    public void A_max_delay_below_the_initial_delay_fails_at_construction()
    {
        Assert.Throws<HindsightConfigurationException>(
            () => new HindsightClient(new HindsightClientOptions
            {
                InitialRetryDelay = TimeSpan.FromSeconds(5),
                MaxRetryDelay = TimeSpan.FromSeconds(1),
            }));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void A_jitter_fraction_outside_zero_to_one_fails_at_construction(double fraction)
    {
        Assert.Throws<HindsightConfigurationException>(
            () => new HindsightClient(new HindsightClientOptions { RetryJitterFraction = fraction }));
    }

    [Fact]
    public void Authorization_cannot_be_smuggled_in_through_headers()
    {
        // A credential must arrive through ApiKey or ApiKeyProvider, never as an
        // ordinary string that could end up in a log.
        var options = new HindsightClientOptions();
        options.Headers["Authorization"] = "Bearer sneaky";

        var exception = Assert.Throws<HindsightConfigurationException>(() => new HindsightClient(options));

        Assert.Contains("Authorization", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_json_hook_is_applied_and_then_frozen()
    {
        var applied = false;
        var options = new HindsightClientOptions
        {
            ConfigureJson = o =>
            {
                applied = true;
                o.WriteIndented = true;
            },
        };

        using var client = new HindsightClient(options);

        Assert.True(applied);
    }

    [Fact]
    public async Task Environment_api_key_provider_reads_the_documented_variable_name()
    {
        var provider = new Authentication.EnvironmentApiKeyProvider(
            "HINDSIGHT_API_KEY",
            _ => "  key-from-env  ");

        var key = await provider.GetApiKeyAsync(CancellationToken.None);

        // Trimmed: a padded value is a common accident and fails authentication with
        // no hint that whitespace is the cause.
        Assert.Equal("key-from-env", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Environment_api_key_provider_yields_null_for_a_blank_value(string? value)
    {
        var provider = new Authentication.EnvironmentApiKeyProvider("X", _ => value);

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_key_chain_returns_the_first_non_empty_value()
    {
        var provider = new Authentication.ChainedApiKeyProvider(
            new Authentication.StaticApiKeyProvider(null),
            new Authentication.StaticApiKeyProvider(""),
            new Authentication.StaticApiKeyProvider("second"),
            new Authentication.StaticApiKeyProvider("third"));

        Assert.Equal("second", await provider.GetApiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_key_chain_yields_null_when_nothing_resolves()
    {
        var provider = new Authentication.ChainedApiKeyProvider(
            new Authentication.StaticApiKeyProvider(null),
            new Authentication.StaticApiKeyProvider(null));

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_key_chain_is_not_an_error()
    {
        var provider = new Authentication.ChainedApiKeyProvider();

        Assert.Null(await provider.GetApiKeyAsync(CancellationToken.None));
    }

    // --- Retry policy as a pure function ------------------------------------

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    [InlineData(422, false)]
    public void Retry_classification_matches_the_documented_set(int status, bool expected)
    {
        // 408, 429 and every 5xx. The whole 5xx range is taken rather than naming 503
        // and 529 individually: retrying only the named ones would leave 500, 502 and
        // 504 un-retried, which is how a transient outage becomes a caller-visible one.
        Assert.Equal(expected, Http.HindsightRetryPolicy.IsRetryableStatus(status));
    }

    [Fact]
    public void Backoff_is_exponential_and_bounded()
    {
        var options = new HindsightClientOptions
        {
            InitialRetryDelay = TimeSpan.FromMilliseconds(500),
            MaxRetryDelay = TimeSpan.FromSeconds(5),
            RetryJitterFraction = 0,
        };

        // No jitter, so the pure sequence is visible.
        Assert.Equal(TimeSpan.FromMilliseconds(500), Delay(1, options));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), Delay(2, options));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), Delay(3, options));
        Assert.Equal(TimeSpan.FromMilliseconds(4000), Delay(4, options));
        Assert.Equal(TimeSpan.FromSeconds(5), Delay(5, options));   // clamped
        Assert.Equal(TimeSpan.FromSeconds(5), Delay(10, options));  // stays clamped
    }

    [Fact]
    public void A_server_supplied_delay_wins_over_the_computed_backoff()
    {
        var options = new HindsightClientOptions { RetryJitterFraction = 0 };

        var delay = Http.HindsightRetryPolicy.ComputeDelay(
            1, TimeSpan.FromSeconds(7), options, () => 0.0);

        Assert.Equal(TimeSpan.FromSeconds(7), delay);
    }

    [Fact]
    public void A_server_delay_longer_than_the_ceiling_is_discarded()
    {
        // A caller waiting minutes inside one call would rather fail and retry at its
        // own level, so an unreasonable server delay falls back to the computed one.
        var options = new HindsightClientOptions
        {
            RetryJitterFraction = 0,
            MaxRetryAfter = TimeSpan.FromSeconds(10),
            InitialRetryDelay = TimeSpan.FromMilliseconds(500),
        };

        var delay = Http.HindsightRetryPolicy.ComputeDelay(
            1, TimeSpan.FromMinutes(5), options, () => 0.0);

        Assert.Equal(TimeSpan.FromMilliseconds(500), delay);
    }

    [Fact]
    public void Jitter_is_subtractive_rather_than_full()
    {
        // Subtractive: each delay is reduced by a fraction no greater than the
        // configured amount, so it never collapses to zero. Full jitter would replace
        // the delay with a value from zero and could discard a deliberate backoff.
        var options = new HindsightClientOptions
        {
            RetryJitterFraction = 0.25,
            InitialRetryDelay = TimeSpan.FromSeconds(1),
        };

        var noJitter = Http.HindsightRetryPolicy.ComputeDelay(1, null, options, () => 0.0);
        var fullJitter = Http.HindsightRetryPolicy.ComputeDelay(1, null, options, () => 1.0);

        Assert.Equal(TimeSpan.FromSeconds(1), noJitter);
        Assert.Equal(TimeSpan.FromMilliseconds(750), fullJitter);
        Assert.True(fullJitter > TimeSpan.Zero);
    }

    [Fact]
    public void Retry_after_is_read_from_the_delta_seconds_form()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Retry-After"] = "30",
        };

        Assert.Equal(TimeSpan.FromSeconds(30), Http.HindsightRetryPolicy.ReadRetryAfter(headers));
    }

    [Fact]
    public void Retry_after_is_read_from_the_millisecond_form()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["retry-after-ms"] = "1500",
        };

        Assert.Equal(TimeSpan.FromMilliseconds(1500), Http.HindsightRetryPolicy.ReadRetryAfter(headers));
    }

    [Fact]
    public void An_http_date_retry_after_is_ignored_rather_than_misread()
    {
        // The HTTP-date form is legal but rare. Parsing it as a number would yield a
        // nonsense delay, so it is ignored and the caller's own backoff used.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Retry-After"] = "Wed, 21 Oct 2026 07:28:00 GMT",
        };

        Assert.Null(Http.HindsightRetryPolicy.ReadRetryAfter(headers));
    }

    [Fact]
    public void A_negative_retry_after_is_ignored()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Retry-After"] = "-5",
        };

        Assert.Null(Http.HindsightRetryPolicy.ReadRetryAfter(headers));
    }

    private static TimeSpan Delay(int attempt, HindsightClientOptions options) =>
        Http.HindsightRetryPolicy.ComputeDelay(attempt, null, options, () => 0.0);
}
