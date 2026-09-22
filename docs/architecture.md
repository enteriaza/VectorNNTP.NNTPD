# VectorNNTP.NNTPD — Phase 0 / 0.1 / 0.2 / 0.3 Architecture

## Goals

Phase 0 establishes a production-shaped host for a long-running NNTP server without implementing the protocol or data plane. Phase 0.1 completes Linux systemd notify/watchdog integration. Phase 0.2 makes Serilog the exclusive logging pipeline. Phase 0.3 removes obsolete Microsoft console formatter configuration left by `AddSystemd()`. Lifecycle, systemd, and logging coordination stay outside future packet-processing paths.

## Component responsibilities

```text
┌─────────────────────────────────────────────────────────────┐
│ Program.cs (composition root)                               │
│  - Bootstrap Serilog, then build HostApplicationBuilder     │
│  - Configure Serilog-only logging                           │
│  - Configure platform hosting (Windows Service / systemd)   │
│  - Register DI + options                                    │
│  - Run host; CloseAndFlush on exit                          │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ SystemdLifecycleNotifier / SystemdWatchdogService           │
│  - STATUS / READY / STOPPING from lifecycle transitions     │
│  - Optional WATCHDOG keep-alives while healthy              │
└───────────────┬─────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────┐
│ NntpdHostedService (IHostedService / BackgroundService)     │
│  - StartAsync → ApplicationLifecycle.StartAsync             │
│  - ExecuteAsync → wait for shutdown / unexpected fault      │
│  - StopAsync → NntpdHostLifetime.RequestShutdownAsync (once) │
└───────────────┬─────────────────────────────┬───────────────┘
                │                             │
                ▼                             ▼
┌───────────────────────────┐   ┌─────────────────────────────┐
│ ApplicationLifecycle      │   │ NntpdHostLifetime             │
│  - Validated state machine│   │  - Single-flight shutdown   │
│  - StateChanged events    │   │  - Unexpected-term → host   │
│  - Start / stop semantics │   │    StopApplication()        │
└─────────────┬─────────────┘   └─────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────┐
│ ApplicationServiceManager                                   │
│  - Deterministic start order / reverse stop order           │
│  - Rollback on startup failure                              │
│  - Bounded shutdown timeout                                 │
│  - Optional Execution monitoring                            │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ IApplicationService implementations                         │
│  - CloudflareDnsReconciliationService (bind resolve + DNS)  │
│  - NntpPlainListenerService (cleartext TCP accept/transport)│
│  - AcmeCertificateService (TLS/ACME when BindPortTls > 0)   │
│  - NntpTlsListenerService (implicit TLS accept/transport)   │
│  - Optional: PlaceholderApplicationService (tests only)     │
│  - NNTP session: greeting, command dispatch, authz gates    │
│  - Later: full command handlers / storage / feeds             │
└─────────────────────────────────────────────────────────────┘
```

Accepted connections establish an immutable `ConnectionClientIdentity` (TCP peer + effective client endpoint). When `ProxyHosts` is non-empty and the TCP peer is trusted, HAProxy PROXY v1/v2 is required on the cleartext socket before TLS/NNTP. Untrusted peers keep TCP identity; PROXY-looking bytes are left as application input and never rewrite client identity (intentional mixed-mode policy; exclusive PROXY ports remain a deployment/firewall choice). `NntpSession` exposes the effective client IP/port without re-parsing the transport.

### NNTP session foundation

`NntpSession` owns the NNTP greeting and command loop over `INntpConnection` pipes. Transport remains responsible for sockets, PROXY, TLS, DEFLATE, and connection lifecycle.

Session concepts (distinct):

