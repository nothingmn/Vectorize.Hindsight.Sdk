# Vectorize.Hindsight.Sdk.DependencyInjection

`Microsoft.Extensions.DependencyInjection` integration for
`Vectorize.Hindsight.Sdk`. It registers the SDK's `HindsightClient` as a
singleton and lets a host wire the base address, tenant and retry behaviour from
configuration, without the SDK taking a dependency on a container.

Target framework: `net10.0`. The package references
`Microsoft.Extensions.DependencyInjection.Abstractions`,
`Microsoft.Extensions.Configuration.Abstractions`,
`Microsoft.Extensions.Configuration.Binder` and
`Microsoft.Extensions.Logging.Abstractions`; it does not pull in a concrete
container.

## Install

```
dotnet add package Vectorize.Hindsight.Sdk.DependencyInjection
```

## Usage

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vectorize.Hindsight.Sdk;
using Vectorize.Hindsight.Sdk.DependencyInjection;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();

// Bind the "Hindsight" section, then refine it in code.
services.AddHindsightFromConfiguration(configuration, "Hindsight", options =>
{
    options.Tenant = "acme";
});

using var provider = services.BuildServiceProvider();

var client = provider.GetRequiredService<HindsightClient>();
Console.WriteLine(client.BaseAddress); // as configured

// Optional: prove connectivity at a point where you can await and handle failure.
var version = await HindsightClientWarmup.WarmAsync(provider, cancellationToken);
Console.WriteLine(version.ApiVersion);
```

Without configuration:

```csharp
services.AddHindsight(options =>
{
    options.BaseAddress = "https://hindsight.internal";
    options.MaxAttempts = 5;
});
```

Both entry points are idempotent — calling them more than once, or calling one
after the other, resolves the same client.

### What gets registered

| Service | Lifetime | Default |
|---|---|---|
| `HindsightClient` | singleton, via a factory | built from the effective options and the container's `ILogger<HindsightClient>` |
| `HindsightClientOptions` | singleton | the effective options, so a host can inject and log them |
| `IApiKeyProvider` | singleton | `EnvironmentApiKeyProvider`, or an env→configuration chain from the configuration overload |
| `EnvironmentApiKeyProvider` | singleton | reads `HINDSIGHT_API_KEY` |

Every default is added with `TryAdd` semantics, so a registration the host makes
first — or later — wins. `HttpClient`, `IHindsightTransport`, `IApiKeyProvider`
and `ILogger<HindsightClient>` are all resolved with `GetService`: a host that
registers its own is used, and a host that registers none gets the library's
default rather than a null. Nothing is registered for `HttpClient` or
`IHindsightTransport` by default, because the client already manages those
correctly on its own.

## Configuration

The `Hindsight` section accepts:

```json
{
  "Hindsight": {
    "BaseAddress": "https://hindsight.internal",
    "Tenant": "acme",
    "MaxAttempts": 5,
    "InitialRetryDelay": "00:00:00.500",
    "MaxRetryDelay": "00:00:05",
    "MaxRetryAfter": "00:01:00",
    "RetryJitterFraction": 0.25,
    "Timeout": "00:01:40",
    "UserAgent": "my-service/1.2.3"
  }
}
```

Durations use `TimeSpan` format (`hh:mm:ss`, or `00:00:00.500` for sub-second
values), not a bare number of seconds. A recognised key that is present but
unparseable is a configuration error: `AddHindsight`-resolution throws
`HindsightConfigurationException` naming the key, rather than ignoring it and
leaving a default in place.

### The API key is not bound into the options

`HindsightClientOptions.ApiKey` is a plain `string`. A key in `appsettings.json`
is a secret in a file that is routinely committed, and a key in an options
property is a value any options dump or log line may render. So this package
never assigns `ApiKey` from configuration. Instead, the configuration overload
registers an `IApiKeyProvider` ladder:

1. `HINDSIGHT_API_KEY` environment variable — process-level deployment settings
   outrank a file, and this is the variable the vendor's other clients honour.
2. The `ApiKey` key inside the bound section (`Hindsight:ApiKey` by default),
   read at request time, not at registration time.

That means the key can come from `dotnet user-secrets`, an environment variable,
or any configuration provider a secret store plugs in as, without the value ever
becoming an options property. To take full control, register your own
`IApiKeyProvider` before or after calling `AddHindsight` — `TryAdd` keeps yours.

The non-configuration overload does no configuration reading at all. There, the
key comes from `options.ApiKey`/`options.ApiKeyProvider` as your callback sets
them, or from `HINDSIGHT_API_KEY` as a last resort when neither is set and the
host registered no `IApiKeyProvider`.

## Warmup is opt-in and never automatic

`AddHindsight` performs no I/O and starts no background work. The client's
constructor performs no I/O either, so nothing can fail at startup on a network
problem. `HindsightClientWarmup.WarmAsync(provider, cancellationToken)` is the
one place that makes a real call — a single `GET /version` — and **you must call
it yourself**, from a startup path where you can await it and decide what a
failure means.

Warmup catches nothing: cancellation, a transport failure, a non-success status
and an unparseable body all propagate as the SDK's own exception for that
condition. Swallowing them here would hide the difference between "the server is
not up yet" and "the key is wrong", which is the only reason to check.

## Related packages

- `Vectorize.Hindsight.Sdk` — the client itself. Usable with no container at all;
  this package adds registration convenience on top of it.
