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
│  - Later: NNTP command dispatcher on NntpSession            │
└─────────────────────────────────────────────────────────────┘
```

Accepted connections establish an immutable `ConnectionClientIdentity` (TCP peer + effective client endpoint). When `ProxyHosts` is non-empty and the TCP peer is trusted, HAProxy PROXY v1/v2 is required on the cleartext socket before TLS/NNTP. Untrusted peers keep TCP identity; PROXY-looking bytes are left as application input and never rewrite client identity (intentional mixed-mode policy; exclusive PROXY ports remain a deployment/firewall choice). `NntpSession` exposes the effective client IP/port without re-parsing the transport.

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

NNTP command/session parsing, storage, peering, metrics exporters, and data-plane performance claims are deferred. The transport layer exposes byte-oriented `INntpConnection` pipes only.