- **Mode:** `Unspecified` | `Reader` | `Stream` (`MODE READER`; `MODE STREAM` is RFC 4644 legacy discovery and does **not** change mode)
- **Authentication:** `NntpAuthenticationState` (identity after successful AUTHINFO); pending `AUTHINFO USER` username is separate and does not authenticate
- **Authorization:** immutable `NntpAuthorization` (`IsAuthenticated`, `AuthorizedReader`, `AuthorizedTransit`, `PostingPermitted`, `StreamingPermitted`). Defaults: unauthenticated; streaming and posting denied. Connection-time `Transit:AllowedPeers` may grant `AuthorizedTransit` + `StreamingPermitted` without authentication (peer privilege, not user identity). `MODE STREAM` requires `StreamingPermitted`; `IHAVE`/`CHECK`/`TAKETHIS` require `AuthorizedTransit` (not AUTHINFO). Authentication success applies **only** privileges returned by `INntpAuthenticationProvider` — it does not imply reader/transit/posting/streaming.
- **Dispatch:** `NntpCommandRegistry` + `NntpCommandDispatcher` apply a fixed gate order: resolve → authentication → authorization → mode → handler.

Public/pre-auth commands: `CAPABILITIES`, `MODE READER`, `HELP`, `DATE`, `QUIT`, `STARTTLS`, `COMPRESS DEFLATE`. **AUTHINFO USER/PASS** are implemented (RFC 4643). **TAKETHIS** (RFC 4644) is implemented for transit-authorized sessions (peer ACL or authenticated transit): multiline article receive → bounded in-memory ingestion queue → background `IncomingSpoolWriterService` → `spool/incoming`. `239` means accepted into the ingestion pipeline (not disk persistence). CAPABILITIES advertises `STREAMING`. `MODE STREAM` requires `StreamingPermitted` and returns `203` without changing session state. Command implementations live in dedicated files under `Session/Commands/` (see `docs/commands.md`). Cleartext AUTHINFO is a **server policy** (`Nntpd:AllowCleartextAuth`, default `true`): TLS inactive + policy false → `483` and CAPABILITIES omits `AUTHINFO USER`. TLS connections always permit AUTHINFO USER/PASS. Default DI registration is `DenyAllNntpAuthenticationProvider` (rejects all credentials). `AUTHINFO SASL`, reader/article/posting/`IHAVE`/`CHECK` remain registered placeholders (`500` / `501` after authz gates). `COMPRESS DEFLATE` is implemented (RFC 8054): advertised until active; after activation AUTHINFO/STARTTLS/MODE READER are rejected with `502` and `COMPRESS` is no longer advertised.

### Article ingestion (TAKETHIS)

```text
TAKETHIS
   ↓
read/unstuff multiline article (session receive path)
   ↓
bounded in-memory ingestion queue
   ↓
background IncomingSpoolWriterService
   ↓
spool/incoming
```

`239`/`439` status lines are enqueued on the session's ordered response writer without waiting for network delivery, so pipelined TAKETHIS can continue receiving the next article. Disk I/O is never on the TAKETHIS receive critical path.

AUTHINFO flow:

```text
AUTHINFO USER username
    → pending username (not authenticated)
AUTHINFO PASS password
    → INntpAuthenticationProvider.AuthenticateAsync
    → on success: NntpAuthenticationState + NntpAuthorization (provider-granted flags only)
```

After successful authentication: AUTHINFO commands return `502`; CAPABILITIES omits `AUTHINFO` and `MODE-READER`. Passwords are never logged (dispatcher logs registry keys only).

Invalid lines: empty/malformed → `501`; unknown verb → `500`; known verb/unknown variant → `501`.

### Transport I/O ownership

`NntpConnection` keeps stable application `Input`/`Output` pipes for the connection lifetime. Receive/send pumps talk only to an internal `ConnectionByteTransport`, which owns the current byte stream:

- **Plain:** `Socket` → `NetworkStream(ownsSocket: false)` → transport → pumps → pipes
- **TLS:** `Socket` → `NetworkStream` → `SslStream` → transport → pumps → pipes
- **DEFLATE:** `Socket` → (`SslStream`?) → `NntpDeflateStream` → transport → pumps → pipes

Pumps never call `Socket`/`SslStream` APIs directly. Exactly one stream implementation owns socket I/O at a time. When DEFLATE is active, that owner is a duplex raw-DEFLATE transform over the plain or TLS stream.

### Transport TLS modes

Connections become TLS-protected in one of two ways, sharing the same server authentication options (`ClientCertificateRequired = false`) and certificate-lease lifetime:

