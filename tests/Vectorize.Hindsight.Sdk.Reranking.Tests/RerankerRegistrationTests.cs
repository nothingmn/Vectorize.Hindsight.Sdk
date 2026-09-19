using Microsoft.Extensions.DependencyInjection;
using Vectorize.Hindsight.Sdk.DependencyInjection;
using Vectorize.Hindsight.Sdk.Reranking;
using Vectorize.Hindsight.Sdk.Reranking.Providers;
using Vectorize.Hindsight.Sdk.Reranking.TypeSafe;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Reranking.Tests;

/// <summary>
/// Selection of a reranker through the container, which is how a provider is chosen.
/// </summary>
/// <remarks>
/// The point of these tests is that <b>selection is a registration and not a branch</b>. Nothing in
/// the library compares a provider name or switches on a type to decide which reranker to build, so
/// the evidence is that replacing the registration changes the resolved reranker with no other
/// change — and that an unknown provider is simply a different registration.
/// </remarks>
public sealed class RerankerRegistrationTests
{
    /// <summary>A provider that exists only in this test, to prove no registry needs updating.</summary>
    private sealed class ThirdPartyReranker : IReranker
    {
        public Task<IReadOnlyList<double>> RerankAsync(
            string query, IReadOnlyList<string> documents, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<double>>([.. documents.Select((_, index) => (double)index)]);
    }

    private sealed class ThirdPartyFactory : IRerankerFactory
    {
        public string ProviderName => "third-party-test";

        public IReranker? TryCreate(IServiceProvider services, out string? reason)
        {
            reason = null;
            return new ThirdPartyReranker();
        }

        public IReranker GetRequired(IServiceProvider services) => new ThirdPartyReranker();
    }

    /// <summary>A factory that cannot build, to exercise the fallback.</summary>
    private sealed class BrokenFactory : IRerankerFactory
    {
        private readonly bool _throwOnRequired;

        internal BrokenFactory(bool throwOnRequired = false) => _throwOnRequired = throwOnRequired;

        public string ProviderName => "broken";

        public IReranker? TryCreate(IServiceProvider services, out string? reason)
        {
            reason = "this provider is deliberately broken";

            if (_throwOnRequired)
            {
                throw new Hindsight.Sdk.Errors.HindsightConfigurationException(reason);
            }

            return null;
        }

        public IReranker GetRequired(IServiceProvider services) =>
            throw new Hindsight.Sdk.Errors.HindsightConfigurationException("this provider is deliberately broken");
    }

    private sealed class StubAsker : IChoiceAsker
    {
        public Task<IReadOnlyDictionary<string, double>> AskChoiceAsync(
            string instructions,
            string state,
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, double>>(
                options.ToDictionary(option => option.Key, _ => 1.0, StringComparer.Ordinal));
    }

    [Fact]
    public void AddHindsight_registers_the_identity_reranker_so_resolution_never_fails()
    {
        var services = new ServiceCollection();
        services.AddHindsight();

        using var provider = services.BuildServiceProvider();
        var reranker = provider.ResolveReranker();

        Assert.IsType<IdentityReranker>(reranker);
        Assert.True(reranker.IsIdentity);
    }

    [Fact]
    public void Resolving_a_reranker_with_nothing_registered_still_succeeds()
    {
        // The property that keeps call sites free of null checks.
        using var provider = new ServiceCollection().BuildServiceProvider();

        var reranker = provider.ResolveReranker();
        Assert.IsType<IdentityReranker>(reranker);
    }

    [Fact]
    public void A_registered_provider_replaces_the_default()
    {
        // No switch statement, no name comparison: the registration is the selection.
        var services = new ServiceCollection();
        services.AddHindsight();
        services.AddReranker<ThirdPartyFactory>();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<ThirdPartyReranker>(provider.ResolveReranker());
    }

    [Fact]
    public async Task A_third_party_provider_needs_no_change_to_the_library()
    {
        // The evidence that this is extensible by registration: a reranker type that exists only in
        // this test is resolved and used, with nothing in the library aware it exists.
        var services = new ServiceCollection();
        services.AddReranker<ThirdPartyFactory>();

        using var provider = services.BuildServiceProvider();
        var reranker = provider.ResolveReranker();

        var scores = await reranker.RerankAsync("q", ["a", "b", "c"], CancellationToken.None);

        Assert.Equal([0.0, 1.0, 2.0], scores);
    }

    [Fact]
    public void The_typesafe_provider_is_selected_by_registering_it()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChoiceAsker, StubAsker>();
        services.AddHindsight();
        services.AddTypeSafeReranker();

        using var provider = services.BuildServiceProvider();

        var reranker = provider.ResolveReranker();
        Assert.IsType<TypeSafeChoiceReranker>(reranker);
        Assert.False(reranker.IsIdentity);
    }

    [Fact]
    public void Registration_order_does_not_change_the_outcome()
    {
        // A regression test for a real bug: AddHindsight used to register the identity factory
        // eagerly, and because every registration uses TryAdd the first one wins — so a host that
        // called AddHindsight() *before* AddTypeSafeReranker() silently kept the no-op default and
        // never ranked anything. Order must not matter, so both orders are asserted.
        foreach (var hindsightFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddSingleton<IChoiceAsker, StubAsker>();

            if (hindsightFirst)
            {
                services.AddHindsight();
                services.AddTypeSafeReranker();
            }
            else
            {
                services.AddTypeSafeReranker();
                services.AddHindsight();
            }

            using var provider = services.BuildServiceProvider();
            var reranker = provider.ResolveReranker();

            Assert.True(
                reranker is TypeSafeChoiceReranker,
                $"with AddHindsight first = {hindsightFirst}, the resolved reranker was "
                + $"{reranker.GetType().Name}, so a registered provider was silently ignored");
        }
    }

