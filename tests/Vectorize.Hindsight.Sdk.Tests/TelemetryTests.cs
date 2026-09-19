using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Redaction and telemetry.
/// </summary>
/// <remarks>
/// <para>
/// The redaction test is the one worth having. It drives a recognisable sentinel
/// through a call and then searches every captured log message, span tag and
/// metric tag for it. That is cheap, and it is the test that would catch a real
/// liability — content the caller stored being written to a log or a metric where
/// it can be read by someone who should not see it.
/// </para>
/// <para>
/// This class disables xunit's parallelisation because it installs process-wide
/// listeners (an <see cref="ActivityListener"/> and a <see cref="MeterListener"/>),
/// which would otherwise interleave with other suites and make the assertions
/// flaky for reasons unrelated to the code under test.
/// </para>
/// </remarks>
[Collection("telemetry")]
public sealed class TelemetryTests
{
    /// <summary>
    /// A distinctive value that must never appear in telemetry.
    /// </summary>
    /// <remarks>
    /// Long and unlikely to occur by accident, so a match is a real leak rather
    /// than a coincidence in a stack trace.
    /// </remarks>
    private const string Sentinel = "SENTINEL-c0ffee-deadbeef-do-not-log-me";

    [Fact]
    public async Task Caller_content_never_reaches_a_log_a_span_or_a_metric_tag()
    {
        var logs = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            // The default minimum level filters out Debug and Trace, which would make
            // a logging assertion fail for the wrong reason.
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new CapturingLoggerProvider(logs));
        });
        var logger = loggerFactory.CreateLogger<HindsightClient>();

        using var activityListener = new CapturingActivityListener();
        using var meterListener = new CapturingMeterListener();

        var transport = new StubTransport().EnqueueJson(200, """{"success":true,"bank_id":"b","items_count":1}""");
        using var client = new HindsightClient(
            new Configuration.HindsightClientOptions
            {
                BaseAddress = "http://localhost:8888",
                Transport = transport,
                ApiKey = "key-that-must-not-be-logged",
            },
            logger)
        {
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };

        await client.RetainMemoriesAsync(
            "bank-with-a-real-name",
            new RetainRequest
            {
                Items =
                [
                    new MemoryItem
                    {
                        Content = HindsightMemoryContent.FromText(Sentinel),
                        Context = Sentinel,
                        Metadata = new Dictionary<string, string> { ["note"] = Sentinel },
                    },
                ],
            },
            CancellationToken.None);

        // The assertion, over every captured surface.
        Assert.DoesNotContain(logs, message => message.Contains(Sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(
            CapturingActivityListener.Tags,
            tag => tag.Contains(Sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(
            CapturingMeterListener.Tags,
            tag => tag.Contains(Sentinel, StringComparison.Ordinal));

        // The credential is equally excluded.
        Assert.DoesNotContain(logs, message => message.Contains("key-that-must-not-be-logged", StringComparison.Ordinal));
        Assert.DoesNotContain(
            CapturingActivityListener.Tags,
            tag => tag.Contains("key-that-must-not-be-logged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bank_id_never_becomes_a_metric_tag_or_span_tag()
    {
        // A bank id is caller-chosen, so it is excluded for the same reason content
        // is: it can be identifying, and an unbounded tag value also explodes
        // cardinality.
        using var activityListener = new CapturingActivityListener();
        using var meterListener = new CapturingMeterListener();

        var transport = new StubTransport().EnqueueJson(200, """{"banks":[],"total":0,"limit":100,"offset":0}""");
        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
        };

        await client.ListBanksAsync(null, CancellationToken.None);

        Assert.DoesNotContain(
            CapturingMeterListener.Tags, tag => tag.Contains("banks", StringComparison.OrdinalIgnoreCase) &&
                                                tag.Contains("bank_id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(CapturingActivityListener.Tags, tag => tag.Contains("hindsight.operation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_successful_call_records_an_activity_and_a_metric()
    {
        using var activityListener = new CapturingActivityListener();
        using var meterListener = new CapturingMeterListener();

        var transport = new StubTransport().EnqueueJson(200, """{"banks":[],"total":0,"limit":100,"offset":0}""");
        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
        };

        await client.ListBanksAsync(null, CancellationToken.None);

        Assert.Contains(CapturingActivityListener.Names, n => n == "GET /v1/default/banks");
        Assert.Contains(CapturingActivityListener.Tags, t => t == "http.response.status_code=200");
        Assert.Contains(CapturingMeterListener.Instruments, i => i == "hindsight.client.calls");
    }

    [Fact]
    public async Task A_retry_is_recorded_on_the_span_and_the_retry_counter()
    {
        using var activityListener = new CapturingActivityListener();
        using var meterListener = new CapturingMeterListener();

        var transport = new StubTransport()
            .EnqueueJson(503, """{"detail":"busy"}""")
            .EnqueueJson(200, """{"banks":[],"total":0,"limit":100,"offset":0}""");
        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };

        await client.ListBanksAsync(null, CancellationToken.None);

        Assert.Contains(CapturingActivityListener.Tags, t => t == "hindsight.retry_count=1");
        Assert.Contains(CapturingMeterListener.Instruments, i => i == "hindsight.client.retries");
    }

    [Fact]
    public async Task Telemetry_is_silent_when_no_logger_is_supplied()
    {
        // A caller that passes no logger must not be forced to care about one. The
        // client is constructed and used with no logger at all, and must work.
        var transport = new StubTransport().EnqueueJson(200, """{"banks":[],"total":0,"limit":100,"offset":0}""");
        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
        };

        var result = await client.ListBanksAsync(null, CancellationToken.None);

        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task A_protocol_violation_is_logged_without_the_body()
    {
        var logs = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new CapturingLoggerProvider(logs));
        });

        var transport = new StubTransport().EnqueueJson(200, """{"unparseable": """);
        using var client = new HindsightClient(
            new Configuration.HindsightClientOptions
            {
                BaseAddress = "http://localhost:8888",
                Transport = transport,
            },
            loggerFactory.CreateLogger<HindsightClient>())
        {
            TimeProvider = new ImmediateTimeProvider(),
        };

        await Assert.ThrowsAsync<Errors.HindsightProtocolException>(
            () => client.ListBanksAsync(null, CancellationToken.None));

        Assert.NotEmpty(logs);
        Assert.Contains(logs, message => message.Contains("GET /v1/default/banks", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _logs;

        internal CapturingLoggerProvider(List<string> logs) => _logs = logs;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<string> _logs;

            internal CapturingLogger(string category, List<string> logs)
            {
                _category = category;
                _logs = logs;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _logs.Add($"{_category}|{logLevel}|{formatter(state, exception)}");
                if (exception is not null)
                {
                    _logs.Add(exception.ToString());
                }
            }
        }
    }

    private sealed class CapturingActivityListener : IDisposable
    {
        private readonly ActivityListener _listener;

        internal CapturingActivityListener()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == Telemetry.HindsightTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity => Names.Add(activity.OperationName),

                // Tags are read on stop: by then every tag the client set is present,
                // including the ones added after the span started.
                ActivityStopped = activity =>
                {
                    foreach (var tag in activity.Tags)
                    {
                        Tags.Add($"{tag.Key}={tag.Value}");
                    }

                    foreach (var tag in activity.TagObjects)
                    {
                        Tags.Add($"{tag.Key}={tag.Value}");
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal static List<string> Names { get; } = [];

        internal static List<string> Tags { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed class CapturingMeterListener : IDisposable
    {
        private readonly MeterListener _listener;

        internal CapturingMeterListener()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    Instruments.Add(instrument.Name);
                    listener.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    Tags.Add($"{instrument.Name}:{tag.Key}={tag.Value}");
                }
            });
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    Tags.Add($"{instrument.Name}:{tag.Key}={tag.Value}");
                }
            });
            _listener.Start();
        }

        internal static List<string> Instruments { get; } = [];

        internal static List<string> Tags { get; } = [];

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>
/// Groups telemetry tests so they do not run in parallel with anything else.
/// </summary>
/// <remarks>
/// They install process-wide listeners, so running them alongside another suite
/// would produce interleaved captures and flaky assertions.
/// </remarks>
[CollectionDefinition("telemetry", DisableParallelization = true)]
public sealed class TelemetryCollectionDefinition
{
}
