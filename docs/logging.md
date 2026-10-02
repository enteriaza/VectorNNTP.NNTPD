# VectorNNTP.NNTPD — Logging (Serilog)

Serilog is the **only** logging implementation. Application code continues to use
`Microsoft.Extensions.Logging.ILogger<T>` abstractions; those resolve to Serilog sinks.

## Abstractions vs implementation

| Layer | Role |
|-------|------|
| `ILogger<T>` / `ILoggerFactory` | Application and framework logging API |
| `SerilogLoggerFactory` | Exclusive MEL factory registered by `AddSerilog` |
| Serilog sinks | Destinations (console Information+ → stdout → journald; file Debug+ under `Nntpd:LogDir`) |

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

Serilog is configured under the `Serilog` section in `VectorNNTP.NNTPD.json` (and environment-specific files).

Console and file have **separate** minimum levels. Do not raise the global / `VectorNNTP.NNTPD` minimum to `Information` — that would starve the file sink of Debug events.

| Sink | Minimum | Purpose |
|------|---------|---------|
| Console | Information+ | Interactive / journald operational use |
| File | Debug+ | Full diagnostics, including TAKETHIS RX/TX |

There is one source of truth per operational setting. `Serilog:WriteTo` in `VectorNNTP.NNTPD.json` owns File/Async/Archive **arguments**. `Nntpd:LogDir` owns the directory. Code does not re-declare rolling, retention, async buffer, or minimum-level values.

| Setting | Source of truth |
|---------|-----------------|
| Console minimum / template | `Serilog:WriteTo` Console args |
| File minimum / template / rolling / retention / buffered / `flushToDiskInterval` / size limit | `Serilog:WriteTo` Async → File args |
| Async buffer / `blockWhenFull` | `Serilog:WriteTo` Async args |
| Gzip + `CompressionLevel.Fastest` | File `hooks` string → `NntpdSerilogHooks.DailyGzipFastest` |
| Log directory | `Nntpd:LogDir` |

`ConfigureNntpdLogging` creates `Nntpd:LogDir` and overwrites the File `path` so the JSON placeholder (`logs/VectorNNTP.NNTPD-.log`) is never the runtime path. Serilog.Settings.Configuration 10.0.1 cannot expand `Nntpd:LogDir` into `path`. Relative `LogDir` values resolve through Common `ApplicationLocalPath.ResolveApplicationLocalPath` against `AppContext.BaseDirectory`. Relative `Nntpd:AcmeStateDir` values resolve through the ACME wrapper `ResolveAcmeStateDir`, which delegates to the same helper.

`ArchiveHooks` cannot be constructed from JSON scalars. The File `hooks` argument is the Settings.Configuration type/member string `VectorNNTP.NNTPD.Logging.NntpdSerilogHooks::DailyGzipFastest, VectorNNTP.NNTPD` (`CompressionLevel.Fastest`, no archive count limit). That factory is the only File/Archive construction left in code.

Daily rolling uses Serilog `rollingInterval: Day` (local midnight). The active file is `{entry assembly name}-yyyyMMdd.log` (for example `VectorNNTP.NNTPD-20260925.log` when the process is VectorNNTP.NNTPD). `fileSizeLimitBytes` is JSON `null` and `rollOnFileSizeLimit` is `false` so a single day may exceed 10 GB. `buffered` stays true. `flushToDiskInterval` is `00:00:01`, so Serilog wraps the rolling file sink with `PeriodicFlushToDiskSink` and flushes whichever file is current about once per second. The same interval is set on `Serilog:News` and `Serilog:Inpaths`.

Gzip runs only because Serilog deletes rolled uncompressed files. `ArchiveHooks.OnFileDeleting` copies the doomed `.log` to `{filename}.gz` in the same directory, then Serilog deletes the uncompressed original. The application File sink `retainedFileCountLimit` is **14 uncompressed daily files**. That is not gzip-archive retention and is not the Path-survey (`inpaths`) limit: Serilog's matcher is `{entry assembly name}-*.log` and does not select `.log.gz`. A rolled application log is gzipped when it falls outside those 14 uncompressed days. Historical `.gz` files stay until an external retention process removes them. The active file is not compressed.

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

