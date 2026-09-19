using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk.Authentication;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.Reranking;
using Vectorize.Hindsight.Sdk.Reranking.Providers;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;

namespace Vectorize.Hindsight.Sdk.DependencyInjection;

/// <summary>
/// Registers a <see cref="HindsightClient"/> with an <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every service default here is added with <c>TryAdd</c> semantics, so a
/// registration the host made itself always wins. Optional collaborators —
/// <see cref="HttpClient"/>, <see cref="IHindsightTransport"/>,
/// <see cref="IApiKeyProvider"/> and <see cref="ILogger{T}"/> — are resolved with
/// <c>GetService</c> and used when present, never replaced. The client's options
/// are composed from the container, so a collaborator the host registered is the
/// one that ships on the wire; nothing here writes a null over a value the
/// caller supplied.
/// </para>
/// <para>
/// Option callbacks accumulate. Calling <see cref="AddHindsight(IServiceCollection, Action{HindsightClientOptions})"/>
/// more than once, or mixing it with
/// <see cref="AddHindsightFromConfiguration(IServiceCollection, IConfiguration)"/>,
/// applies every callback in registration order when the client is first
/// resolved — so a later call refines an earlier one instead of being silently
/// discarded, and a configuration section read after an explicit callback
/// overrides it. Within one key the last writer wins.
/// </para>
/// <para>
/// No method on this type performs I/O, and the client's constructor performs
/// none either, so resolving the client cannot fail on a network problem.
/// Connectivity is verified by the host, at a point where it can await and
/// handle the failure, through <see cref="HindsightClientWarmup"/>.
/// </para>
/// </remarks>
public static class HindsightServiceCollectionExtensions
{
    /// <summary>
    /// The configuration section <see cref="AddHindsightFromConfiguration(IServiceCollection, IConfiguration)"/>
    /// binds when no section name is given.
    /// </summary>
    public const string DefaultConfigurationSectionName = "Hindsight";

    /// <summary>
    /// The key, relative to the bound section, that the API key is read from.
    /// </summary>
    public const string DefaultApiKeyConfigurationKey = "ApiKey";

    /// <summary>
    /// Registers <see cref="HindsightClient"/> as a singleton.
    /// </summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configure">
    /// Applied to the effective options when the client is first resolved;
    /// callbacks from earlier or later calls are applied too, in registration
    /// order. Null contributes nothing.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// This overload reads no configuration. The API key therefore comes from
    /// <see cref="HindsightClientOptions.ApiKey"/> or
    /// <see cref="HindsightClientOptions.ApiKeyProvider"/> as the callback sets
    /// them, from an <see cref="IApiKeyProvider"/> the host registered, or from
    /// the <c>HINDSIGHT_API_KEY</c> environment variable as a last resort. Use
    /// <see cref="AddHindsightFromConfiguration(IServiceCollection, IConfiguration)"/>
    /// when the base address or the key should come from configuration.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddHindsight(
        this IServiceCollection services,
        Action<HindsightClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The effective options are a singleton, so a host can inject them to log
        // or assert on, and so the client and the host observe one instance.
        services.TryAddSingleton(_ => new HindsightClientOptions());

        // The environment provider is the seam default, registered under its own
        // type. The configuration overload adds an IApiKeyProvider chain that
        // outranks it, so either overload resolves the same way in any order.
        services.TryAddSingleton<EnvironmentApiKeyProvider>();

        // Deliberately NOT registering a default reranker factory here.
        //
        // Registering the identity factory eagerly looked tidy and was a real bug: every provider
        // registration uses TryAdd, so the first one wins, and a host that called
        // `AddHindsight()` before `AddTypeSafeReranker()` would silently keep the no-op default and
        // never rank anything. The absence of a registration is the default instead, and
        // ResolveReranker falls back to identity when none is present — so a provider registered in
        // any order wins, and a host with no provider still gets a reranker rather than null.
        services.TryAddSingleton<HindsightClient>(CreateClient);

