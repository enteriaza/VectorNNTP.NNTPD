# systemd integration verification procedure

Automated unit tests in `VectorNNTP.NNTPD.Tests` cover detection, readiness, watchdog, and shutdown **offline** with fakes. They do **not** prove real systemd behavior.

## Environment used for this Phase 0.1 delivery

| Item | Value |
|------|-------|
| Development OS | Windows 10/11 |
| Real systemd available | **No** |
| Real integration executed | **Not executed** |

The checks below must be run on a Linux host with systemd before claiming production verification.

## Prerequisites

- Linux host with systemd
- Published `VectorNNTP.NNTPD` under `/opt/vectornntp/nntpd`
- Unit installed as `vectornntpd.service`
- Journal access (`journalctl`)

## Verification checklist

1. **Start**
   ```bash
   sudo systemctl start vectornntpd
   sudo systemctl status vectornntpd
   ```
   Expect `Active: active (running)`.

2. **Readiness**
   ```bash
   systemctl show vectornntpd -p ActiveState -p SubState
   journalctl -u vectornntpd -b --no-pager | grep -i ready
   ```
   Expect readiness only after application initialization log lines (lifecycle `Running`).

3. **Status text**
   ```bash
   systemctl show vectornntpd -p StatusText
   ```
   Expect a meaningful status such as running/initialization completed.

4. **Watchdog (optional; enable only for this test)**
   - Set `WatchdogSec=30` in the unit, `daemon-reload`, restart.
   - Confirm keep-alive logs at Debug/Trace only (Information should show activation, not every heartbeat).
   - Confirm `WatchdogTimestamp` advances: `systemctl show vectornntpd -p WatchdogTimestamp`.

5. **Fatal failure surfacing**
   - Induce a fatal supervised service failure in a test build/harness (or temporarily register a faulting `IApplicationService`).
   - Expect host stop, journal critical logs, and systemd restart according to `Restart=`.

6. **Graceful stop**
   ```bash
   sudo systemctl stop vectornntpd
   ```
   Expect `STOPPING` / shutdown logs and exit within `TimeoutStopSec`.

7. **Final state**
   ```bash
   systemctl show vectornntpd -p ActiveState -p Result
   ```
   Expect inactive with `Result=success` for a clean stop.

## Recording results

When executed, record:

- Distro / systemd version
- Publish mode (FDD vs self-contained)
- Whether watchdog was enabled
- Pass/fail for each checklist item
- Journal excerpts for failures

Until that record exists, treat real systemd integration as **unverified**.
