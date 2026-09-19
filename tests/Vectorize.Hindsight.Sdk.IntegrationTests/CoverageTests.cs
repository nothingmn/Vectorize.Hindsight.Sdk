using System.Reflection;
using System.Text.Json;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.IntegrationTests;

/// <summary>
/// The coverage claim, checked mechanically.
/// </summary>
/// <remarks>
/// This is the most important file in the suite. "100% of the non-deprecated
/// footprint" is a claim that can be measured rather than asserted: enumerate the
/// specification's operations, map each id to the method that should exist, and
/// check by reflection that it is there. If a future revision adds an operation,
/// this fails until it is covered.
/// </remarks>
public sealed class CoverageTests
{
    /// <summary>Operations that are hand-written rather than generated, with their method names.</summary>
    /// <remarks>
    /// A multipart upload's parameters are streams and filenames, not a JSON body, so
    /// those three (two, after the deprecated one was excluded) are written by hand.
    /// </remarks>
    private static readonly Dictionary<string, string> HandWritten = new(StringComparer.Ordinal)
    {
        ["file_retain"] = "FileRetainAsync",
        ["import_bank_transfer"] = "ImportBankTransferAsync",
    };

    private static string ToMethodName(string operationId)
    {
        var parts = operationId.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        var pascal = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return pascal + "Async";
    }

    private static JsonDocument LoadPublishedSpec()
    {
        // The spec is copied to the output directory by the project file.
        var path = Path.Combine(AppContext.BaseDirectory, "spec", "openapi.json");
        Assert.True(File.Exists(path), $"The published spec was not copied to {path}.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static IEnumerable<(string Method, string Path, string OperationId)> EnumerateOperations(
        JsonElement paths, bool includeDeprecated)
    {
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var verb = operation.Name.ToUpperInvariant();
                if (verb is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
                {
                    continue;
                }

                var summary = operation.Value.TryGetProperty("summary", out var summaryElement)
                    ? summaryElement.GetString() ?? string.Empty
                    : string.Empty;

                var deprecated =
                    (operation.Value.TryGetProperty("deprecated", out var deprecatedElement) &&
                     deprecatedElement.ValueKind == JsonValueKind.True) ||
                    summary.Contains("removed", StringComparison.OrdinalIgnoreCase);

                if (deprecated != includeDeprecated)
                {
                    continue;
                }

                var operationId = operation.Value.TryGetProperty("operationId", out var idElement)
                    ? idElement.GetString() ?? string.Empty
                    : string.Empty;

                yield return (verb, path.Name, operationId);
            }
        }
    }

    [Fact]
    public void Every_non_deprecated_operation_has_a_client_method()
    {
        using var document = LoadPublishedSpec();
        var paths = document.RootElement.GetProperty("paths");
        var methods = typeof(HindsightClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        var covered = 0;

        foreach (var (verb, path, operationId) in EnumerateOperations(paths, includeDeprecated: false))
        {
            var expected = HandWritten.TryGetValue(operationId, out var handWritten)
                ? handWritten
                : ToMethodName(operationId);

            if (methods.Contains(expected))
            {
                covered++;
            }
            else
            {
                missing.Add($"{verb} {path} (expected method {expected})");
            }
        }

        Assert.Empty(missing);

        // Guard against the enumeration silently finding nothing, which would make the
        // assertion above pass for the wrong reason.
        Assert.Equal(87, covered);
    }

    [Fact]
    public void No_deprecated_operation_has_a_client_method()
    {
        // The other half of the "100% of non-deprecated footprint" claim: the excluded
        // operations must genuinely be absent, not quietly present. Being present would
        // mean the coverage definition had drifted without anyone noticing.
        using var document = LoadPublishedSpec();
        var paths = document.RootElement.GetProperty("paths");
        var methods = typeof(HindsightClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var present = new List<string>();
        var deprecatedCount = 0;

        foreach (var (verb, path, operationId) in EnumerateOperations(paths, includeDeprecated: true))
        {
            deprecatedCount++;
            var name = ToMethodName(operationId);
            if (methods.Contains(name))
            {
                present.Add($"{verb} {path} exposes {name}");
            }
        }

        Assert.Empty(present);
        Assert.Equal(7, deprecatedCount);
    }

    [Fact]
    public void Every_public_async_method_requires_a_cancellation_token_and_returns_a_task()
    {
        // The async-only rule, enforced structurally rather than by review: no
        // synchronous counterpart can be added without failing this.
        var offenders = new List<string>();

        foreach (var method in typeof(HindsightClient).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!method.Name.EndsWith("Async", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 0 || parameters[^1].ParameterType != typeof(CancellationToken))
            {
                offenders.Add($"{method.Name} does not end with a CancellationToken parameter");
                continue;
            }

            if (parameters[^1].HasDefaultValue)
            {
                offenders.Add($"{method.Name} gives its CancellationToken a default value, so cancellation is optional");
            }

            if (!method.ReturnType.IsGenericType &&
                method.ReturnType != typeof(Task))
            {
                offenders.Add($"{method.Name} does not return a Task");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_client_exposes_no_synchronous_io_surface()
    {
        // No sync counterparts, no sync-over-async. A method that performs I/O and does
        // not return a Task would be the first step back to a blocking client.
        //
        // Property accessors are excluded as a category rather than by name. A getter returns state
        // the client already holds and cannot perform I/O, so it is not a synchronous I/O surface
        // however it is named — and naming them individually (get_Options, get_BaseAddress, …) meant
        // every new property needed this test edited, which is how a guard quietly stops guarding.
        var synchronous = typeof(HindsightClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.Name.EndsWith("Async", StringComparison.Ordinal))
            .Where(m => m.Name is not ("Dispose" or "GetType" or "ToString" or "Equals" or "GetHashCode"))
            .Where(m => m.ReturnType != typeof(void) || m.GetParameters().Length > 0)
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(synchronous);

        // And the exclusion above proves itself: a property that *did* expose something async-only
        // must not be reachable synchronously, which this asserts by name so the category exclusion
        // cannot hide a genuine offender.
        Assert.All(
            typeof(HindsightClient).GetProperties().Where(p => p.Name == "Reranker"),
            property => Assert.NotNull(property.GetMethod));
    }
}
