# VectorNNTP.NNTPD — Logging (Serilog)

Serilog is the **only** logging implementation. Application code continues to use
`Microsoft.Extensions.Logging.ILogger<T>` abstractions; those resolve to Serilog sinks.

## Abstractions vs implementation

| Layer | Role |
|-------|------|
| `ILogger<T>` / `ILoggerFactory` | Application and framework logging API |
| `SerilogLoggerFactory` | Exclusive MEL factory registered by `AddSerilog` |
| Serilog sinks | Destinations (console → stdout → journald under systemd) |

Do not re-add `AddConsole`, `AddDebug`, or `AddEventLog`. `ConfigureNntpdLogging()` clears MEL providers and removes the default `ILoggerFactory` before registering Serilog.

`AddSystemd()` is retained for systemd lifetime and notify support only. Any Microsoft `ConsoleLoggerOptions` formatter registration it may add is removed immediately afterward; Serilog owns console formatting.

## Bootstrap logging

`Program.cs` assigns a bootstrap logger before the host is built so configuration and construction failures are recorded:

```csharp
Log.Logger = NntpdLoggingExtensions.CreateBootstrapLogger();
try { /* build and run host */ }
catch (Exception ex) { Log.Fatal(ex, "..."); }
finally { await Log.CloseAndFlushAsync(); }
```

`AddSerilog` reloads/replaces the bootstrap logger from configuration without duplicating the pipeline. Host disposal and `CloseAndFlushAsync` flush buffered events.

## Configuration

Serilog is configured under the `Serilog` section in `appsettings.json` (and environment-specific files).

### Change minimum level

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "VectorNNTP.NNTPD": "Debug"
    }
  }
}
```

Environment variables (double underscore):

```bash
Serilog__MinimumLevel__Default=Debug
Serilog__MinimumLevel__Override__VectorNNTP.NNTPD=Verbose
```

Invalid Serilog configuration (unknown sink, malformed JSON) fails host startup predictably.

Prefer `Serilog:*` settings. There is no Microsoft `Logging` section in application configuration.

## Console and systemd/journald

Serilog's console sink owns formatting and writes single-line events to stdout. No Microsoft console formatter configuration is required or retained. Under systemd, journald collects process stdout/stderr through the unit's standard execution environment (this does not guarantee journald structured-field preservation beyond plain text lines):

```bash
sudo journalctl -u vectornntpd -f
sudo journalctl -u vectornntpd -b --priority=err
sudo journalctl -u vectornntpd | grep -i watchdog
```

No custom journald sink is required. Do not write directly to stdout/stderr outside Serilog except for catastrophic bootstrap failures already covered by `Log.Fatal`.

Watchdog heartbeats remain Trace/Debug; activation/deactivation stay Information.

## Windows Service and interactive console

All platforms use the same Serilog pipeline. Windows Service and interactive `dotnet run` both emit through the console sink (and whatever sinks you add later via configuration).

## Structured logging conventions

Prefer compile-time / source-generated logging (`LoggerMessageAttribute` + partial methods) with named structured properties. The authoritative rule is [architecture.md — Source-Generated Structured Logging](architecture.md#source-generated-structured-logging).

```csharp
CommandLogMessages.CommandRx(logger, client, command);
LifecycleLogMessages.StartupFailed(logger, exception, elapsedMs);
```

When a generated method is not appropriate, still use a message template and named properties rather than interpolation:

```csharp
_logger.LogInformation(
    "Application entered {State} state after {ElapsedMs} ms",
    state,
    elapsedMs);
```

Avoid string interpolation or concatenation as the log message. Never log secrets, tokens, or credentials.

Logging is a human-readable boundary. The current `ILogger` / `LoggerMessageAttribute` APIs require `string` (or other supported structured types) for operational text. Protocol bytes may be converted to a string **once, locally**, at that boundary when human-readable output is required. Do not treat logging as a reason to change protocol representation in the data plane.

## Shutdown flushing

- Serilog hosted integration disposes/flushes with the Generic Host.
- `Program` always calls `Log.CloseAndFlushAsync()` in `finally`.
- Application services may still log during stop; do not dispose Serilog earlier.

## Troubleshooting

| Symptom | Check |
|---------|--------|
| No logs under systemd | Unit `StandardOutput=` inherits; confirm process writes to stdout; `journalctl -u vectornntpd -f` |
| Too verbose | Raise `Serilog:MinimumLevel` or category overrides |
| Missing early failure logs | Bootstrap logger must run before `Host.CreateApplicationBuilder` |
| Duplicate lines | Ensure MEL providers were not re-added; `writeToProviders` must remain `false` |
