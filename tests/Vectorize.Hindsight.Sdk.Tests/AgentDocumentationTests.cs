using System.Reflection;
using System.Text.RegularExpressions;
using Vectorize.Hindsight.Sdk;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Keeps <c>AGENTS.md</c> from drifting away from the code it describes.
/// </summary>
/// <remarks>
/// <para>
/// The document tells an agent which calls to make, so a wrong name in it is worse than no
/// document: the agent copies the call, gets a compile error, and has no way to tell whether the
/// document is stale or the library is broken. This test asserts the names the document uses still
/// resolve against the real client.
/// </para>
/// <para>
/// It checks the two things that can rot silently: operation method names, and the types the
/// document tells a reader to construct or handle. Renaming an operation is exactly the change that
/// a prose document does not survive.
/// </para>
/// </remarks>
public sealed class AgentDocumentationTests
{
    /// <summary>The document this suite guards.</summary>
    private const string DocumentName = "AGENTS.md";

    private static string RepositoryRoot()
    {
        // Walk up from the test binary until the document is found, so the test does not
        // depend on the working directory the runner happens to use.
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, DocumentName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate {DocumentName} above {AppContext.BaseDirectory}. The document is the " +
            "subject of this test, so its absence is a failure rather than a skip.");
    }

    private static string ReadDocument()
    {
        string path = Path.Combine(RepositoryRoot(), DocumentName);

        Assert.True(File.Exists(path), $"{DocumentName} is missing from the repository root.");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Public method names declared anywhere in the sources, found textually.
    /// </summary>
    /// <remarks>
    /// Textual rather than reflective because the document also covers the admin and reranker
    /// packages, which this test project does not reference. Reading the sources keeps the check
    /// honest without widening the test project's dependency graph — and a rename is a source
    /// change either way, which is what this is guarding against.
    /// </remarks>
    private static HashSet<string> PublicMethodNamesInSources()
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (string file in SourceFiles())
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"public\s+(?:static\s+)?(?:async\s+)?[\w<>,.?\[\] ]+?\s+(\w+)\s*(?:\(|<)"))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>Public type names declared anywhere in the sources, found textually.</summary>
    private static HashSet<string> PublicTypeNamesInSources()
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (string file in SourceFiles())
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"public\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+)*(?:class|record|struct|interface|enum)\s+(\w+)"))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>Every <c>.cs</c> file under <c>src/</c>, excluding build output.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        string root = Path.Combine(RepositoryRoot(), "src");

        Assert.True(Directory.Exists(root), $"The source directory {root} is missing.");

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    /// <summary>
    /// Every <c>*Async</c> name the document mentions must be a real public method.
    /// </summary>
    /// <remarks>
    /// The document may legitimately mention a name that the library does not own — a caller's own
    /// helper inside a recipe. Those are named in <see cref="NotOperations"/> explicitly rather than
    /// excluded by a pattern, so a genuine rename cannot quietly join the list.
    /// </remarks>
    [Fact]
    public void EveryMethodNamedInTheDocumentExists()
    {
        HashSet<string> declared = PublicMethodNamesInSources();

        // Reflection on the client too, so an inherited or generated member is covered even if the
        // textual scan misses it.
        foreach (MethodInfo method in typeof(HindsightClient).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            declared.Add(method.Name);
        }

        string[] documented = Regex
            .Matches(ReadDocument(), @"\b([A-Z][A-Za-z0-9]*(?:Async))\b")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] unknown = [.. documented.Where(name => !declared.Contains(name) && !NotOperations.Contains(name))];

        Assert.True(
            unknown.Length == 0,
            $"{DocumentName} names {unknown.Length} method(s) that exist nowhere in src/: " +
            $"{string.Join(", ", unknown)}. Either the document is stale or the name is wrong.");

        // A document that mentions no methods would pass the check above vacuously.
        Assert.True(
            documented.Length > 50,
            $"Only {documented.Length} method names found in {DocumentName}; the scan is not reading the document.");
    }

    /// <summary>
    /// Names ending in <c>Async</c> that the document legitimately uses and the library does not own:
    /// the caller's own helpers inside a recipe.
    /// </summary>
    private static readonly HashSet<string> NotOperations = new(StringComparer.Ordinal)
    {
        "ByNameAsync",     // a helper defined inside the document's own example.
        "RecallAsync",     // ditto.
        "RememberAsync",   // ditto.
    };

    /// <summary>
    /// Every library type name the document references must be declared somewhere in the sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Types are found by <i>usage context</i> rather than by suffix. A name preceded by
    /// <c>new</c>, <c>typeof(</c>, or <c>catch (</c>, or followed by <c>? </c>/<c>{</c>/<c>&lt;</c>
    /// and then an identifier, is unambiguously being used as a type — so the check has no need to
    /// guess and therefore does not fire on ordinary prose.
    /// </para>
    /// <para>
    /// Framework types the document mentions legitimately are listed in <see cref="NotLibraryTypes"/>,
    /// explicitly, so that removing a real type from the library still fails.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTypeUsedAsATypeInTheDocumentExists()
    {
        HashSet<string> declared = PublicTypeNamesInSources();

        foreach (Type type in typeof(HindsightClient).Assembly.GetExportedTypes())
        {
            declared.Add(type.Name);
        }

        foreach (Type type in typeof(DependencyInjection.HindsightServiceCollectionExtensions).Assembly.GetExportedTypes())
        {
            declared.Add(type.Name);
        }

        string document = ReadDocument();

        // Usage contexts that only a type appears in.
        string[] patterns =
        [
            @"\bnew\s+(?!\s)([A-Z][A-Za-z0-9]*)\s*[({]",   // new RecallRequest { ... } / new OnnxReranker(...)
            @"\btypeof\(([A-Z][A-Za-z0-9]*)\)",               // typeof(HindsightClient)
            @"\bcatch\s*\(\s*([A-Z][A-Za-z0-9]*)",           // catch (HindsightException ex)
            @"\b([A-Z][A-Za-z0-9]*)<[A-Za-z]",                  // CrossBankRecallResult.Candidates is List<RecallResult>
            @"^\s*([A-Z][A-Za-z0-9]*)\??\s+\w+\s*=",         // TypeName variable = ... (declaration)
            @"\b([A-Z][A-Za-z0-9]*)\??\s+\w+\s*=\s*await",  // TypeName x = await ...
        ];

        HashSet<string> used = new(StringComparer.Ordinal);

        foreach (string pattern in patterns)
        {
            foreach (Match match in Regex.Matches(document, pattern, RegexOptions.Multiline))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.True(used.Count >= 30, $"Only {used.Count} type usages found in {DocumentName}; the scan is not reading the document.");

        // `AddReranker<TFactory>()` matches a type-shaped pattern because a generic method call and a
        // generic type are the same syntax. Names that are declared methods are therefore excluded
        // structurally rather than by adding each one to a hand-maintained list.
        HashSet<string> methods = PublicMethodNamesInSources();

        string[] unknown =
        [
            .. used
                .Where(name => !declared.Contains(name)
                            && !NotLibraryTypes.Contains(name)
                            && !methods.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
        ];

        Assert.True(
            unknown.Length == 0,
            $"{DocumentName} uses {unknown.Length} name(s) as a type that exist nowhere in src/: " +
            $"{string.Join(", ", unknown)}.");
    }

    /// <summary>
    /// Types the document legitimately names that the library does not declare.
    /// </summary>
    /// <remarks>
    /// Framework and BCL types, plus the caller's own example helpers. Listed one by one rather than
    /// matched by a framework-namespace pattern, so a library type that disappears cannot hide here.
    /// </remarks>
    private static readonly HashSet<string> NotLibraryTypes = new(StringComparer.Ordinal)
    {
        // BCL / framework
        "CancellationToken", "Task", "Stream", "Console", "List", "IDisposable", "IServiceCollection",
        "IServiceProvider", "ILogger", "IConfiguration", "Exception", "Environment", "StringComparison",
        "TimeSpan", "DayOfWeek", "String", "Action", "Func", "Dictionary", "MemoryStream",
        "IReadOnlyList", "IReadOnlyCollection", "IEnumerable", "IList", "ICollection", "JsonElement",
        // The document's own example types and methods
        "MyReranker", "MyFakeTransport", "MySecretStoreKeyProvider", "By" + "NameAsync",
        "Program", "Startup",
        // Test-framework / language constructs that read like a type in a declaration pattern
        "Assert", "Fact", "Theory", "InlineData", "Builder", "ServiceProvider", "Add",
        // C# keywords and prose words that the declaration pattern can over-match
        "HTTP", "JSON", "ONNX", "TypeSafe", "Hindsight",
    };

    /// <summary>
    /// The document must carry the warnings that cost the most to omit.
    /// </summary>
    /// <remarks>
    /// These are the behaviours that look like success when they go wrong: a 200 that means
    /// "accepted" rather than "stored", an empty result that means "no such bank", a rerank score
    /// that is ordinal rather than calibrated. A rewrite that drops one of these makes the document
    /// worse than silent, so their absence fails the build.
    /// </remarks>
    [Theory]
    [InlineData("ChildOperations")]
    [InlineData("RerankApplied")]
    [InlineData("TagsMatch")]
    [InlineData("IsPartial")]
    [InlineData("ordinal")]
    [InlineData("PolicyWeight")]
    [InlineData("IsIdentity")]
    public void TheDocumentStatesTheBehaviourThatLooksLikeSuccess(string required)
    {
        Assert.Contains(required, ReadDocument(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every operation the client exposes should be findable by name in the document's index.
    /// </summary>
    /// <remarks>
    /// The document claims to cover the whole surface. If an operation is added and the document is
    /// not updated, that claim becomes false — so the claim is checked rather than trusted.
    /// </remarks>
    [Fact]
    public void TheDocumentIndexesTheWholeSurface()
    {
        string document = ReadDocument();

        string[] operations = typeof(HindsightClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal))
            .Where(method => method.DeclaringType == typeof(HindsightClient))
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] absent = [.. operations.Where(name => !document.Contains(name, StringComparison.Ordinal))];

        Assert.True(
            absent.Length == 0,
            $"{DocumentName} does not mention {absent.Length} operation(s): {string.Join(", ", absent)}. " +
            "The document claims full surface coverage, so every operation must appear.");
    }
}
