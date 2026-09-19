using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vectorize.Hindsight.Sdk.Configuration;

namespace Vectorize.Hindsight.Sdk.Http;

/// <summary>
/// Builds and owns the frozen serializer options the client uses.
/// </summary>
/// <remarks>
/// The options are created once per client and never mutated afterwards.
/// Building them per call would discard the metadata cache that source
/// generation exists to populate, turning every request into a fresh metadata
/// walk.
/// </remarks>
internal sealed class HindsightJson
{
    /// <summary>Creates the options, applying an optional caller hook to a private copy.</summary>
    /// <param name="configure">The caller's hook, or null.</param>
    internal HindsightJson(Action<JsonSerializerOptions>? configure)
    {
        // Two separate options instances, and the reason is subtle enough to be
        // worth stating. A JsonSerializerContext encapsulates and freezes the
        // options it is constructed with. So the context gets its own fresh
        // instance — and the client's options, which the caller's hook may still
        // modify, gets a second. Sharing one instance would throw as soon as the
        // hook touched it.
        var contextOptions = CreateWireOptions();
        var context = new HindsightJsonContext(contextOptions);

        var options = CreateWireOptions();
        options.TypeInfoResolver = context;
        configure?.Invoke(options);

        // Freeze. An options instance a caller still holds a reference to would
        // otherwise throw the moment they touched it after first use.
        options.MakeReadOnly();

        Options = options;
    }

    /// <summary>The frozen options this instance owns.</summary>
    internal JsonSerializerOptions Options { get; }

    /// <summary>The type information for a type.</summary>
    /// <typeparam name="T">The wire model type.</typeparam>
    internal JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    /// <summary>The wire-shape conventions every options instance starts from.</summary>
    private static JsonSerializerOptions CreateWireOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Deliberately left null. Every dictionary key on this API is a
        // caller-supplied identifier — a bank id, a memory id, a tag — and any key
        // policy would silently rewrite it, so a response map would no longer be
        // keyed by what the caller asked for.
        DictionaryKeyPolicy = null,
    };
}
