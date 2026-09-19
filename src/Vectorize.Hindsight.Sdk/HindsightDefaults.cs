using System.Reflection;

namespace Vectorize.Hindsight.Sdk;

/// <summary>Values that are fixed for this build of the library.</summary>
public static class HindsightDefaults
{
    /// <summary>The library version, taken from the assembly.</summary>
    public static string Version { get; } =
        typeof(HindsightDefaults).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    /// <summary>
    /// The default <c>User-Agent</c>, identifying both the library and the
    /// version that produced a request.
    /// </summary>
    public static string UserAgent { get; } = $"vectorize-hindsight-dotnet/{Version}";
}
