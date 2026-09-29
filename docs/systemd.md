# VectorNNTP — systemd deployment guide

This document covers Linux systemd installation and operations for VectorNNTP.NNTPD and VectorNNTP.BackFiller.
It does **not** cover NNTP listeners, TLS data-plane networking, RabbitMQ topology, Article Work, or VATP protocol details.

NNTPD and BackFiller share the same systemd *operational contract* (Type=notify, READY/STOPPING/watchdog, journald logging) but keep distinct application lifecycles. Do not assume identical internal state machines.

---

# VectorNNTP.NNTPD

## Detection behavior and limitations

| Condition | Behavior |
|-----------|----------|
| Non-Linux OS | systemd notify/watchdog remain inactive. Windows Service registration still applies on Windows. |
| Linux console / interactive | `AddSystemd()` does not activate lifetime/notifier unless `NOTIFY_SOCKET` is set. Ordinary `dotnet run` is unaffected. |
| Linux under systemd | `SystemdHelpers.IsSystemdService()` / `NOTIFY_SOCKET` enable `SystemdLifetime` and `ISystemdNotifier`. Application logs use Serilog console → stdout → journald. |
| Watchdog | Activated only when `WATCHDOG_USEC` is valid for this process, notify is enabled, and `Nntpd:Systemd:EnableWatchdog` is `true`. |

Limitations:

- Detection uses the official .NET hosting helpers. Extremely locked-down `ProtectProc=` environments on older systemd may fall back incorrectly; prefer systemd ≥ 248 with `SYSTEMD_EXEC_PID`.
- `NOTIFY_SOCKET` is consumed by `Microsoft.Extensions.Hosting.Systemd` when the notifier is constructed and cleared so children do not inherit it. Application code must use `ISystemdNotifier` / `ISystemdNotifyBridge`, not re-read the environment variable later.
- Application configuration never overrides the systemd-supplied watchdog deadline.

## Publishing

### Framework-dependent (smaller publish; requires .NET 10 runtime on the host)

```bash
dotnet publish src/VectorNNTP.NNTPD -c Release -r linux-x64 --self-contained false -o /tmp/vectornntpd-publish
```

### Self-contained (no shared framework required on the host)

```bash
dotnet publish src/VectorNNTP.NNTPD -c Release -r linux-x64 --self-contained true -o /tmp/vectornntpd-publish
```

Choose `linux-arm64` when targeting ARM servers.

## Installation

```bash
sudo useradd --system --home /opt/vectornntp --shell /usr/sbin/nologin vectornntp
sudo mkdir -p /opt/vectornntp/nntpd
sudo cp -a /tmp/vectornntpd-publish/. /opt/vectornntp/nntpd/
sudo chown -R vectornntp:vectornntp /opt/vectornntp
sudo chmod 755 /opt/vectornntp/nntpd/VectorNNTP.NNTPD

sudo cp deploy/systemd/vectornntpd.service /etc/systemd/system/vectornntpd.service
sudo systemctl daemon-reload
sudo systemctl enable --now vectornntpd
```

Optional environment file (`/etc/vectornntp/nntpd.env`):

```bash
DOTNET_ENVIRONMENT=Production
Nntpd__GracefulShutdownTimeout=00:00:30
Nntpd__Systemd__EnableWatchdog=true
```

Uncomment `EnvironmentFile=` in the unit if used. Ensure the file is readable by root and not world-writable.