1. **Implicit TLS** (`NntpTlsListenerService`): accept → PROXY (when trusted) → `SslStream` authenticate-as-server → TLS transport → pumps.
2. **In-place upgrade** (`INntpConnection.UpgradeToTlsAsync`): accept → PROXY (when trusted) → plain transport + pumps → later upgrade on the **same** TCP socket.

Upgrade sequence (STARTTLS): **pause application reads** (writes still admitted) so ClientHello cannot enter application `Input` → write and flush the `382` response → wait until the send pump is idle (`382` on the wire) → **quiesce writes** → verify application `Input` is empty (true NNTP leftovers only) → acquire certificate lease → `AuthenticateAsServerAsync` on `SslStream` (optional `PrefixedStream` for octets retained if a socket read completed during pause) → publish TLS and resume pumps. `ClientIdentity` is unchanged. Concurrent upgrade / already-TLS / closed-connection calls fail deterministically.

Upgrade failure semantics are split:

- **Precondition refusal** (for example unconsumed plaintext still in application `Input`): upgrade is rejected, the connection remains valid plaintext, no TLS transport is published, and no socket bytes are consumed by TLS.
- **Post-quiescence / TLS failure** (authentication failure, cancellation after quiescence begins, or teardown during handshake): the connection becomes terminal with no plaintext fallback; certificate lease and `SslStream` are disposed.

NNTPD performs **server-side TLS authentication only**. Client certificates are not requested, required, or validated.

The transport exposes TCP→TLS upgrade capability. The NNTP `STARTTLS` command (session layer) writes `382`, discards pipelined plaintext per RFC 8143, then invokes `UpgradeToTlsAsync`.

### Transport DEFLATE compression

The transport supports bidirectional **raw DEFLATE** (RFC 8054 §4 / RFC 1951) as a connection-layer capability via `INntpConnection.UpgradeToDeflateAsync`. The NNTP `COMPRESS` command (`Session/Commands/Compress.cs`) negotiates activation: pause reads → write `206 Compression active` → wait for outbound delivery → `UpgradeToDeflateAsync`.

Stack when both TLS and DEFLATE are active (RFC 8054 layering; TLS negotiated first):

```text
Application (NNTP session)
    ↓
PipeReader / PipeWriter
    ↓
Transport pumps
    ↓
ConnectionByteTransport
    ↓
NntpDeflateStream (raw DEFLATE; when active)
    ↓
SslStream (when TLS active)
    ↓
NetworkStream(ownsSocket: false)
    ↓
Socket
```

PROXY processing remains outside this stack and runs once at accept time. `ConnectionClientIdentity` is unchanged by DEFLATE activation.

Wire format notes:

- Uses `System.IO.Compression.DeflateStream` (raw DEFLATE; negative zlib `windowBits` equivalent).
- Does **not** use zlib wrappers (RFC 1950 / `ZLibStream`) or gzip (`GZipStream`).
- Independent compressor and decompressor state per connection; state is not shared across connections.
- Send-pump `FlushAsync` after each outbound pipe batch forwards to `DeflateStream.FlushAsync`. On .NET 10 this implementation uses DEFLATE sync-flush semantics (compressed bytes become visible to the peer; sliding dictionary retained). Finalization occurs on stream dispose.

Activation reuses the same quiescence model as TLS upgrade (atomic admission + outstanding counts). Preconditions mirror TLS: drain application `Input`; flush any bytes that must remain uncompressed (the COMPRESS `206` response) before calling `UpgradeToDeflateAsync`. Post-quiescence failure is terminal with no uncompressed fallback. DEFLATE after TLS is supported; TLS after DEFLATE is rejected. TLS-level compression is not used.

## Cloudflare DNS reconciliation

Startup order places `CloudflareDnsReconciliationService` first among application services. It resolves eligible bind addresses (including intentional private IPs), reconciles A/AAAA for the generated FQDN via the Cloudflare DNS API, verifies the remote set, and fails startup on any hard error (empty address set, API failure, verification mismatch, cancellation). Configuration validation still never calls Cloudflare.

Next, `NntpPlainListenerService` binds cleartext NNTP listeners (`BindAddress`/`BindPort`) and accepts connections into duplex `PipeReader`/`PipeWriter` transports. It does **not** wait for ACME.

