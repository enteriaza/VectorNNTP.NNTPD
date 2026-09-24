# VectorNNTP.NNTPD

High-performance, mode-switching NNTP server host targeting sustained throughput exceeding 40 Gbps.

**Current phase: Phase 0.3 — remove obsolete MEL console formatter configuration** (Serilog remains exclusive). This repository provides the generic-host composition root, explicit application lifecycle, application-service orchestration, configuration, Serilog-exclusive structured logging, Windows Service support, and production-oriented systemd notify/watchdog integration. NNTP protocol handling and networking remain deferred.

## Prerequisites

- .NET SDK 10.0 or later (latest stable non-preview)
- Windows, Linux, or macOS for interactive development
- Windows (for Windows Service) or Linux with systemd (for service installation)

## Solution structure

```text
VectorNNTP.NNTPD.sln
Directory.Build.props
src/
  VectorNNTP.NNTPD/
    Program.cs                 # Composition root
    Configuration/             # NntpdOptions + SystemdOptions + validation
    Cloudflare/                # DNS API client + reconciliation
    Networking/                # Bind-address resolution / IP eligibility
    Core/                      # Lifecycle, service manager, abstractions
    Hosting/                   # Generic Host / Windows Service / systemd
      Systemd/                 # Notify bridge, readiness, watchdog, health
    Logging/                   # Serilog host integration + log helpers
tests/
  VectorNNTP.NNTPD.Tests/
deploy/
  systemd/vectornntpd.service  # Example unit file
docs/
  architecture.md
  configuration.md
  logging.md
  systemd.md
  systemd-integration-verification.md
  standards/rfcs/               # RFC reference library
```

## Build and test

```bash
dotnet restore VectorNNTP.NNTPD.sln
dotnet build VectorNNTP.NNTPD.sln -c Release
dotnet test VectorNNTP.NNTPD.sln -c Release --no-build
dotnet format VectorNNTP.NNTPD.sln --verify-no-changes
```

## Running interactively

```bash
dotnet run --project src/VectorNNTP.NNTPD
```

Stop with Ctrl+C (SIGINT) or SIGTERM. The host initiates a single graceful shutdown of application services. systemd notify/watchdog remain inactive outside a systemd notify environment.

## Running as a Windows Service

Publish and install (elevated PowerShell):

```powershell
dotnet publish src/VectorNNTP.NNTPD -c Release -o C:\Services\VectorNNTP.NNTPD
New-Service -Name "VectorNNTP.NNTPD" `
  -BinaryPathName "C:\Services\VectorNNTP.NNTPD\VectorNNTP.NNTPD.exe" `
  -DisplayName "VectorNNTP NNTPD" `
  -StartupType Automatic
Start-Service VectorNNTP.NNTPD
```

The process uses `Microsoft.Extensions.Hosting.WindowsServices`. Interactive console execution continues to work on developer machines.

## Running under systemd

See **[docs/systemd.md](docs/systemd.md)** for full install, operations, and troubleshooting.

Quick path:

```bash
dotnet publish src/VectorNNTP.NNTPD -c Release -r linux-x64 --self-contained true -o /tmp/vectornntpd-publish
sudo mkdir -p /opt/vectornntp/nntpd
sudo cp -a /tmp/vectornntpd-publish/. /opt/vectornntp/nntpd/
sudo cp deploy/systemd/vectornntpd.service /etc/systemd/system/vectornntpd.service
sudo systemctl daemon-reload
sudo systemctl enable --now vectornntpd
sudo systemctl status vectornntpd
sudo journalctl -u vectornntpd -f
```

The example unit uses `Type=notify`. Readiness is reported after application lifecycle reaches `Running`, not merely because the Generic Host constructed. Optional `WatchdogSec=` is documented but left disabled in the example until operators verify readiness on target hosts.

**Real systemd integration was not executed in the Windows development environment used for this phase.** Follow [docs/systemd-integration-verification.md](docs/systemd-integration-verification.md) on a Linux host.

## Lifecycle state machine

```text
Created → Starting → Running → Stopping → Stopped
                ↘___________↗