## Operations

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now vectornntpd
sudo systemctl status vectornntpd
sudo journalctl -u vectornntpd -f
sudo systemctl stop vectornntpd
sudo systemctl restart vectornntpd
```

Useful status checks:

```bash
systemctl show vectornntpd -p ActiveState -p SubState -p Result -p NRestarts
systemctl show vectornntpd -p StatusText -p WatchdogTimestamp -p WatchdogUSec
```

## Configuration (application)

Section `Nntpd:Systemd` in `appsettings.json`:

| Option | Default | Meaning |
|--------|---------|---------|
| `EnableWatchdog` | `true` | Allow keep-alives when systemd configured `WatchdogSec=` / `WATCHDOG_USEC` |
| `ReportLifecycleStatus` | `true` | Send `STATUS=` on lifecycle transitions |
| `NotifyReadyOnApplicationRunning` | `true` | Send `READY=1` when lifecycle reaches `Running` |
| `NotifyStoppingOnApplicationShutdown` | `true` | Send `STOPPING=1` when shutdown begins |
| `WatchdogIntervalFraction` | `0.5` | Heartbeat interval as a fraction of the systemd deadline |

`GracefulShutdownTimeout` should stay consistent with unit `TimeoutStopSec` (timeout stop ≥ graceful timeout).

## Readiness and watchdog semantics

- `Type=notify` is appropriate because the process sends `READY=1` only after `ApplicationLifecycle` reaches `Running` (successful application-service initialization).
- Startup failure never reports ready; host start fails closed.
- Watchdog keep-alives are sent only while healthy: state `Running`, no fatal supervised-service failure, shutdown not started.
- Heartbeat interval is derived from systemd’s deadline (default half). No arbitrary interval is invented when systemd did not configure a watchdog.
- Built-in `SystemdLifetime` may also emit `READY`/`STOPPING` around host lifetime events; duplicate notify messages are harmless. Application-level notify is the readiness contract tied to lifecycle.
- Notification failures from the notify bridge are logged and **rethrown** so a broken notify path fails closed under systemd.

## Logging under systemd

All application and hosting logs flow through Serilog's console sink to stdout; journald collects that output via the service unit. Serilog owns console formatting—Microsoft console formatters are not used. See [logging.md](logging.md).

```bash
sudo journalctl -u vectornntpd -f
sudo journalctl -u vectornntpd -b --priority=err
```

## Troubleshooting

### Failure to start

```bash
sudo systemctl status vectornntpd --full
sudo journalctl -u vectornntpd -b --no-pager
```

Check publish layout, execute bit, shared framework presence (FDD), and options validation errors.

### Readiness notification timeouts

Symptoms: `TimeoutStartSec` exceeded, service never becomes active.

- Confirm `Type=notify` and that the process is the main PID.
- Confirm initialization completes (look for “Application initialization completed” / readiness log lines).
- Increase `TimeoutStartSec` or set `Nntpd:StartupTimeout` if startup is legitimately slow.

### Watchdog-triggered restarts

```bash
systemctl show vectornntpd -p Result -p ExecMainStatus -p NRestarts
journalctl -u vectornntpd -b | grep -i watchdog
```

- Ensure `WatchdogSec` is only enabled after validation.
- Confirm `EnableWatchdog=true` and that the app remains `Running`.
- A fatal supervised service failure stops keep-alives by design.

### Unexpected process termination

```bash
journalctl -u vectornntpd -b --priority=err
```

Look for unexpected service termination and `BackgroundServiceExceptionBehavior.StopHost`.

### Shutdown timeouts

Align `TimeoutStopSec` with `Nntpd:GracefulShutdownTimeout`. Inspect stop logs for which application service stalled.

### Permission problems

Confirm `User=`/`Group=`, ownership of `/opt/vectornntp/nntpd`, and that `ProtectSystem=strict` still allows required read/write paths.

### Running outside systemd

Interactive `dotnet run` must not send watchdog keep-alives. If `NOTIFY_SOCKET` is accidentally set in a developer shell, remove it.

## Security notes

- Do not run as root.
- Do not add network-related capabilities until a later phase introduces the NNTP listener; document those separately.
- Prefer an environment file with restrictive permissions over embedding secrets in the unit file.

---

# VectorNNTP.BackFiller

BackFiller uses the Generic Host + `ApplicationServiceManager` (not NNTPD’s `ApplicationLifecycle` state machine). systemd integration is BackFiller-local under `Hosting/Systemd/` and attaches to Generic Host lifetime events.

## Detection behavior and limitations

| Condition | Behavior |
|-----------|----------|
| Non-Linux OS | systemd notify/watchdog remain inactive. Windows Service registration still applies on Windows. |
| Linux console / interactive | `AddSystemd()` does not activate lifetime/notifier unless `NOTIFY_SOCKET` is set. Ordinary `dotnet run` is unaffected. |
| Linux under systemd | `SystemdHelpers.IsSystemdService()` / `NOTIFY_SOCKET` enable `SystemdLifetime` and `ISystemdNotifier`. Application logs use Serilog console → stdout → journald. |
| Watchdog | Activated only when `WATCHDOG_USEC` is valid for this process, notify is enabled, and `BackFiller:Systemd:EnableWatchdog` is `true`. |

Limitations match NNTPD (NOTIFY_SOCKET consumed by the hosting package; application config never overrides systemd’s watchdog deadline).

## Publishing

```bash
dotnet publish src/VectorNNTP.BackFiller -c Release -r linux-x64 --self-contained false -o /tmp/vectornntp-backfiller-publish
# or:
dotnet publish src/VectorNNTP.BackFiller -c Release -r linux-x64 --self-contained true -o /tmp/vectornntp-backfiller-publish
```

Choose `linux-arm64` when targeting ARM servers.

## Installation

```bash
sudo useradd --system --home /opt/vectornntp --shell /usr/sbin/nologin vectornntp
sudo mkdir -p /opt/vectornntp/backfiller
sudo cp -a /tmp/vectornntp-backfiller-publish/. /opt/vectornntp/backfiller/
sudo chown -R vectornntp:vectornntp /opt/vectornntp
sudo chmod 755 /opt/vectornntp/backfiller/VectorNNTP.BackFiller

