using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Wire-model mapping rules, asserted as rules rather than case by case.
/// </summary>
/// <remarks>
/// Two of the defects found during this build lived here and were invisible to ordinary
/// tests: the tag-group tree was modelled but unreachable from any request, and a
/// nullable array was emitted as an empty one so it always serialized. Both were
/// properties of the generated surface as a whole, so they are asserted as properties of
/// the surface — and where the observable consequence is what matters, the assertion is
/// on the serialized bytes rather than on a field.
/// </remarks>
public sealed class ModelMappingTests
{
    private static JsonSerializerOptions Options() =>
        new HindsightJson(new Configuration.HindsightClientOptions().ConfigureJson).Options;

    /// <summary>
    /// The generated wire DTOs: classes in the Models namespace whose members carry
    /// explicit JSON names.
    /// </summary>
    /// <remarks>
    /// Filtering on <see cref="JsonPropertyNameAttribute"/> excludes the hand-written
    /// unions and their converters, which are deliberately converter-driven and carry no
    /// explicit names, and the constants holder, which is static.
    /// </remarks>
    private static IEnumerable<Type> WireModels() =>
        typeof(HindsightClient).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "Vectorize.Hindsight.Sdk.Models")
            .Where(t => t.GetProperties().Any(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null));

    [Fact]
    public void An_unset_nullable_collection_is_omitted_from_the_wire()
    {
        // The rule with the sharpest consequence. `anyOf: [array, null]` means the member may
        // be absent, and on this API an empty array is a real value: `tags: []` is mutually
        // exclusive with `tag_groups`, so serializing a defaulted `[]` made a valid
        // tag-group request fail with a 422. Asserted on the bytes, because that is where the
        // difference is observable.
        var request = new RecallRequest { Query = "q" };

        var json = JsonSerializer.Serialize(request, Options());

        Assert.DoesNotContain("\"tags\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tag_groups\"", json, StringComparison.Ordinal);
        Assert.Contains("\"query\":\"q\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_collection_the_caller_sets_is_still_serialized()
    {
        // The complement, so the rule above cannot be satisfied by never serializing.
        var request = new RecallRequest { Query = "q", Tags = ["a", "b"] };

        var json = JsonSerializer.Serialize(request, Options());

        Assert.Contains("\"tags\":[\"a\",\"b\"]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spec_defaulted_collection_is_present_as_empty()
    {
        // The other case the specification distinguishes: a plain array with `default: []`
        // documents empty as a real value (CreateDirectiveRequest.tags — "Empty means
        // global"), so it IS initialised and does appear. A blanket rule would be wrong here.
        var request = new CreateDirectiveRequest { Name = "n", Content = "c" };

        var json = JsonSerializer.Serialize(request, Options());

        Assert.Contains("\"tags\":[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nullable_collection_member_has_no_initialiser_in_source()
    {
        // The structural counterpart to the behavioural test: a nullable collection must not
        // be initialised, because an initialiser is what would defeat omission.
        var offenders = new List<string>();

        foreach (var type in WireModels())
        {
            var instance = TryCreate(type);
            if (instance is null)
            {
                continue;
            }

            foreach (var property in type.GetProperties())
            {
                if (!IsCollection(property.PropertyType) || !IsNullable(property))
                {
                    continue;
                }

                if (property.GetValue(instance) is not null)
                {
                    offenders.Add($"{type.Name}.{property.Name} is a nullable collection that was initialised");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_tag_group_tree_is_reachable_from_the_models_that_filter_by_tags()
    {
        // The rule: a model carrying `tag_groups` must expose the typed tree. While it was
        // List<JsonElement>, the hand-written union was unreachable and a caller had to
        // assemble the expression by hand to filter by tags.
        foreach (var typeName in new[] { "RecallRequest", "ReflectRequest" })
        {
            var type = typeof(HindsightClient).Assembly.GetType($"Vectorize.Hindsight.Sdk.Models.{typeName}");
            Assert.NotNull(type);

            var property = type!.GetProperty("TagGroups");
            Assert.NotNull(property);
            Assert.Equal(
                typeof(HindsightTagGroup),
                property!.PropertyType.GetGenericArguments()[0]);
        }
    }

    [Fact]
    public void The_tag_group_tree_serializes_into_the_shape_the_server_declares()
    {
        // End-to-end through the client's own options, because the union converter is the
        // thing that decides the wire shape.
        var filter = HindsightTagGroup.All(
            HindsightTagGroup.Tags(HindsightValues.TagGroupLeafMatch.ALL, "a"),
            HindsightTagGroup.Not(HindsightTagGroup.Tags(HindsightValues.TagGroupLeafMatch.ANY, "b")));

        var request = new RecallRequest { Query = "q", TagGroups = [filter] };
        var json = JsonSerializer.Serialize(request, Options());

        Assert.Contains("\"tag_groups\":[{\"and\":[", json, StringComparison.Ordinal);
        Assert.Contains("\"not\":{", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_wire_member_declares_its_json_name()
    {
        // A member without an explicit name falls back to the serializer's naming policy,
        // which is a silent dependency on a convention nothing verifies. The explicit name is
        // what makes the wire shape reviewable against the specification.
        var offenders = new List<string>();

        foreach (var type in WireModels())
        {
            foreach (var property in type.GetProperties())
            {
                if (property.GetCustomAttribute<JsonPropertyNameAttribute>() is null &&
                    property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_wire_member_is_a_non_nullable_json_element()
    {
        // A non-nullable JsonElement defaults to ValueKind.Undefined, and the framework's
        // converter throws on Undefined when writing — so one untyped member made an entire
        // request unserializable.
        var offenders = WireModels()
            .SelectMany(t => t.GetProperties()
                .Where(p => p.PropertyType == typeof(JsonElement))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_generated_model_is_constructible_without_arguments()
    {
        // Response models are tolerant by design, so every generated model must be creatable
        // with no arguments. A model that could not be would mean members had been marked
        // required after all, which would make deserialization throw on an absent field —
        // turning minor server drift into an unreadable payload.
        var offenders = new List<string>();

        foreach (var type in WireModels())
        {
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                offenders.Add(type.Name);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_request_body_is_serializable_for_every_operation_that_takes_one()
    {
        // The defect class this guards: a model that cannot be serialized compiles, is
        // generated, appears in the surface — and fails only when a caller first uses it.
        // Every request model is round-tripped through the client's own options here.
        var failures = new List<string>();

        foreach (var type in WireModels())
        {
            var instance = TryCreate(type);
            if (instance is null)
            {
                continue;
            }

            try
            {
                var json = JsonSerializer.Serialize(instance, type, Options());
                JsonDocument.Parse(json).Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{type.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// Creates an instance, filling required members so a model with them is reachable.
    /// </summary>
    /// <remarks>
    /// Reflection on the caller's own model, confined to the test assembly: the library
    /// itself never reflects over a caller's type.
    /// </remarks>
    private static object? TryCreate(Type type)
    {
        try
        {
            var instance = Activator.CreateInstance(type);
            if (instance is null)
            {
                return null;
            }

            foreach (var property in type.GetProperties().Where(p => p.CanWrite))
            {
                var required = property.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null;
                if (required && property.GetValue(instance) is null)
                {
                    property.SetValue(instance, DefaultFor(property.PropertyType));
                }
            }

            return instance;
        }
        catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException)
        {
            return null;
        }
    }

    private static object? DefaultFor(Type type)
    {
        if (type == typeof(string))
        {
            return "x";
        }

        if (type == typeof(bool))
        {
            return false;
        }

        if (type == typeof(int))
        {
            return 0;
        }

        if (type == typeof(long))
        {
            return 0L;
        }

        if (type == typeof(double))
        {
            return 0.0;
        }

        if (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(List<>) ||
             type.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            return Activator.CreateInstance(type);
        }

        if (type == typeof(JsonElement) || type == typeof(JsonElement?))
        {
            return null;
        }

        if (type.IsClass && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch (MissingMethodException)
            {
                return null;
            }
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static bool IsCollection(Type type)
    {
        var candidate = Nullable.GetUnderlyingType(type) ?? type;
        return candidate.IsGenericType &&
               (candidate.GetGenericTypeDefinition() == typeof(List<>) ||
                candidate.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));
    }

    private static bool IsNullable(PropertyInfo property) =>
        Nullable.GetUnderlyingType(property.PropertyType) is not null ||
        new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable;
}
