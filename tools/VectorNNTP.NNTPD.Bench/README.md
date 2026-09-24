# VectorNNTP.NNTPD.Bench

Benchmark runner for VectorNNTP.NNTPD. Select the workload with `--benchmark`.
BENCHIT, TAKETHIS, and IHAVE are real-TCP clients. CHECK is an in-process session/application measure.

| Workload | Purpose |
|----------|---------|
| `BENCHIT` (default) | Internal transport/TX baseline. Unadvertised server command. Real TCP. |
| `TAKETHIS` | RFC 4644 STREAM ingest client. Pre-built ~768 KiB article. Real TCP. |
| `CHECK` | Frozen depth-16 CHECK pipeline. Session/application (Pipes + fake Redis). Not TCP throughput. |
| `IHAVE` | RFC 3977 serialized IHAVE command. Real TCP to production NNTPD. Production HistoryDB. Real `.artifacts/Articles` corpus, restuffed on the wire. |
| `SPEEDTEST` | VectorNNTP `SPEEDTEST <peer>` diagnostic. Real TCP. Independent of TAKETHIS/IHAVE/CHECK methodology. |

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

## Run CHECK

In-process session/application benchmark of the production CHECK pipeline
(`CheckPipeline.Depth` = 16). Uses `NntpSession`, duplex Pipes, and a fake delayed Redis.
This is **not** a TCP socket throughput measurement. Depth is not a CLI parameter.

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- --benchmark CHECK
```

Workloads and iteration counts match the validation session bench: 2000 CHECKs at 0 ms Redis
delay, 200 CHECKs at 1/2/5 ms. The run fails if responses are out of command order.

## Run IHAVE

Real serialized IHAVE command benchmark. The client connects over TCP to the
running production `VectorNNTP.NNTPD` host and performs:

```
IHAVE <unique-message-id>
← 335
<raw stuffed corpus article>
<CRLF>.<CRLF>
← 235
```

HistoryDB is the production server path (local memory + Redis). The client does
not pipeline. Unexpected `435` / `436` / `437` fails the run. Corpus files are
the destuffed `.artifacts/Articles` catalog, restuffed for the wire. Unique
command Message-IDs keep HistoryDB from treating a later iteration as a duplicate.

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark IHAVE `
  --host 198.18.0.66 --port 1199 `
  --connections 1 --warmup-seconds 5 --measure-seconds 60 --runs 2 `
  --server-pid <pid>
```

CLI defaults match TAKETHIS when flags are omitted (warmup 0 s, one run, 30 s).
Use the flags above to match the BENCHIT/TAKETHIS methodology recorded in
[`PERFORMANCE.md`](../../PERFORMANCE.md).

This is **not** the earlier IHAVE Pipe-reader microbenchmark. That forensic
measure remains documented separately in `PERFORMANCE.md`.

### IHAVE diagnostic timing

`--timing` samples client-visible phases with `Stopwatch.GetTimestamp`. It does
not change the default duration throughput path. Default sample count is 4000.

TAKETHIS `--timing` keeps the pipelined client (same `--pipeline-depth`) and
records send→239 phases per completed `239`. Server pipeline-slot stages are
recorded only when `VECTORNNTP_TAKETHIS_TIMING` is set on the NNTPD process
(off by default; dump on session close under `.artifacts/takethis-timing/`).

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark IHAVE --timing --samples 4000 `
  --host 198.18.0.66 --port 1199 `
  --connections 1 --warmup-seconds 5 --server-pid <pid>
```

Writes `.artifacts/ihave-command-bench/timing.txt` and `timing.csv`.
`235` is queue admission, not worker destuff. HistoryDB Peek is inside
`IHAVE sent → 335` and is not isolated from the 335 RTT.

## Run SPEEDTEST

Exercises the advertised VectorNNTP `SPEEDTEST` extension on the current TCP session.
`--speedtest-peer` is a configured Transit identifier (dictionary key), not `PeerName` and not a host or IP.
The server measures TX of a synthetic payload; outbound Transit initiation is not implemented.

This workload does not change TAKETHIS, IHAVE, or CHECK defaults or results.

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark SPEEDTEST `
  --host 198.18.0.66 --port 1199 `
  --speedtest-peer usenet-ninja
```

Default payload receive (`--speedtest-receive byte`) is a reusable-buffer `Socket.ReceiveAsync` drain that scans for the multiline terminator in bytes. Diagnostic `--speedtest-receive line` keeps the `StreamReader.ReadLineAsync` path. Opt-in `--speedtest-receive raw` drains `--speedtest-bytes` (default 64 MiB) without terminator scan. None of these change NNTPD.

## iperf3 baseline

```powershell
# terminal 1
C:\Tools\iperf3.exe -s -B 198.18.0.66 -p 5201

# terminal 2
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- --iperf-only --host 198.18.0.66
```

## Notes

- BENCHIT, TAKETHIS, and IHAVE use actual TCP sockets (no in-process transport doubles).
- CHECK uses `NntpSession` + duplex Pipes + fake Redis. It is not a TCP throughput measurement.
- IHAVE is serialized (one in-flight IHAVE per connection). `--pipeline-depth` is unused.
- BENCHIT modes: plain, deflate, tls (implicit TLS port), tls+deflate.
- BENCHIT/TAKETHIS concurrency: 1 / 10 / 50. Warm-up 5s, measure 60s per scenario (defaults).
- `BENCHIT` is not advertised in CAPABILITIES or HELP.