sudo cp deploy/systemd/vectornntp-backfiller.service /etc/systemd/system/vectornntp-backfiller.service
sudo systemctl daemon-reload
sudo systemctl enable --now vectornntp-backfiller
```

Optional environment file (`/etc/vectornntp/backfiller.env`):

```bash
DOTNET_ENVIRONMENT=Production
BackFiller__Shutdown__GracePeriodSeconds=30
BackFiller__Systemd__EnableWatchdog=true
```

Uncomment `EnvironmentFile=` in the unit if used. Ensure the file is readable by root and not world-writable.

## Operations

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now vectornntp-backfiller
sudo systemctl status vectornntp-backfiller
sudo journalctl -u vectornntp-backfiller -f
sudo systemctl stop vectornntp-backfiller
sudo systemctl restart vectornntp-backfiller
```

Useful status checks:

```bash
systemctl show vectornntp-backfiller -p ActiveState -p SubState -p Result -p NRestarts
systemctl show vectornntp-backfiller -p StatusText -p WatchdogTimestamp -p WatchdogUSec
```

## Configuration (application)

Section `BackFiller:Systemd` in `appsettings.json`:

| Option | Default | Meaning |
|--------|---------|---------|
| `EnableWatchdog` | `true` | Allow keep-alives when systemd configured `WatchdogSec=` / `WATCHDOG_USEC` |
| `ReportLifecycleStatus` | `true` | Send `STATUS=` on Generic Host lifetime transitions |
| `NotifyReadyOnApplicationRunning` | `true` | Send `READY=1` after all hosted services started successfully |
| `NotifyStoppingOnApplicationShutdown` | `true` | Send `STOPPING=1` when Generic Host graceful shutdown begins |
| `WatchdogIntervalFraction` | `0.5` | Heartbeat interval as a fraction of the systemd deadline |

`BackFiller:Shutdown:GracePeriodSeconds` should stay consistent with unit `TimeoutStopSec` (timeout stop ≥ grace period).

## Readiness and watchdog semantics

