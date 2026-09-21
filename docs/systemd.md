# VectorNNTP.NNTPD — systemd deployment guide

This document covers Linux systemd installation and operations for Phase 0.1+.
It does **not** cover NNTP listeners, TLS, or data-plane networking.

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
