# Security policy

## Reporting a vulnerability

Report suspected vulnerabilities privately rather than in a public issue. Open a
[security advisory](https://github.com/robchartier) on the repository, or contact the maintainer
directly.

Please include what you did, what happened, and what you expected. A minimal reproduction is worth
more than a description, and it lets the fix be verified rather than assumed.

## Scope

This library is a **client**. It holds no server-side state and runs in the caller's process.

In scope:

- Credential handling. The API key must never appear in a log, a span tag, a metric tag, or an
  exception message; the authorization header is always redacted; and `Authorization` cannot be
  supplied through `HindsightClientOptions.Headers` as an ordinary string.
- Caller-content exposure. The content being retained may be sensitive or regulated. It must never
  reach a log, span, or metric tag at any level. There is a test that drives a sentinel value
  through a call and searches every captured surface for it.
- Command injection or argument confusion in the admin bridge, which builds a process invocation
  from caller-supplied identifiers.
- Serialization behaviour that could be induced to read memory or execute code.

Out of scope:

- The Hindsight service itself, or the admin CLI. Report those to Vectorize.
- Anything requiring a modified local build of this library.
- Denial of service against a caller's own process by passing deliberately hostile input to a
  public method.

## Credentials

The API key is resolved from an `IApiKeyProvider` or from `HindsightClientOptions.ApiKey`, and is
sent as a Bearer token. It is never logged.

For the admin bridge, the database connection string is read from the environment rather than passed
on the command line, because a command line is visible to every process on the host. That is
deliberate, and it is a reason not to add a way to pass one as an argument.

## Supported versions

While the version is `0.x`, only the latest release receives fixes. Security fixes are released as a
patch on the current minor.

## Disclosure

A fix is released before details are published, so a consumer has a version to move to. Credit is
given unless you prefer otherwise.