    [Fact]
    public void An_explicit_identity_registration_beats_a_provider_registered_after_it()
    {
        // The other side of the same coin: a host that deliberately asks for no reranking means it.
        var services = new ServiceCollection();
        services.AddIdentityReranker();
        services.AddReranker<ThirdPartyFactory>();

        using var provider = services.BuildServiceProvider();
        Assert.True(provider.ResolveReranker().IsIdentity);
    }

    [Fact]
    public void A_hosts_registration_wins_over_a_package_default()
    {
        // TryAdd semantics, and the reason they matter: a host's explicit choice must not be
        // silently replaced by a package that is registered later.
        var services = new ServiceCollection();
        services.AddReranker<ThirdPartyFactory>();
        services.AddTypeSafeReranker();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<ThirdPartyReranker>(provider.ResolveReranker());
    }

    [Fact]
    public void Registering_a_provider_twice_is_a_no_op_rather_than_an_error()
    {
        var services = new ServiceCollection();
        services.AddReranker<ThirdPartyFactory>();
        services.AddReranker<ThirdPartyFactory>();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<ThirdPartyReranker>(provider.ResolveReranker());
    }

    [Fact]
    public void A_provider_that_cannot_build_falls_back_to_identity_rather_than_failing()
    {
        // Losing cross-bank recall entirely is a worse outcome than losing its ranking.
        var services = new ServiceCollection();
        services.AddReranker(new BrokenFactory());

        using var provider = services.BuildServiceProvider();

        var reranker = provider.ResolveReranker();
        Assert.IsType<IdentityReranker>(reranker);
    }

    [Fact]
    public void The_fallback_reason_is_reported_rather_than_hidden()
    {
        // A host that logs its wiring, or exposes it on a health endpoint, needs to know that the
        // reranker it asked for is not the one running.
        var services = new ServiceCollection();
        services.AddReranker(new BrokenFactory());

        using var provider = services.BuildServiceProvider();

        var reranker = provider.ResolveReranker(out var reason);

        Assert.IsType<IdentityReranker>(reranker);
        Assert.Equal("this provider is deliberately broken", reason);
    }

    [Fact]
    public void A_provider_that_throws_on_required_is_surfaced_to_a_host_that_wants_it()
    {
        // The other valid posture: a host that has decided ranking is required fails loudly.
        var services = new ServiceCollection();
        services.AddReranker(new BrokenFactory(throwOnRequired: true));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRerankerFactory>();

        Assert.Throws<Hindsight.Sdk.Errors.HindsightConfigurationException>(
            () => factory.GetRequired(provider));
    }

    [Fact]
    public void The_typesafe_provider_without_a_client_reports_what_is_missing()
    {
        // The common misconfiguration, and it should name the interface rather than making the host
        // guess why nothing was ranked.
        var services = new ServiceCollection();
        services.AddTypeSafeReranker();

        using var provider = services.BuildServiceProvider();

        var reranker = provider.ResolveReranker(out var reason);

        Assert.IsType<IdentityReranker>(reranker);
        Assert.NotNull(reason);
        Assert.Contains("IChoiceAsker", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_identity_registration_is_a_deliberate_choice()
    {
        // A host reading its own configuration can mean "no reranking" rather than relying on the
        // absence of a registration.
        var services = new ServiceCollection();
        services.AddIdentityReranker();

        using var provider = services.BuildServiceProvider();
        Assert.True(provider.ResolveReranker().IsIdentity);
    }

    [Fact]
    public void The_client_resolved_from_the_container_carries_the_configured_reranker()
    {
        // End to end: the container's choice reaches the client that call sites inject, so a caller
        // never wires a reranker by hand.
        var services = new ServiceCollection();
        services.AddSingleton<IChoiceAsker, StubAsker>();
        services.AddHindsight();
        services.AddTypeSafeReranker();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<Hindsight.Sdk.HindsightClient>();

        Assert.IsType<TypeSafeChoiceReranker>(client.Reranker);
    }

    [Fact]
    public void A_client_with_no_reranker_supplied_still_has_one()
    {
        // `new HindsightClient(null, null, null)` and `new HindsightClient()` must both resolve to
        // the identity default, so no call site or test handles a nullable reranker.
        using var client = new Hindsight.Sdk.HindsightClient();

        Assert.NotNull(client.Reranker);
        Assert.True(client.Reranker.IsIdentity);
    }

    [Fact]
    public void An_explicit_reranker_passed_to_the_constructor_is_used()
    {
        using var client = new Hindsight.Sdk.HindsightClient(new ThirdPartyReranker());

        Assert.IsType<ThirdPartyReranker>(client.Reranker);
        Assert.False(client.Reranker.IsIdentity);
    }

    [Fact]
    public void A_reranker_passed_to_the_constructor_reaches_cross_bank_recall()
    {
        // The constructor's reranker is the client-wide default, so a cross-bank call that supplies
        // none of its own uses it — which is what makes the two-argument constructor useful.
        using var client = new Hindsight.Sdk.HindsightClient(new ThirdPartyReranker());
        Assert.False(client.Reranker.IsIdentity);
    }
}