- `Type=notify` is appropriate because the process sends `READY=1` only after Generic Host `ApplicationStarted` (all `IHostedService.StartAsync` calls succeeded — including RabbitMQ, provider accounts, NNTP pools, DNS/ACME/Cache Listener via `ApplicationServiceManager`, retention, and Article Work publisher/consumers).
- READY does **not** wait for every external provider NNTP session to be connected; provider sessions continue under their own lifecycle after host start.
- Startup failure never reports ready; host start fails closed and the process exits non-zero.
- Watchdog keep-alives are sent only while healthy: host started, shutdown not begun, no unexpected supervised `IApplicationService` termination.
- Heartbeat interval is derived from systemd’s deadline (default half). No arbitrary interval is invented when systemd did not configure a watchdog.
- Built-in `SystemdLifetime` may also emit `READY`/`STOPPING` around host lifetime events; duplicate notify messages are harmless. Application-level notify is the readiness contract tied to BackFiller’s host start boundary.
- Notification failures are logged and **swallowed** (deliberate difference from NNTPD). Missing watchdog keep-alives still trip systemd when `WatchdogSec=` is configured.

## Logging under systemd

Same Serilog → stdout → journald model as NNTPD.

```bash
sudo journalctl -u vectornntp-backfiller -f
sudo journalctl -u vectornntp-backfiller -b --priority=err
```

## Troubleshooting

### Failure to start

```bash
sudo systemctl status vectornntp-backfiller --full
sudo journalctl -u vectornntp-backfiller -b --no-pager
```

Check publish layout, execute bit, shared framework presence (FDD), options validation, RabbitMQ reachability, and ACME/TLS readiness.

### Readiness notification timeouts

Symptoms: `TimeoutStartSec` exceeded, service never becomes active.

- Confirm `Type=notify` and that the process is the main PID.
- Confirm hosted-service startup completes (look for readiness / “Running: BackFiller hosted services started” status).
- ACME issuance and RabbitMQ connect are on the start path; increase `TimeoutStartSec` when those are legitimately slow (unit default is 180s vs NNTPD’s 120s).

### Watchdog-triggered restarts

```bash
systemctl show vectornntp-backfiller -p Result -p ExecMainStatus -p NRestarts
journalctl -u vectornntp-backfiller -b | grep -i watchdog
```

- Ensure `WatchdogSec` is only enabled after validation.
- Confirm `EnableWatchdog=true` and that the host remains started / not stopping.
- Unexpected `IApplicationService` termination stops keep-alives by design.

### Shutdown timeouts

Align `TimeoutStopSec` with `BackFiller:Shutdown:GracePeriodSeconds`. Inspect stop logs for which hosted or application service stalled.

### Permission problems

Confirm `User=`/`Group=`, ownership of `/opt/vectornntp/backfiller`, and that `ProtectSystem=strict` still allows required read/write paths.

### Running outside systemd

Interactive `dotnet run` must not send watchdog keep-alives. If `NOTIFY_SOCKET` is accidentally set in a developer shell, remove it.

## Deliberate differences from NNTPD

| Topic | NNTPD | BackFiller |
|-------|-------|------------|
| Ready boundary | `ApplicationLifecycle` → `Running` | Generic Host `ApplicationStarted` |
| Lifecycle owner | `ApplicationLifecycle` | `ApplicationServiceManager` + hosted services |
| Notify failure | Log + rethrow | Log + swallow |
| Unit `TimeoutStartSec` | 120 | 180 (ACME + RabbitMQ + Cache Listener on start path) |
| Install path | `/opt/vectornntp/nntpd` | `/opt/vectornntp/backfiller` |
| Unit file | `deploy/systemd/vectornntpd.service` | `deploy/systemd/vectornntp-backfiller.service` |

## Security notes

- Do not run as root.
- Prefer an environment file with restrictive permissions over embedding secrets in the unit file.
- The same `vectornntp` system user may own both NNTPD and BackFiller trees under `/opt/vectornntp`.
