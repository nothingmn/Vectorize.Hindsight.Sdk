using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk.Authentication;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;

namespace Vectorize.Hindsight.Sdk.DependencyInjection;

/// <summary>
/// Registers a <see cref="HindsightClient"/> with an <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every default here is added with <c>TryAdd</c> semantics, so a registration the
/// host made itself wins regardless of whether it came before or after these
/// calls. Optional collaborators — <see cref="HttpClient"/>,
/// <see cref="IHindsightTransport"/>, <see cref="IApiKeyProvider"/> and
/// <see cref="ILogger{T}"/> — are resolved with <c>GetService</c> and are used
/// when present rather than replaced. The client's options are composed from the
/// container, so a collaborator the host registered is the one that ships on the
/// wire; nothing here writes a null over a value the caller supplied.
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
    /// Applied to the effective options when the client is first resolved. Null
    /// means the options are left exactly as the container supplied them.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// This overload reads no configuration. The API key therefore comes from
    /// <see cref="HindsightClientOptions.ApiKey"/> or
    /// <see cref="HindsightClientOptions.ApiKeyProvider"/> as the callback sets
    /// them, from the <c>HINDSIGHT_API_KEY</c> environment variable as a last
    /// resort, or from an <see cref="IApiKeyProvider"/> the host registered.
    /// Use <see cref="AddHindsightFromConfiguration(IServiceCollection, IConfiguration)"/>
    /// when the base address or the key should come from configuration.
    /// </remarks>
    public static IServiceCollection AddHindsight(
        this IServiceCollection services,
        Action<HindsightClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton, so a host can inject the effective options to log or assert
        // on them, and so the client and the host observe one instance.
        services.TryAddSingleton(_ => new HindsightClientOptions());

        // The environment provider is the seam default and is registered under
        // its own type. The configuration overload adds an IApiKeyProvider chain
        // that takes precedence, so calling either overload — in any order, once
        // each or twice — resolves the same way.
        services.TryAddSingleton<EnvironmentApiKeyProvider>();

        services.TryAddSingleton(provider => CreateClient(provider, configure));

        return services;
    }

    /// <summary>
    /// Binds the <c>Hindsight</c> configuration section and registers
    /// <see cref="HindsightClient"/> as a singleton.
    /// </summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">The configuration to bind from.</param>
    /// <returns>The same collection, for chaining.</returns>
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
    public static IServiceCollection AddHindsightFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName) =>
        AddHindsightFromConfiguration(services, configuration, sectionName, configure: null);

    /// <summary>
    /// Binds a named configuration section, applies a callback, and registers
    /// <see cref="HindsightClient"/> as a singleton.
    /// </summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">The configuration to bind from.</param>
    /// <param name="sectionName">The section to bind, e.g. <c>Hindsight</c>.</param>
    /// <param name="configure">
    /// Applied after the section, so code can override a value that came from a
    /// file. Resolved with the caller, never null on this overload.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// <para>Recognised keys, all optional and all read case-insensitively:</para>
    /// <list type="bullet">
    /// <item><description><c>BaseAddress</c> — absolute http or https URI.</description></item>
    /// <item><description><c>Tenant</c> — the <c>/v1/{tenant}</c> segment.</description></item>
    /// <item><description><c>MaxAttempts</c> — integer, at least 1.</description></item>
    /// <item><description><c>InitialRetryDelay</c>, <c>MaxRetryDelay</c>, <c>MaxRetryAfter</c>, <c>Timeout</c> — a <see cref="TimeSpan"/> in <c>hh:mm:ss</c> form, or <c>00:00:00.500</c> for sub-second values.</description></item>
    /// <item><description><c>RetryJitterFraction</c> — a number between 0 and 1.</description></item>
    /// <item><description><c>UserAgent</c> — the <c>User-Agent</c> header.</description></item>
    /// <item><description><c>ApiKey</c> — read as a credential source, never copied into the options. See the note on the key below.</description></item>
    /// </list>
    /// <para>
    /// A recognised key that is present but unparseable is a configuration error
    /// and throws <see cref="HindsightConfigurationException"/> naming the key,
    /// rather than being ignored and silently leaving a default in place.
    /// </para>
    /// <para>
    /// <b>The API key.</b> A secret in a file that is routinely committed — an
    /// <c>appsettings.json</c> — is a bad default, and a key copied into an
    /// options property is a plain string that any logger or options dump may
    /// render. This package therefore never assigns <c>ApiKey</c> from
    /// configuration. It registers an <see cref="IApiKeyProvider"/> that reads
    /// <c>{section}:ApiKey</c> at request time, chained after
    /// <see cref="EnvironmentApiKeyProvider"/> so an <c>HINDSIGHT_API_KEY</c>
    /// environment variable wins over the file. The key can still come from
    /// user-secrets, an environment variable, or any configuration provider a
    /// secret store plugs in as, and the host can replace the whole ladder by
    /// registering its own <see cref="IApiKeyProvider"/>.
    /// </para>
    /// </remarks>
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
        // registered first is left alone, because TryAdd keeps it.
        services.TryAddSingleton<IApiKeyProvider>(_ => new ChainedApiKeyProvider(
            new EnvironmentApiKeyProvider(),
            new ConfigurationApiKeyProvider(configuration, apiKeyKey)));

        return services.AddHindsight(options =>
        {
            BindSection(section, options);
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Builds the client from the container, so registered seams are used.
    /// </summary>
    /// <param name="provider">The resolving container.</param>
    /// <param name="configure">The caller's callback, applied before the seams are filled.</param>
    /// <returns>The singleton client.</returns>
    private static HindsightClient CreateClient(
        IServiceProvider provider,
        Action<HindsightClientOptions>? configure)
    {
        var options = provider.GetService<HindsightClientOptions>() ?? new HindsightClientOptions();

        // Applied first, so an explicit value wins over a container registration
        // and so "did the callback set a key?" can be answered below.
        configure?.Invoke(options);

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
            // no key is unaffected, because the provider returns null.
            options.ApiKeyProvider = provider.GetService<EnvironmentApiKeyProvider>();
        }

        return new HindsightClient(options, provider.GetService<ILogger<HindsightClient>>());
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
}
