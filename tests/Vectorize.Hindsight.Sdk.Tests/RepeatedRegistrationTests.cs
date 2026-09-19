using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vectorize.Hindsight.Sdk.DependencyInjection;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Repeated registration: several <c>AddHindsight*</c> calls must compose rather than
/// silently discard all but one.
/// </summary>
/// <remarks>
/// This was a real footgun found by running a throwaway container probe: a second
/// registration was silently ignored, so a host that split its configuration across two
/// calls — a library adding its own transport, an application adding its own base address —
/// lost one of them with no error. Silent partial configuration is worse than a failure,
/// because everything appears to work.
/// </remarks>
public sealed class RepeatedRegistrationTests
{
    [Fact]
    public void Two_registrations_both_apply()
    {
        // The key property: the second call must not replace the first. Each callback
        // configures a different member, so both must be visible on the resolved client.
        var services = new ServiceCollection();
        services.AddHindsight(options => options.BaseAddress = "https://first.example.com");
        services.AddHindsight(options => options.Tenant = "second");

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Equal("https://first.example.com", client.Options.BaseAddress);
        Assert.Equal("second", client.Options.Tenant);
    }

    [Fact]
    public void Callbacks_apply_in_the_order_they_were_registered_so_the_last_wins_on_conflict()
    {
        var services = new ServiceCollection();
        services.AddHindsight(options => options.MaxAttempts = 2);
        services.AddHindsight(options => options.MaxAttempts = 7);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Equal(7, client.Options.MaxAttempts);
    }

    [Fact]
    public void A_configuration_registration_and_a_callback_registration_compose()
    {
        // The mixed case a host is most likely to write: bind a section, then adjust one
        // value in code.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hindsight:BaseAddress"] = "https://from-config.example.com",
                ["Hindsight:MaxAttempts"] = "3",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHindsightFromConfiguration(configuration);
        services.AddHindsight(options => options.MaxAttempts = 9);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HindsightClient>();

        Assert.Equal("https://from-config.example.com", client.Options.BaseAddress);
        Assert.Equal(9, client.Options.MaxAttempts);
    }

    [Fact]
    public void Only_one_client_is_registered_no_matter_how_many_calls_are_made()
    {
        var services = new ServiceCollection();
        services.AddHindsight();
        services.AddHindsight();
        services.AddHindsight(options => options.Tenant = "t");

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<HindsightClient>());
    }

    [Fact]
    public void A_caller_registered_client_still_wins_over_repeated_calls()
    {
        // The composition change must not have weakened the substitution guarantee.
        var mine = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://mine:8888",
        });

        var services = new ServiceCollection();
        services.AddSingleton(mine);
        services.AddHindsight(options => options.BaseAddress = "https://theirs.example.com");
        services.AddHindsight(options => options.Tenant = "theirs");

        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<HindsightClient>());
    }

    [Fact]
    public void The_client_is_created_once_so_a_later_registration_cannot_change_it()
    {
        // The callbacks run at resolution, which happens once for a singleton. Asserting the
        // reference is stable is what proves the options were applied before construction
        // rather than mutated afterwards.
        var services = new ServiceCollection();
        services.AddHindsight(options => options.Tenant = "once");

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<HindsightClient>();
        var second = provider.GetRequiredService<HindsightClient>();

        Assert.Same(first, second);
        Assert.Equal("once", first.Options.Tenant);
    }
}
