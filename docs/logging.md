# VectorNNTP.NNTPD — Logging (Serilog)

Serilog is the **only** logging implementation. Application code continues to use
`Microsoft.Extensions.Logging.ILogger<T>` abstractions; those resolve to Serilog sinks.

## Abstractions vs implementation

| Layer | Role |
|-------|------|
| `ILogger<T>` / `ILoggerFactory` | Application and framework logging API |
| `SerilogLoggerFactory` | Exclusive MEL factory registered by `AddSerilog` |
| Serilog sinks | Destinations (console Information+ → stdout → journald; file Verbose+ under `Nntpd:LogDir`) |

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

After `host.Build()`, `NntpdLoggingExtensions.WriteLoggingInitialized` emits one Information event through `ILogger` (`VectorNNTP.NNTPD.Hosting`) proving the Serilog factory is live. It includes Application, provider type, category, environment, and content root. It does not dump configuration or secrets.

## Configuration

Serilog is configured under the `Serilog` section in `appsettings.json` (and environment-specific files).

Console and file have **separate** minimum levels. Do not raise the global / `VectorNNTP.NNTPD` minimum to `Information` — that would starve the file sink of Debug and Trace events.

| Sink | Minimum | Purpose |
|------|---------|---------|
| Console | Information+ | Interactive / journald operational use |
| File | Verbose+ (MEL Trace / Debug+) | Full diagnostics, including TAKETHIS RX/TX |

There is one source of truth per operational setting. `Serilog:WriteTo` in `appsettings.json` owns File/Async/Archive **arguments**. `Nntpd:LogDir` owns the directory. Code does not re-declare rolling, retention, async buffer, or minimum-level values.

| Setting | Source of truth |
|---------|-----------------|
| Console minimum / template | `Serilog:WriteTo` Console args |
| File minimum / template / rolling / retention / buffered / size limit | `Serilog:WriteTo` Async → File args |
| Async buffer / `blockWhenFull` | `Serilog:WriteTo` Async args |
| Gzip + `CompressionLevel.Fastest` | File `hooks` string → `NntpdSerilogHooks.DailyGzipFastest` |
| Log directory | `Nntpd:LogDir` |

`ConfigureNntpdLogging` creates `Nntpd:LogDir` and overwrites the File `path` so the JSON placeholder (`logs/VectorNNTP.NNTPD-.log`) is never the runtime path. Serilog.Settings.Configuration 10.0.1 cannot expand `Nntpd:LogDir` into `path`. Relative `LogDir` values resolve through Common `ApplicationLocalPath.ResolveApplicationLocalPath` against `AppContext.BaseDirectory`. Relative `Nntpd:AcmeStateDir` values resolve through the ACME wrapper `ResolveAcmeStateDir`, which delegates to the same helper.

`ArchiveHooks` cannot be constructed from JSON scalars. The File `hooks` argument is the Settings.Configuration type/member string `VectorNNTP.NNTPD.Logging.NntpdSerilogHooks::DailyGzipFastest, VectorNNTP.NNTPD` (`CompressionLevel.Fastest`, no archive count limit). That factory is the only File/Archive construction left in code.

Daily rolling uses Serilog `rollingInterval: Day` (local midnight). The active file is `{ApplicationName}-yyyyMMdd.log` (for example `VectorNNTP.NNTPD-20260925.log`). `fileSizeLimitBytes` is JSON `null` and `rollOnFileSizeLimit` is `false` so a single day may exceed 10 GB.

Gzip runs only because Serilog deletes rolled uncompressed files. `ArchiveHooks.OnFileDeleting` copies the doomed `.log` to `{filename}.gz` in the same directory, then Serilog deletes the uncompressed original. File `retainedFileCountLimit` is **1 uncompressed file** (the active day). That is not gzip-archive retention: Serilog's matcher is `{ApplicationName}-*.log` and does not select `.log.gz`. Historical `.gz` files stay until an external retention process removes them. The active file is not compressed.

## INN `news` log

`news` is an article-disposition log, not an acceptance-only file. VectorNNTP implements the dispositions that current ingress actually produces:

| Character | Meaning | When emitted |
|-----------|---------|--------------|
| `+` | accepted/carried | after dequeue for a normal accepted article |
| `j` | accepted but junk | after dequeue when WantTrash junk applies and `LogTrash` is true |
| `-` | deliberately rejected | at the IHAVE/TAKETHIS/POST decision that produced the NNTP rejection |
| `m` | accepted for moderation | at successful POST moderation submission (does not enter the queue) |

Cancel processing (`c`) is not implemented and is not emitted. INN's informational `?` disposition (isolated CR/LF) is not emitted. `m` is a VectorNNTP disposition for moderated POST; it is not an innd(8) code. This is not a claim of complete INN `news` behaviour.

The INN line format is an application invariant implemented by `InnNewsTextFormatter` in code. Operators cannot change field order, timestamp representation, disposition characters, feed placement, Message-ID placement, rejection formatting, or delimiters through `outputTemplate` or any other appsettings key.

Field order is taken from innd(8) LOGGING and INN `innd/art.c` `ARTlog`: timestamp, disposition, inbound feed/site, Message-ID, article size in bytes. Outbound sites exist only for articles accepted for propagation (`+`); empty Sites then emit INN's unavailable token `?` until egress routing exists. Junk (`j`), rejected (`-`), and moderated (`m`) lines omit the outbound-site field and terminate after size unless a reason is present. Empty inbound feed renders `?` in the inbound-peer position. The inbound feed is the session Transit identifier (`NntpAuthorization.TransitPeerName`) captured onto the news event at decision time; the formatter does not consult a live session.

```text
mon dd hh:mm:ss.mmm + feed <message-id> size ?
mon dd hh:mm:ss.mmm j feed <message-id> size [reason]
mon dd hh:mm:ss.mmm - feed <message-id> size [reason]
mon dd hh:mm:ss.mmm m feed <message-id> size
```

Examples:

```text
Sep 28 08:18:41.398 + giganews <accepted@example> 793311 ?
Sep 28 08:18:41.398 j giganews <junk@example> 741116 newsgroup not carried: alt.binaries.encryptnzb.hotel
Sep 28 08:18:41.398 - giganews <rejected@example> 793311 yEncoding invalid
Sep 28 08:18:41.398 m giganews <moderated@example> 123456
```

Accepted (`+` / `j`) events are emitted by `IncomingSpoolWriterService` after dequeue. Rejected (`-`) events are emitted at the protocol decision that produced the NNTP rejection, because rejected articles never enter the queue. Moderated (`m`) is emitted at the successful moderation decision. The component that decides the article supplies the disposition and, for `-` and `j`, the already-decided operator-facing reason; the formatter only serializes that decision.

The NNTP response code is not a news field and is not rendered. INN's `- feed <message-id> reason` template has no response-code column; a code that appears inside some INN filter reason strings is not used here.

Rejection reasons are short operational journal text decided at the source, for example `message-id invalid`, `date invalid`, `newsgroup not carried`, `article too large`, `article type not permitted`, `queue capacity exceeded`, `yEncoding invalid`. An existing PostFilter reason such as `closed` is written unchanged.

Feed is the inbound Transit identifier already known on the NNTP session (`Authorization.TransitPeerName`, the Transit dictionary key). It is copied onto `NewsLogEvent.Feed` at the decision so the asynchronous writer does not consult a live session. When the session is not a named peer, the inbound field is INN's unavailable token `?`. On `+` only, the field after size is the outbound-site list; empty Sites emit `?` because outbound `SITE` routing is not implemented and is not invented. `j`, `-`, and `m` do not include that field.

`Nntpd:Transit:WantTrash` (TAKETHIS/IHAVE only): when `true`, articles posted only to unknown or RFC 6048 `j` groups are accepted and treated as junk internally without rewriting `Newsgroups:`. The `j` line carries `newsgroup not carried: <group>[, <group>...]` for the unknown header tokens when every listed group is unknown, or `peer-only: <group>[, <group>...]` for the RFC 6048 `PeerOnly` catalogue hits that caused junk. When `false`, unknown/non-carried groups are rejected before enqueue and write `-` with the same `newsgroup not carried: ...` group list. PeerOnly (`j`) groups remain accepted junk. POST is not subject to this policy. Only the groups responsible for the existing decision are listed; a complete Newsgroups header is not dumped.

