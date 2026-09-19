#!/usr/bin/env bash
# Consume the packed packages the way a real consumer would.
#
# `dotnet pack` produces a well-formed archive. `verify_packages.py` checks its contents.
# Neither proves the archive can be *restored and called*. This script does: it creates a
# throwaway project outside the repository, points it at the packages directory as a local
# feed, restores, and calls into the library. A package that is missing a dependency, has a
# broken lib/ payload, or carries an unsatisfiable sibling version fails here.
#
# The throwaway project lives under a temporary directory and is removed on exit, so the
# repository never gains a build artifact and no test depends on it existing.
#
# Usage:
#   bash eng/consume_package.sh [versions-dir]
#
# Exit codes: 0 consumed successfully, 1 a failure, 2 misconfiguration.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT="$(pwd)"

PACKAGES_DIR="${1:-$ROOT/artifacts/packages}"
VERSION="$(python3 eng/package_version.py)"

if [[ ! -d "$PACKAGES_DIR" ]]; then
  printf '[consume] ERROR: %s does not exist. Run `dotnet pack` first.\n' "$PACKAGES_DIR" >&2
  exit 2
fi

for package in Vectorize.Hindsight.Sdk Vectorize.Hindsight.Sdk.DependencyInjection Vectorize.Hindsight.Sdk.Admin; do
  if ! ls "$PACKAGES_DIR/${package}.${VERSION}.nupkg" >/dev/null 2>&1; then
    printf '[consume] ERROR: %s.%s.nupkg is missing from %s\n' "$package" "$VERSION" "$PACKAGES_DIR" >&2
    exit 2
  fi
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/hindsight-consume-XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

printf '[consume] consuming version %s from %s\n' "$VERSION" "$PACKAGES_DIR"
printf '[consume] scratch directory: %s\n' "$WORK"

# A consumer needs a NuGet.config that adds the local feed. Nothing else: the packages must
# carry their own dependency graph, which is exactly what this is checking.
cat > "$WORK/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$PACKAGES_DIR" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

# The version is substituted rather than referenced as an MSBuild property: `$(Version)` is
# not defined in this scratch project, so the reference would silently resolve to 0.0.0 and
# the restore would fail for the wrong reason.
cat > "$WORK/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Vectorize.Hindsight.Sdk" Version="$VERSION" />
    <PackageReference Include="Vectorize.Hindsight.Sdk.DependencyInjection" Version="$VERSION" />
    <PackageReference Include="Vectorize.Hindsight.Sdk.Admin" Version="$VERSION" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
  </ItemGroup>
</Project>
EOF

cat > "$WORK/Program.cs" <<'EOF'
// Calls into each package's real surface, so a package that restores but does not load,
// or loads but is missing a type the README advertises, fails here rather than for a user.

using Microsoft.Extensions.DependencyInjection;
using Vectorize.Hindsight.Sdk;
using Vectorize.Hindsight.Sdk.Admin;
using Vectorize.Hindsight.Sdk.DependencyInjection;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;

var failures = new List<string>();

// 1. The HTTP client constructs with no configuration, which is the quick-start path.
try
{
    using var client = new HindsightClient();
    if (client.BaseAddress != "http://localhost:8888")
    {
        failures.Add($"default BaseAddress was {client.BaseAddress}");
    }

    // A generated operation exists and is callable. It is never invoked against a server;
    // the point is that the type and method survived packing.
    var method = typeof(HindsightClient).GetMethod("RecallMemoriesAsync");
    if (method is null)
    {
        failures.Add("RecallMemoriesAsync is missing from the packed assembly");
    }

    // A hand-written union type, which lives outside the generated directories.
    HindsightMemoryContent content = "text";
    if (!content.IsText)
    {
        failures.Add("HindsightMemoryContent lost its implicit conversion");
    }

    // The error hierarchy is public, and the root is shared across packages.
    if (!typeof(HindsightConfigurationException).IsSubclassOf(typeof(HindsightException)))
    {
        failures.Add("HindsightConfigurationException no longer derives from HindsightException");
    }
}
catch (Exception ex)
{
    failures.Add($"core package: {ex.GetType().Name}: {ex.Message}");
}

// 2. The DI package registers and resolves.
try
{
    var services = new ServiceCollection();
    services.AddHindsight(options => options.BaseAddress = "https://consumed.example.com");

    using var provider = services.BuildServiceProvider();
    var client = provider.GetRequiredService<HindsightClient>();

    if (client.Options.BaseAddress != "https://consumed.example.com")
    {
        failures.Add($"DI registration lost the configured address: {client.Options.BaseAddress}");
    }
}
catch (Exception ex)
{
    failures.Add($"DI package: {ex.GetType().Name}: {ex.Message}");
}

// 3. The admin bridge constructs and describes its invocation, which exercises the
//    options type and the process-runner seam without spawning anything.
try
{
    var options = new HindsightAdminOptions
    {
        Executable = "docker",
    };
    options.LauncherPrefixArguments.Add("exec");
    options.LauncherPrefixArguments.Add("-i");
    options.LauncherPrefixArguments.Add("hindsight-api");

    using var admin = new HindsightAdminClient(options);

    if (admin.DescribeInvocationPrefix() != "docker exec -i hindsight-api")
    {
        failures.Add($"admin invocation prefix was {admin.DescribeInvocationPrefix()}");
    }

    // The exception type is in the shared hierarchy, so one catch covers both packages.
    if (!typeof(HindsightAdminException).IsSubclassOf(typeof(HindsightException)))
    {
        failures.Add("HindsightAdminException does not derive from the shared HindsightException");
    }
}
catch (Exception ex)
{
    failures.Add($"admin package: {ex.GetType().Name}: {ex.Message}");
}

if (failures.Count > 0)
{
    Console.WriteLine("CONSUME FAILED:");
    foreach (var failure in failures)
    {
        Console.WriteLine($"  - {failure}");
    }

    return 1;
}

Console.WriteLine("CONSUME OK: all three packages restored and their public surfaces are callable.");
return 0;
EOF

printf '[consume] restoring from the local feed...\n'
if ! (cd "$WORK" && dotnet restore --configfile NuGet.config >/dev/null 2>&1); then
  printf '[consume] restore FAILED. Output:\n' >&2
  (cd "$WORK" && dotnet restore --configfile NuGet.config 2>&1 | tail -25) >&2
  exit 1
fi

printf '[consume] building the consumer...\n'
if ! (cd "$WORK" && dotnet build -c Release --no-restore >/dev/null 2>&1); then
  printf '[consume] build FAILED. Output:\n' >&2
  (cd "$WORK" && dotnet build -c Release --no-restore 2>&1 | tail -25) >&2
  exit 1
fi

printf '[consume] running the consumer...\n'
if ! (cd "$WORK" && dotnet run -c Release --no-build); then
  printf '[consume] the consumer reported a failure.\n' >&2
  exit 1
fi

printf '[consume] passed: version %s restores and loads from the packaged archives.\n' "$VERSION"
