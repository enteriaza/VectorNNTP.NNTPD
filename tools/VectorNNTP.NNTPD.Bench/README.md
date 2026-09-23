# VectorNNTP.NNTPD.Bench

Real-TCP benchmark runner for VectorNNTP.NNTPD. Select the workload with `--benchmark`.

| Workload | Purpose |
|----------|---------|
| `BENCHIT` (default) | Internal transport/TX baseline. Unadvertised server command. |
| `TAKETHIS` | RFC 4644 STREAM ingest client. Pre-built ~768 KiB article. |

`BENCHIT` is an internal, unadvertised server command retained for transport baselines and
regressions. See repository root [`PERFORMANCE.md`](../../PERFORMANCE.md).

## Prerequisites

1. Build and start the actual `VectorNNTP.NNTPD` host (Release recommended).
2. Confirm listeners on the configured plain/TLS ports.
3. Optionally note the server PID for CPU/working-set sampling:
   `Get-Process VectorNNTP.NNTPD`

## Run server (example)

```powershell
cd src\VectorNNTP.NNTPD
dotnet run -c Release --no-launch-profile
```

## Run BENCHIT

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark BENCHIT `
  --host 198.18.0.66 --plain-port 1199 --tls-port 5633 `
  --server-pid <pid> --runs 2
```

Omitting `--benchmark` keeps the historical BENCHIT behaviour (plain/deflate/tls sweep,
1/10/50 connections, 5s warmup, 60s measure).

## Run TAKETHIS

Plain TCP only. The article payload is built once and reused. The TAKETHIS command
carries a unique Message-ID; the article header Message-ID is static.

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark TAKETHIS `
  --host 127.0.0.1 --port 119 `
  --connections 1 --duration 30

dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark TAKETHIS `
  --host 127.0.0.1 --port 119 `
  --connections 10 --duration 30 --pipeline-depth 256
```

`--duration` is an alias for `--measure-seconds`. `--port` is an alias for `--plain-port`.

## iperf3 baseline

```powershell
# terminal 1
C:\Tools\iperf3.exe -s -B 198.18.0.66 -p 5201

# terminal 2
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- --iperf-only --host 198.18.0.66
```

## Notes

- Uses actual TCP sockets only (no in-process transport doubles).
- Modes: plain, deflate, tls (implicit TLS port), tls+deflate.
- Concurrency: 1 / 10 / 50. Warm-up 5s, measure 60s per scenario (defaults).
- `BENCHIT` is not advertised in CAPABILITIES or HELP.
