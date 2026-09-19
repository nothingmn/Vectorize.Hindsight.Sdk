using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.DependencyInjection;
using Vectorize.Hindsight.Sdk.Reranking;
using Vectorize.Hindsight.Sdk.Reranking.Onnx;
using Vectorize.Hindsight.Sdk.Reranking.Providers;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Reranking.Tests;

/// <summary>
/// The ONNX provider's factory and registration.
/// </summary>
/// <remarks>
/// Every branch here runs <b>without</b> the model assets, by pointing the factory at paths that do
/// not exist. That is deliberate: the failure paths are the ones a host actually hits — a wrong path
/// in configuration, a model that was never downloaded — and they should be covered on every machine
/// rather than only where the multi-hundred-megabyte download happened.
/// </remarks>
public sealed class OnnxRerankerFactoryTests
{
    private static OnnxRerankerOptions MissingModelOptions() => new()
    {
        ModelPath = "/nonexistent/model_int8.onnx",
        TokenizerJsonPath = "/nonexistent/tokenizer.json",
        SentencePieceModelPath = "/nonexistent/sentencepiece.bpe.model",
    };

    [Fact]
    public void A_factory_with_a_missing_model_reports_a_reason_rather_than_throwing()
    {
        // A host that wants cross-bank recall without a reranker must still start.
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var factory = new OnnxRerankerFactory(MissingModelOptions());

        var reranker = factory.TryCreate(provider, out var reason);

        Assert.Null(reranker);
        Assert.NotNull(reason);
        Assert.Contains("HindsightConfigurationException", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_is_attempted_once_and_the_reason_is_stable()
    {
        // Reading a missing model on every recall would be wasteful, and a reason that changed
        // between calls would make a host's log misleading.
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var factory = new OnnxRerankerFactory(MissingModelOptions());

        factory.TryCreate(provider, out var first);
        factory.TryCreate(provider, out var second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Required_turns_a_missing_model_into_a_configuration_failure()
    {
        // The other valid posture: a host that has decided local ranking is required fails at first
        // use rather than silently retrieving unranked results.
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var factory = new OnnxRerankerFactory(MissingModelOptions()) { Required = true };

        var exception = Assert.Throws<HindsightConfigurationException>(
            () => factory.TryCreate(provider, out _));

        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRequired_throws_when_the_model_cannot_be_loaded()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var factory = new OnnxRerankerFactory(MissingModelOptions());

        Assert.Throws<HindsightConfigurationException>(() => factory.GetRequired(provider));
    }

    [Fact]
    public void The_provider_name_is_stable_and_not_used_for_dispatch()
    {
        // Read for diagnostics only. If a host is choosing between rerankers by comparing this, the
        // selection has leaked out of the container.
        using var factory = new OnnxRerankerFactory(MissingModelOptions());
        Assert.Equal("onnx", factory.ProviderName);
    }

    [Fact]
    public void Null_services_and_null_options_are_rejected()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var factory = new OnnxRerankerFactory(MissingModelOptions());

        Assert.Throws<ArgumentNullException>(() => factory.TryCreate(null!, out _));
        Assert.Throws<ArgumentNullException>(() => new OnnxRerankerFactory(null!));
    }

    [Fact]
    public void Disposing_a_factory_that_built_nothing_is_harmless()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var factory = new OnnxRerankerFactory(MissingModelOptions());

        factory.TryCreate(provider, out _);
        factory.Dispose();

        // Disposed twice, because a host may dispose it and so may its container.
        factory.Dispose();
    }

    [Fact]
    public void Registering_the_onnx_provider_selects_it()
    {
        // Selection is the registration, so registering this provider is the whole mechanism.
        var services = new ServiceCollection();
        services.AddOnnxReranker(MissingModelOptions());

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRerankerFactory>();

        Assert.IsType<OnnxRerankerFactory>(factory);
        Assert.Equal("onnx", factory.ProviderName);
    }

    [Fact]
    public void A_registered_onnx_provider_that_cannot_load_falls_back_to_identity()
    {
        // With the model absent, the registration is present but unusable. The host still gets a
        // reranker rather than a null, and the reason says why.
        var services = new ServiceCollection();
        services.AddOnnxReranker(MissingModelOptions());

        using var provider = services.BuildServiceProvider();

        var reranker = provider.ResolveReranker(out var reason);

        Assert.IsType<IdentityReranker>(reranker);
        Assert.NotNull(reason);
    }

    [Fact]
    public void A_required_registration_surfaces_the_failure_through_the_container()
    {
        var services = new ServiceCollection();
        services.AddOnnxReranker(MissingModelOptions(), required: true);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRerankerFactory>();

        Assert.Throws<HindsightConfigurationException>(() => factory.GetRequired(provider));
    }

    [SkippableFact]
    public void With_the_assets_present_the_factory_builds_a_working_reranker()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var provider = new ServiceCollection().BuildServiceProvider();

        var options = new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
        };

        using var factory = new OnnxRerankerFactory(options);
        var reranker = factory.TryCreate(provider, out var reason);

        Assert.NotNull(reranker);
        Assert.Null(reason);
        Assert.IsType<OnnxReranker>(reranker);
        Assert.False(reranker.IsIdentity);

        // The same instance is returned again, so the model is loaded once rather than per call.
        Assert.Same(reranker, factory.TryCreate(provider, out _));
    }

    [SkippableFact]
    public async Task With_the_assets_present_the_factory_reranker_actually_scores()
    {
        Skip.IfNot(RerankerAssets.Available, RerankerAssets.SkipReason);

        using var provider = new ServiceCollection().BuildServiceProvider();

        using var factory = new OnnxRerankerFactory(new OnnxRerankerOptions
        {
            ModelPath = RerankerAssets.ModelPath,
            TokenizerJsonPath = RerankerAssets.TokenizerPath,
            SentencePieceModelPath = RerankerAssets.SentencePiecePath,
        });

        var reranker = factory.GetRequired(provider);

        var scores = await reranker.RerankAsync(
            "when did the billing platform migrate?",
            ["The billing platform migrated to PostgreSQL in March 2026.", "The office coffee machine was replaced."],
            CancellationToken.None);

        Assert.Equal(2, scores.Count);
        Assert.True(scores[0] > scores[1]);
    }
}