When `BindPortTls > 0`, `AcmeCertificateService` runs next: it ensures the ACME account and a usable TLS certificate (DNS-01 via Cloudflare), then publishes an immutable `SslStreamCertificateContext` for new handshakes. When `BindPortTls` is `0`, that service is idle and performs no ACME work. `NntpTlsListenerService` then binds the implicit-TLS port only if a certificate context is available. Certificate rotation atomically replaces the published context without rebinding listeners or dropping existing TLS sessions.

On shutdown (reverse service order), TLS then plain listeners stop accepting and complete active transports before ACME/DNS cleanup. After other application services stop, Cloudflare DNS cleanup removes **every** DNS record for the exact FQDN (all types), verifies none remain in the Cloudflare API view, and reports failure if cleanup cannot be verified before the graceful-shutdown budget expires. Parent/child/other hostnames are never deleted. The host is authoritative for that exact name only. ACME does not contact Let's Encrypt during shutdown.

Cloudflare multi-record updates are **not atomic**. Each reconcile attempt lists both families, creates all missing A/AAAA records before any deletes, updates kept desired records to managed attributes (`ttl=300`, `proxied=false`), deletes stale/duplicates, then verifies exact content sets plus managed TTL and DNS-only proxy state. Address changes keep create-before-delete staging. Cleanup lists all types for the exact name, deletes by id, then verifies emptiness. The application fails closed on partial/uncertain outcomes and recovers by re-reading remote state on the next attempt (up to 3 attempts with backoff) inside a shared `CloudFlareOperationTimeout` (default 2 minutes) linked with the caller token; HTTP attempts are additionally capped by `min(30s, remaining budget)`. Failed-start cleanup is best-effort and capped at 15 seconds. Same-instance concurrent reconcile/cleanup calls are serialized; cross-process and external DNS managers are not coordinated. Intermediate supersets or missing families may be briefly visible externally. Forced termination may prevent cleanup.

API client and reconciler types live under `Cloudflare/`; bind expansion under `Networking/`. Credentials and raw Cloudflare response bodies are never logged in exception messages.

**Limitation:** A/AAAA publication runs at startup; exact-FQDN removal runs at shutdown. NNTP listeners bind the configured `BindAddress`/`BindPort` (and TLS port when enabled). Recursive caches are not waited out. See [configuration.md](configuration.md).

## systemd integration

- `AddSystemd()` registers official `SystemdLifetime` / `ISystemdNotifier` only when systemd (or `NOTIFY_SOCKET`) is detected. Any Microsoft console formatter options it registers are stripped; Serilog owns console formatting.
- `ISystemdNotifyBridge` wraps the notifier for testability.
- `ISystemdRuntime` exposes Linux / service / notify / watchdog deadline detection.
- `IApplicationHealth` defines watchdog health as: `Running`, no unexpected termination, shutdown not requested.
- No custom SIGTERM/SIGINT handlers; the Generic Host / `SystemdLifetime` own signal handling.
- Watchdog and status notifications are not on the future NNTP data path.

## Dependencies

| Component | Depends on | Must not depend on |
|-----------|------------|--------------------|
| `Program` | Hosting extensions, DI | Lifecycle internals beyond registration |
| `NntpdHostedService` | `ApplicationLifecycle`, `NntpdHostLifetime`, options, logging | Concrete NNTP services |
| `NntpdHostLifetime` | `ApplicationLifecycle`, `IHostApplicationLifetime` | Service manager details |
| `ApplicationLifecycle` | `ApplicationServiceManager`, options, logging | Host types (keeps core testable) |
| `ApplicationServiceManager` | `IApplicationService`, options, logging | Host lifetime |
| `CloudflareDnsReconciliationService` | options, bind resolver, DNS reconciler | Host types |
| `ICloudflareDnsClient` / reconciler | options, `HttpClient` | Lifecycle coordinator |
| `SystemdLifecycleNotifier` | lifecycle, notify bridge, runtime, options | NNTP / sockets |
| `SystemdWatchdogService` | runtime, notify bridge, health, host lifetime | NNTP / sockets |
| `IApplicationService` | BCL + cancellation only | Host, lifecycle coordinator |