Accepted (`+` / `j`) events are emitted by ingestion workers (owned by `IncomingSpoolWriterService`) after a confirmed OverviewDB RabbitMQ publish. Rejected (`-`) events are emitted at the protocol decision that produced the NNTP rejection, because rejected articles never enter the queue. Moderated (`m`) is emitted at the successful moderation decision. The component that decides the article supplies the disposition and, for `-` and `j`, the already-decided operator-facing reason; the formatter only serializes that decision.

The NNTP response code is not a news field and is not rendered. INN's `- feed <message-id> reason` template has no response-code column; a code that appears inside some INN filter reason strings is not used here.

Rejection reasons are short operational journal text decided at the source, for example `message-id invalid`, `date invalid`, `newsgroup not carried`, `article too large`, `article type not permitted`, `queue capacity exceeded`, `yEncoding invalid`. An existing PostFilter reason such as `closed` is written unchanged.

Feed is the inbound Transit identifier already known on the NNTP session (`Authorization.TransitPeerName`, the Transit dictionary key). It is copied onto `NewsLogEvent.Feed` at the decision so the asynchronous writer does not consult a live session. When the session is not a named peer, the inbound field is INN's unavailable token `?`. On `+` only, the field after size is the outbound-site list; empty Sites emit `?` because outbound `SITE` routing is not implemented and is not invented. `j`, `-`, and `m` do not include that field.

`Nntpd:Transit:WantTrash` (TAKETHIS/IHAVE only): when `true`, articles posted only to unknown or RFC 6048 `j` groups are accepted and treated as junk internally without rewriting `Newsgroups:`. The `j` line carries `newsgroup not carried: <group>[, <group>...]` for the unknown header tokens when every listed group is unknown, or `peer-only: <group>[, <group>...]` for the RFC 6048 `PeerOnly` catalogue hits that caused junk. When `false`, unknown/non-carried groups are rejected before enqueue and write `-` with the same `newsgroup not carried: ...` group list. PeerOnly (`j`) groups remain accepted junk. POST is not subject to this policy. Only the groups responsible for the existing decision are listed; a complete Newsgroups header is not dumped.

`Nntpd:Transit:LogTrash` controls accepted-junk `j` lines only. It does not suppress `+`, `-`, or `m`. Accepted articles then continue to the existing /dev/null no-op sink.

Temporary capacity or pre-article responses (`435` not wanted, `436` try later, TAKETHIS `400`, POST `440`) are not article-rejection news events and are not written as `-`.

Operational file behaviour is the same Serilog File/Async contract as application logs and is configured under `Serilog:News` (path, `rollingInterval`, `retainedFileCountLimit`, `fileSizeLimitBytes`, `rollOnFileSizeLimit`, `hooks` compression, `buffered`, `flushToDiskInterval`, Async `bufferSize` / `blockWhenFull`). `Nntpd:LogDir` still resolves the directory; the runtime path is `{LogDir}/news-yyyyMMdd.log` for daily rolling. Changing those Serilog settings changes the news file. Changing them does not change the INN formatter.

A news-log I/O failure is reported through application diagnostics and does not produce a second NNTP response. The dedicated news logger is not written to Console or the application File sink, so normal application logs do not contain INN `news` lines.

High-volume writes use `Serilog.Sinks.Async` (`bufferSize: 50000`, `blockWhenFull: true`) wrapping a buffered File sink. Events are **not dropped**: if the file writer cannot keep up, logging calls block until the queue has space. `Program` still calls `Log.CloseAndFlushAsync()` on shutdown so the application async buffer is flushed. The news logger is disposed with the host / `INewsLogWriter`.

## Path-survey (`inpaths`) log

`news` is an INN `ARTlog`-compatible disposition journal. It does **not** contain Path headers and is not Path-survey input. That is intentional: the news line contract is unchanged.

The Path-survey stream is a separate durable file, analogous to an INN `WP` Path feed, written beside news:

```text
{LogDir}/news-yyyyMMdd.log
{LogDir}/inpaths-yyyyMMdd.log
```

Each observation is one line produced from the canonical `ArticleRecord.Path` already materialized by the existing ingestion pipeline:

```text
Path: <canonical-path>
```

The value is the Path header bytes on the CanonicalV1 record. NNTPD does not reconstruct Path from the inbound peer, Message-ID, Newsgroups, FQDN, or any other metadata, and does not invent hops such as `giganews!nntpd01!not-for-mail`. Empty or missing `ArticleRecord.Path` follows the existing field-table semantics (empty span) and still writes `Path: ` plus the line terminator. Message-ID, Newsgroups, article size, timestamp, inbound peer, disposition, news reason, and ArticleId are not Path-survey fields.