`Nntpd:Transit:LogTrash` controls accepted-junk `j` lines only. It does not suppress `+`, `-`, or `m`. Accepted articles then continue to the existing /dev/null no-op sink.

Temporary capacity or pre-article responses (`435` not wanted, `436` try later, TAKETHIS `400`, POST `440`) are not article-rejection news events and are not written as `-`.

Operational file behaviour is the same Serilog File/Async contract as application logs and is configured under `Serilog:News` (path, `rollingInterval`, `retainedFileCountLimit`, `fileSizeLimitBytes`, `rollOnFileSizeLimit`, `hooks` compression, `buffered`, Async `bufferSize` / `blockWhenFull`). `Nntpd:LogDir` still resolves the directory; the runtime path is `{LogDir}/news-yyyyMMdd.log` for daily rolling. Changing those Serilog settings changes the news file. Changing them does not change the INN formatter.

A news-log I/O failure is reported through application diagnostics and does not produce a second NNTP response. The dedicated news logger is not written to Console or the application File sink, so normal application logs do not contain INN `news` lines.

High-volume writes use `Serilog.Sinks.Async` (`bufferSize: 50000`, `blockWhenFull: true`) wrapping a buffered File sink. Events are **not dropped**: if the file writer cannot keep up, logging calls block until the queue has space. `Program` still calls `Log.CloseAndFlushAsync()` on shutdown so the application async buffer is flushed. The news logger is disposed with the host / `INewsLogWriter`.

### Change minimum level

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "VectorNNTP.NNTPD": "Verbose"
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

`appsettings.Development.json` may raise the default minimum to Debug for interactive work. Keep `VectorNNTP.NNTPD` at `Verbose` so MEL Trace events still reach the file sink.

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

All platforms use the same Serilog pipeline. `Program` assigns an auto-flush `Console.Out` before Serilog creates the Console sink. Serilog's Console sink writes to `Console.Out` and does not Flush; when stdout is a pipe (IDE capture, redirected output) the runtime may block-buffer that writer. Auto-flush keeps later Information lines (connection accept, lifecycle) visible without waiting for the buffer to fill or the process to exit. DATE and TAKETHIS RX/TX are Debug and appear in the file log, not on the console.

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

PostFilter operational EventIds (2800–2811) and reservation/COMMIT/SPAMD/policy-revision messages are listed in [postfilter.md](postfilter.md#11-observability). Those numeric IDs currently overlap RabbitMQ lifecycle EventIds; match on `PostFilter` message text.

Logging is a human-readable boundary. The current `ILogger` / `LoggerMessageAttribute` APIs require `string` (or other supported structured types) for operational text. Protocol bytes may be converted to a string **once, locally**, at that boundary when human-readable output is required. Do not treat logging as a reason to change protocol representation in the data plane.

## Shutdown flushing

- Serilog hosted integration disposes/flushes with the Generic Host.
- `Program` always calls `Log.CloseAndFlushAsync()` in `finally`.
- Application services may still log during stop; do not dispose Serilog earlier.

## Troubleshooting

| Symptom | Check |
|---------|--------|
| No logs under systemd | Unit `StandardOutput=` inherits; confirm process writes to stdout; `journalctl -u vectornntpd -f` |
| Startup visible, later Information missing in an IDE console | Confirm the console is attached to the process that accepted the TCP connection; `UseAutoFlushConsoleOutput` keeps `Console.Out` flushing when stdout is a pipe |
| Too verbose | Raise `Serilog:MinimumLevel` or category overrides |
| Missing early failure logs | Bootstrap logger must run before `Host.CreateApplicationBuilder` |
| Duplicate lines | Ensure MEL providers were not re-added; `writeToProviders` must remain `false` |