        if (configure is not null)
        {
            Registration(services).Add(configure);
        }

        return services;
    }

    /// <summary>
    /// Binds the <c>Hindsight</c> configuration section and registers
    /// <see cref="HindsightClient"/> as a singleton.
    /// </summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">The configuration to bind from.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    /// <remarks>
    /// Values are read when the client is first resolved, not when this method is
    /// called, so a reloadable configuration is honoured. The API key is
    /// deliberately <b>not</b> copied into the options: see
    /// <see cref="AddHindsightFromConfiguration(IServiceCollection, IConfiguration, string, Action{HindsightClientOptions})"/>
    /// for the ladder that is used instead.
    /// </remarks>
    public static IServiceCollection AddHindsightFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration) =>
        AddHindsightFromConfiguration(services, configuration, DefaultConfigurationSectionName, configure: null);

    /// <summary>
    /// Binds a named configuration section and registers <see cref="HindsightClient"/>
    /// as a singleton.
    /// </summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">The configuration to bind from.</param>
    /// <param name="sectionName">The section to bind, e.g. <c>Hindsight</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sectionName"/> is null, empty or blank.</exception>
    public static IServiceCollection AddHindsightFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName) =>
        AddHindsightFromConfiguration(services, configuration, sectionName, configure: null);

    /// <summary>
    /// Binds a named configuration section, applies a callback after it, and
    /// registers <see cref="HindsightClient"/> as a singleton.
    /// </summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">The configuration to bind from.</param>
    /// <param name="sectionName">The section to bind, e.g. <c>Hindsight</c>.</param>
    /// <param name="configure">
    /// Applied after the section, so code can override a value that came from a
    /// file. Null contributes nothing.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// <para>Recognised keys, all optional and all read case-insensitively:</para>
    /// <list type="bullet">
    /// <item><description><c>BaseAddress</c> — an absolute http or https URI.</description></item>
    /// <item><description><c>Tenant</c> — the <c>/v1/{tenant}</c> segment.</description></item>
    /// <item><description><c>MaxAttempts</c> — an integer, at least 1.</description></item>
    /// <item><description><c>InitialRetryDelay</c>, <c>MaxRetryDelay</c>, <c>MaxRetryAfter</c>, <c>Timeout</c> — a <see cref="TimeSpan"/>, in <c>hh:mm:ss</c> form or <c>00:00:00.500</c> for sub-second values.</description></item>
    /// <item><description><c>RetryJitterFraction</c> — a number between 0 and 1.</description></item>
    /// <item><description><c>UserAgent</c> — the <c>User-Agent</c> header.</description></item>
    /// <item><description><c>ApiKey</c> — read as a credential source, never copied into the options. See the note on the key below.</description></item>
    /// </list>
    /// <para>
    /// A recognised key that is present but unparseable is a configuration error:
    /// resolution throws <see cref="HindsightConfigurationException"/> naming the
    /// key, rather than leaving a default in place and appearing to work.
    /// </para>
    /// <para>
    /// <b>The API key.</b> A secret in a file that is routinely committed — an
    /// <c>appsettings.json</c> — is a bad default, and a key copied into an
    /// options property is a plain string that any logger or options dump may
    /// render. This package therefore never assigns <c>ApiKey</c> from
    /// configuration. It registers an <see cref="IApiKeyProvider"/> that reads
    /// <c>{section}:ApiKey</c> at request time, chained after
    /// <see cref="EnvironmentApiKeyProvider"/> so an <c>HINDSIGHT_API_KEY</c>
    /// environment variable outranks the file. The key can still come from
    /// user-secrets, an environment variable, or any configuration provider a
    /// secret store plugs in as, and the host can replace the whole ladder by
    /// registering its own <see cref="IApiKeyProvider"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sectionName"/> is null, empty or blank.</exception>
    /// <exception cref="HindsightConfigurationException">
    /// A recognised key is present but cannot be parsed as its declared type.
    /// </exception>
    public static IServiceCollection AddHindsightFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName,
        Action<HindsightClientOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        var section = configuration.GetSection(sectionName);
        var apiKeyKey = $"{sectionName}:{DefaultApiKeyConfigurationKey}";

        // Registered before the client, so the singleton factory finds it: the
        // environment variable first, because process-level deployment settings
        // outrank a file, then the configuration value. A provider the host
        // registered is left alone, because TryAdd keeps it.
        services.TryAddSingleton<IApiKeyProvider>(_ => new ChainedApiKeyProvider(
            new EnvironmentApiKeyProvider(),
            new ConfigurationApiKeyProvider(configuration, apiKeyKey)));

        return services.AddHindsight(options =>
        {
            BindSection(section, options);
            configure?.Invoke(options);
        });
    }

    /// <summary>Builds the client from the container, so registered seams are used.</summary>
    /// <param name="provider">The resolving container.</param>
    /// <returns>The singleton client.</returns>
    private static HindsightClient CreateClient(IServiceProvider provider)
    {
        var options = provider.GetService<HindsightClientOptions>() ?? new HindsightClientOptions();

        // Every callback registered by any AddHindsight* call, in the order those
        // calls were made, applied to one options instance.
        provider.GetService<HindsightRegistration>()?.Apply(options);

        // Filled only when unset. A host that registered an HttpClient,
        // IHindsightTransport or IApiKeyProvider gets that one; a host that
        // registered none gets no substitute and keeps the library's default.
        options.HttpClient ??= provider.GetService<HttpClient>();
        options.Transport ??= provider.GetService<IHindsightTransport>();
        options.ApiKeyProvider ??= provider.GetService<IApiKeyProvider>();

        if (options.ApiKeyProvider is null && string.IsNullOrEmpty(options.ApiKey))
        {
            // Last resort, and only when nothing above supplied a credential: the
            // variable the vendor's other clients honour. A deployment that needs
            // no key is unaffected, because the provider reports null.
            options.ApiKeyProvider = provider.GetService<EnvironmentApiKeyProvider>();
        }

        // The reranker is resolved from whichever IRerankerFactory is registered, and this never
        // returns null: with no provider registered it is the identity reranker, which preserves
        // the merge order. So a client built here always has a reranker and no call site or test
        // has to handle a nullable one — the same shape as `new HindsightClient(null, null, null)`.
        var reranker = provider.ResolveReranker();

        return new HindsightClient(options, provider.GetService<ILogger<HindsightClient>>(), reranker);
    }

    /// <summary>
    /// Returns the per-container callback list, creating it on the first call.
    /// </summary>
    /// <param name="services">The collection being configured.</param>
    /// <returns>The shared registration, so repeated calls accumulate onto it.</returns>
    /// <remarks>
    /// A callback appended to a second, separate instance would never be resolved,
    /// which is why the existing instance is recovered from the collection rather
    /// than created again.
    /// </remarks>
    private static HindsightRegistration Registration(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(HindsightRegistration)
                && descriptor.ImplementationInstance is HindsightRegistration existing)
            {
                return existing;
            }
        }

        var created = new HindsightRegistration();
        services.TryAddSingleton(created);

        return created;
    }

    /// <summary>Reads the recognised keys of a section onto options.</summary>
    /// <param name="section">The bound section.</param>
    /// <param name="options">The options to apply to, in place.</param>
    /// <exception cref="HindsightConfigurationException">A present key is unparseable.</exception>
    private static void BindSection(IConfigurationSection section, HindsightClientOptions options)
    {
        ApplyString(section, nameof(HindsightClientOptions.BaseAddress), value => options.BaseAddress = value);
        ApplyString(section, nameof(HindsightClientOptions.Tenant), value => options.Tenant = value);
        ApplyString(section, nameof(HindsightClientOptions.UserAgent), value => options.UserAgent = value);

        ApplyInt(section, nameof(HindsightClientOptions.MaxAttempts), value => options.MaxAttempts = value);
        ApplyDouble(section, nameof(HindsightClientOptions.RetryJitterFraction), value => options.RetryJitterFraction = value);

        ApplyTimeSpan(section, nameof(HindsightClientOptions.InitialRetryDelay), value => options.InitialRetryDelay = value);
        ApplyTimeSpan(section, nameof(HindsightClientOptions.MaxRetryDelay), value => options.MaxRetryDelay = value);
        ApplyTimeSpan(section, nameof(HindsightClientOptions.MaxRetryAfter), value => options.MaxRetryAfter = value);
        ApplyTimeSpan(section, nameof(HindsightClientOptions.Timeout), value => options.Timeout = value);
    }

    /// <summary>Applies a string key, ignoring one that is absent or blank.</summary>
    /// <param name="section">The bound section.</param>
    /// <param name="key">The key, relative to the section.</param>
    /// <param name="apply">Receives the trimmed value.</param>
    private static void ApplyString(IConfigurationSection section, string key, Action<string> apply)
    {
        var text = section[key];
        if (!string.IsNullOrWhiteSpace(text))
        {
            apply(text.Trim());
        }
    }

    /// <summary>Applies an integer key. A present but unparseable value is an error.</summary>
    /// <param name="section">The bound section.</param>
    /// <param name="key">The key, relative to the section.</param>
    /// <param name="apply">Receives the parsed value.</param>
    /// <exception cref="HindsightConfigurationException">The value is unparseable.</exception>
    private static void ApplyInt(IConfigurationSection section, string key, Action<int> apply)
    {
        var text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw InvalidValue(section, key, text, "an integer");
        }

        apply(value);
    }

    /// <summary>Applies a fractional key. A present but unparseable value is an error.</summary>
    /// <param name="section">The bound section.</param>
    /// <param name="key">The key, relative to the section.</param>
    /// <param name="apply">Receives the parsed value.</param>
    /// <exception cref="HindsightConfigurationException">The value is unparseable.</exception>
    private static void ApplyDouble(IConfigurationSection section, string key, Action<double> apply)
    {
        var text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw InvalidValue(section, key, text, "a number");
        }

        apply(value);
    }

    /// <summary>Applies a duration key. A present but unparseable value is an error.</summary>
    /// <param name="section">The bound section.</param>
    /// <param name="key">The key, relative to the section.</param>
    /// <param name="apply">Receives the parsed value.</param>
    /// <exception cref="HindsightConfigurationException">The value is unparseable.</exception>
    private static void ApplyTimeSpan(IConfigurationSection section, string key, Action<TimeSpan> apply)
    {
        var text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
        {
            throw InvalidValue(section, key, text, "a duration such as 00:00:05 or 00:00:00.500");
        }

        apply(value);
    }

    /// <summary>Describes a malformed configuration value, naming its full path.</summary>
    /// <param name="section">The section the key lives in.</param>
    /// <param name="key">The key, relative to the section.</param>
    /// <param name="value">The offending value.</param>
    /// <param name="expected">What the value would have to look like to be accepted.</param>
    /// <returns>The exception to throw.</returns>
    private static HindsightConfigurationException InvalidValue(
        IConfigurationSection section,
        string key,
        string value,
        string expected)
    {
        var message =
            $"Configuration key '{section.Path}:{key}' has value '{value}', which is not {expected}.";

        return new HindsightConfigurationException(message);
    }

    /// <summary>
    /// The option callbacks collected across every <c>AddHindsight</c> call on one
    /// collection, applied in registration order.
    /// </summary>
    private sealed class HindsightRegistration
    {
        private readonly List<Action<HindsightClientOptions>> _configure = [];

        /// <summary>Appends a callback.</summary>
        /// <param name="configure">The callback, invoked when the client is resolved.</param>
        internal void Add(Action<HindsightClientOptions> configure) => _configure.Add(configure);

        /// <summary>Applies every callback, in order, to one options instance.</summary>
        /// <param name="options">The options about to be handed to the client.</param>
        internal void Apply(HindsightClientOptions options)
        {
            foreach (var configure in _configure)
            {
                configure(options);
            }
        }
    }
}