## Lifecycle semantics

### Happy path

`Created → Starting → Running → Stopping → Stopped`

### Startup failure / cancellation

1. `ApplicationServiceManager` stops already-started services in reverse order.
2. `ApplicationLifecycle` transitions `Starting → Stopping → Stopped`.
3. The original exception is rethrown so host startup fails closed.
4. No partially initialized services remain tracked as started.
5. systemd readiness is not reported.

### Shutdown

- Initiated exactly once through `NntpdHostLifetime`.
- Repeated stop callers on `ApplicationLifecycle` await the same shutdown task; concurrent stop/dispose is serialized there.
- `ApplicationServiceManager` rejects overlapping `StartAsync`/`StopAsync` calls (`InvalidOperationException`). Host paths rely on the lifecycle single-flight stop.
- `GracefulShutdownTimeout` is one overall wall-clock budget for the reverse-order stop sequence (also applied to Generic Host `ShutdownTimeout` when configured).
- The manager **awaits** each `IApplicationService.StopAsync` and does not abandon in-flight stops. Cooperative services observe the linked cancel/timeout token. A service that ignores cancellation keeps the manager awaiting until that call returns; process supervisors (host `ShutdownTimeout`, systemd `TimeoutStopSec`) are the kill backstop.
- Removal from `StartedServices` means the awaited stop attempt for that service finished (success, cancel/timeout, or exception)—not that the process has already been killed.
- Shutdown failures and timeouts are logged and rethrown; lifecycle state still settles on `Stopped` when possible. `Stopped` means the shutdown sequence finished (possibly with failures), not that every service cooperated within the budget.
- Watchdog keep-alives stop when shutdown begins.

### Unexpected termination

If `IApplicationService.Execution` faults or completes while `Running`, the manager raises `UnexpectedServiceTermination`. Health becomes unhealthy (watchdog stops). The hosted service requests host stop when `StopHostOnUnexpectedServiceTermination` is enabled, with `BackgroundServiceExceptionBehavior.StopHost`.

## Logging

`ConfigureNntpdLogging()` clears MEL providers, removes the default `ILoggerFactory`, and registers Serilog via `AddSerilog` (`writeToProviders: false`). Framework and application logs share one Serilog pipeline. Details: [logging.md](logging.md).

## Configuration

`NntpdOptions` binds from the `Nntpd` section, including nested `Systemd` options, listener bind settings, Cloudflare DNS settings, and a generated FQDN (`nntpd{ServerId:00}.{DnsSuffix}`).

Mandatory settings that fail startup when missing or invalid: `CloudFlareApiKey`, `CloudFlareZoneId`, and `ServerId` (`1–99`, no default). Validation runs via `ValidateOnStart` / `IValidateOptions<NntpdOptions>` before the host enters the running state. Validation does not bind sockets or call Cloudflare. After validation, `CloudflareDnsReconciliationService` reconciles and verifies A/AAAA for the generated FQDN against resolved bind addresses before other application services start; failure prevents `Running`. Details: [configuration.md](configuration.md). Serilog is configured under the `Serilog` section.

## Testing strategy

- Offline tests live under `tests/VectorNNTP.NNTPD.Tests/`, grouped by production subsystem (`Cloudflare/`, `Core/`, `Hosting/Systemd/`, `Configuration/`, `Networking/`, `Logging/`), with shared `Fixtures/` and `TestDoubles/`.
- Unit tests drive lifecycle, service manager, systemd notifier, watchdog, and Serilog exclusivity with deterministic fakes and collecting sinks.
- Host integration tests use `Host.CreateApplicationBuilder` without network listeners.
- Platform-specific service managers are registered but do not require an actual Windows Service or systemd session for tests.
- Real systemd verification is documented separately and must not be claimed from unit tests alone.

## Non-goals (deferred)

NNTP article/group data plane, posting, `IHAVE`/`CHECK`, AUTHINFO SASL, and account backends beyond `INntpAuthenticationProvider` remain deferred. AUTHINFO USER/PASS, COMPRESS DEFLATE (RFC 8054), TAKETHIS streaming ingestion (RFC 4644), and the session authorization gates are in place.