```

- State transitions are explicit, validated, and observable via `StateChanged`.
- Invalid transitions throw `InvalidOperationException`.
- Startup failure or cancellation moves `Starting → Stopping → Stopped` after rolling back partially started services.
- Repeated stop requests are idempotent.
- Lifecycle operations are serialized; concurrent unsafe execution fails predictably.

## Startup and shutdown behavior

| Scenario | Behavior |
|----------|----------|
| Successful startup | Options validated; DNS reconciliation verifies A/AAAA for `{Fqdn}`; services start in registration order; state becomes `Running`; systemd `READY=1` (when notify enabled). |
| Startup failure | Started services stop in reverse order (including exact-FQDN DNS cleanup when DNS ownership was active); state becomes `Stopped`; exception is rethrown (host start fails); no readiness. |
| Startup cancellation | Same rollback path; `OperationCanceledException` propagates. |
| Graceful shutdown | `STOPPING=1` (when enabled); reverse-order stop under one overall `GracefulShutdownTimeout` budget; DNS service removes **all** records for the exact `{Fqdn}` after other app services stop (API-verified; recursive caches not waited out). |
| Shutdown timeout | Timeout is logged and surfaced as `TimeoutException`; DNS cleanup may be incomplete and is not claimed successful; state still ends in `Stopped`. |
| Unexpected service termination | Logged as critical; watchdog keep-alives stop; host stop is requested when configured. |
| Ctrl+C / SIGTERM | Host lifetime initiates a single shutdown request (no custom competing signal handlers). |

## Configuration options

Section: `Nntpd` (`appsettings.json` / environment variables / command line). See **[docs/configuration.md](docs/configuration.md)** for bind addresses, Cloudflare secrets, and FQDN generation.

| Option | Default | Description |
|--------|---------|-------------|
| `ApplicationName` | `VectorNNTP.NNTPD` | Display name for logs and Windows Service metadata |
| `GracefulShutdownTimeout` | `00:00:30` | Overall wall-clock bound for application-service shutdown (also applied to host shutdown when configured) |
| `StartupTimeout` | `null` | Optional bound for startup; `null` means host cancellation only |
| `StopHostOnUnexpectedServiceTermination` | `true` | Request host stop when a service execution faults/completes while `Running` |
| `Systemd:EnableWatchdog` | `true` | Allow watchdog keep-alives when systemd configured a deadline |
| `Systemd:ReportLifecycleStatus` | `true` | Send `STATUS=` on lifecycle changes |
| `Systemd:NotifyReadyOnApplicationRunning` | `true` | Send `READY=1` at lifecycle `Running` |
| `Systemd:NotifyStoppingOnApplicationShutdown` | `true` | Send `STOPPING=1` when shutdown begins |
| `Systemd:WatchdogIntervalFraction` | `0.5` | Heartbeat interval as a fraction of systemd’s deadline |
| `BindAddress` | `["*"]` when omitted | Listen addresses / wildcards; explicit IPs must be local NIC addresses |
| `BindPort` | `119` | Cleartext TCP port (`1–65535`) |
| `BindPortTls` | `0` | TLS TCP port; `0`/unset disables TLS; `1–65535` enables |
| `CloudFlareApiKey` | _(env only)_ | **Required** secret; set `nntpd__cloudflareapikey` — missing/blank fails startup; never commit |
| `CloudFlareZoneId` | _(configured)_ | **Required**; `nntpd__CloudFlareZoneId` may supply it — missing/blank fails startup |
| `DnsSuffix` | `usenet.ninja` | DNS suffix for generated FQDN |
| `ServerId` | _(required; no default)_ | **Required** integer `1–99` (`nntpd__ServerId`); no silent default |
| `Fqdn` | generated | `nntpd{ServerId:00}.{DnsSuffix}` — not independently configurable |

Options are validated at startup via `IValidateOptions<NntpdOptions>` and data annotations (`ValidateOnStart`) before the application enters `Running`.

## Logging

Serilog is the exclusive logging implementation. Application code uses `ILogger<T>`; the Generic Host registers `SerilogLoggerFactory` only (Microsoft Console/Debug/EventLog providers are cleared). Microsoft console formatter options that `AddSystemd()` may register are removed; Serilog owns console formatting.

See **[docs/logging.md](docs/logging.md)** for bootstrap logging, configuration, journald filtering, and shutdown flushing.

```bash
Serilog__MinimumLevel__Default=Debug
sudo journalctl -u vectornntpd -f
```

## Current scope

**In scope (Phase 0 / 0.1 / 0.2 / 0.3)**

- .NET Generic Host composition root
- Explicit application lifecycle
- Application service manager
- Options pattern + validation
- Serilog-exclusive structured logging (no Microsoft console formatter configuration)
- Windows Service hosting
- systemd detect / notify / readiness / optional watchdog
- Example unit file + deployment docs
- Automated offline tests for lifecycle, systemd, logging, bind-address resolution, and Cloudflare DNS reconciliation/cleanup (mocked API; no live DNS)

**Explicitly deferred**

- NNTP protocol parsing and command handling
- Network listeners, sockets, and TLS
- Article storage, indexing, and peering
- Throughput optimizations and the 40+ Gbps data plane
- Metrics servers / telemetry exporters
- NNTP-specific health checks for watchdog

See [docs/architecture.md](docs/architecture.md) for component responsibilities and the [byte-oriented protocol data-plane rule](docs/architecture.md#byte-oriented-protocol-data-plane).
