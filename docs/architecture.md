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
│  - Phase 0: PlaceholderApplicationService (no-op)           │
│  - Later: listeners, session managers, storage, etc.        │
└─────────────────────────────────────────────────────────────┘
```

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
- Repeated stop callers await the same shutdown task.
- Shutdown failures are logged and rethrown; state still settles on `Stopped` when possible.
- Watchdog keep-alives stop when shutdown begins.

### Unexpected termination

If `IApplicationService.Execution` faults or completes while `Running`, the manager raises `UnexpectedServiceTermination`. Health becomes unhealthy (watchdog stops). The hosted service requests host stop when `StopHostOnUnexpectedServiceTermination` is enabled, with `BackgroundServiceExceptionBehavior.StopHost`.

## Logging

`ConfigureNntpdLogging()` clears MEL providers, removes the default `ILoggerFactory`, and registers Serilog via `AddSerilog` (`writeToProviders: false`). Framework and application logs share one Serilog pipeline. Details: [logging.md](logging.md).

## Configuration

`NntpdOptions` binds from the `Nntpd` section, including nested `Systemd` options. Validation runs at startup. No NNTP-specific settings exist in Phase 0.2. Serilog is configured under the `Serilog` section.

## Testing strategy

- Unit tests drive lifecycle, service manager, systemd notifier, watchdog, and Serilog exclusivity with deterministic fakes and collecting sinks.
- Host integration tests use `Host.CreateApplicationBuilder` without network listeners.
- Platform-specific service managers are registered but do not require an actual Windows Service or systemd session for tests.
- Real systemd verification is documented separately and must not be claimed from unit tests alone.

## Non-goals (deferred)

Protocol, sockets, storage, peering, metrics exporters, NNTP health probes, and data-plane performance work are deferred so the host foundation remains stable.