`IncomingSpoolWriterService` workers write the observation after a confirmed OverviewDB publish, from the same CanonicalV1 queued `ArticleRecord` used for news `+`/`j`. Articles that never become a CanonicalV1 queued record (protocol rejections, moderated POST that never enters the queue) are not surveyed. Path-survey writes are independent of news: junk with `LogTrash=false` still records Path; a news-log failure still records Path; a Path-survey failure still writes news and still persists.

Observations are appended sequentially to disk. They are not aggregated into ninpaths statistics at write time, not retained in an in-memory collection, and not a cache. Restarting NNTPD leaves previous Path observations on disk. After daily rotation, a background ninpaths worker streams each **completed** uncompressed file when `Nntpd:Top1000` has at least one mailbox.

The Path-survey line format is an application invariant implemented by `InnPathSurveyTextFormatter`. Operators cannot change the `Path: ` prefix, the Path bytes, or the terminator through `outputTemplate` or any other appsettings key. Operational file behaviour is the same Serilog File/Async contract as news and is configured under `Serilog:Inpaths`. `Nntpd:LogDir` still resolves the directory.

High-volume writes use `Serilog.Sinks.Async` (`bufferSize: 50000`, `blockWhenFull: true`) wrapping a buffered File sink. Events are not dropped: if the file writer cannot keep up, logging calls block until the queue has space. The dedicated Path-survey logger has source context `VectorNNTP.NNTPD.Inpaths` and is not written to Console, the application File sink, or the news file.

`Serilog:Inpaths:retainedFileCountLimit` is **1 uncompressed file** (the active day) so Serilog's delete callback runs at daily rotation. That callback is the completed-file handoff:

```text
active inpaths log
      |
      | daily rotation
      v
completed uncompressed inpaths log
      |
      +----> ICompletedPathSurveyFileHandler (open + enqueue; does not parse)
      |         |
      |         v
      |      NinpathsProcessingService (background stream → !!NINP → IEmailService)
      |
      +----> NntpdSerilogHooks.DailyGzipFastest
      |
      v
Serilog deletes the uncompressed original
historical {filename}.gz retained (this hook has no archive count limit)
```

The handler sees the uncompressed completed file **before** gzip. It opens the file with share-read/write/delete so gzip and Serilog deletion can proceed while the worker still reads. The hook does not delete that file; Serilog does after gzip returns. Handler failure is logged and does not skip gzip or change ingestion. News continues to use `DailyGzipFastest` directly and is not part of this handoff.

When `Nntpd:Top1000` is missing, null, or empty (or only whitespace), ninpaths is disabled and the completed file is not opened for reporting. When recipients remain, the worker streams the file with bounded memory (unique sites and relations only), formats the INN 3.1.1 compact dump (`!!NINP` / `!!NLREC` / `!!NLEND`), and sends one `IEmailService` message to every recipient. Subject is `inpaths {Fqdn}`. The source file is not attached. Ninpaths failures are logged and do not affect NNTP, article ingestion, RabbitMQ handoff, persistence, news logging, or gzip.

A Path-survey I/O failure is reported through application diagnostics (`SpoolLogMessages.PathSurveyFailed`) and does not produce an NNTP response, reject an accepted article, stop RabbitMQ handoff, stop persistence, or affect the news log.

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

`VectorNNTP.NNTPD.Development.json` may raise the default minimum to Debug for interactive work. Production sets `VectorNNTP.NNTPD` and the File sink to `Debug` so TAKETHIS RX/TX reach the file without Console Debug noise. Raise the override to `Verbose` only when MEL Trace must also reach the file sink.

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

Always-on one-minute application telemetry (`ApplicationTelemetryService`) emits Information EventIds 2400–2406: HistoryDb (2400), TransitIngressQueue (2401), ActiveSessions (2402), per-peer TransitPeer (2403), IngestionWorker histograms (2404), OverviewDbHandoff publish/confirm histograms (2405), and TakeThisPipeline occupancy/wait histograms (2406). These are interval counters and bucketed percentiles, not per-article logs. RabbitMQ.Client publisher-confirmation tracking fuses send and confirm into one `BasicPublishAsync` await, so 2405 `pub_p95_ms` and `confirm_p95_ms` are the same fused duration. Publisher confirms remain enabled.

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
