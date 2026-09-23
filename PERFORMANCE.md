# VectorNNTP.NNTPD Performance

## Purpose

This document records the current baseline performance of the VectorNNTP.NNTPD socket/transport
stack using a real TCP benchmark against the production server implementation.

The purpose of this benchmark was to answer:

> Is the production socket/pipe/transport architecture fundamentally limiting NNTP throughput on the target hardware?

The benchmark was deliberately performed before implementing the remaining heavy NNTP data-plane and
storage functionality so that transport performance could be isolated.

This document also records a later **TAKETHIS** STREAM ingest benchmark on the same host, using the
.NET client in `tools/VectorNNTP.NNTPD.Bench`. BENCHIT and TAKETHIS exercise different portions of
the server and are not substitutes for each other.

## Test Environment

| Item | Value |
|------|-------|
| CPU | Intel Core i9-12900KF |
| Cores / threads | 16C / 24T |
| RAM | approximately 32 GB |
| OS | Windows 10.0.26200 |
| Server | VectorNNTP.NNTPD |
| Benchmark client | `tools/VectorNNTP.NNTPD.Bench` |
| Server address | `198.18.0.66` |
| Plain benchmark port | `1199` |
| TLS benchmark port | `5633` |
| TLS | TLS 1.3 |
| Cipher | `TLS_AES_256_GCM_SHA384` |

The IP address and benchmark ports are test-environment details, not protocol requirements.

## BENCHIT

`BENCHIT` is an **internal, intentionally unadvertised benchmark command**.

- It is not advertised through `CAPABILITIES`.
- It is not advertised through `HELP`.
- It is not included in the public command inventory.
- It exists specifically to provide a stable transport benchmark workload.
- It is retained intentionally for future performance-regression testing.

Workload characteristics:

- article payload: `768000` bytes
- equivalent to `750 KiB`
- generated once and held in memory
- immutable static response
- response framing follows NNTP multiline article framing
- response begins with: `220 0 <benchit-static@vectornntp.local>`
- response terminates with the NNTP multiline terminator
- benchmark is non-pipelined
- normal session/command/transport path is exercised
- response is written through the production `PipeWriter`/transport path
- writes occur in chunks of at most 64 KiB
- approximately 12 flushes per response
- not a per-line flush benchmark

This is a **transport-capacity workload**, not a realistic model of article-storage or database
performance.

## Benchmark Methodology

- Real TCP connections to the running production server (no in-process fake transport).
- Warm-up: 5 seconds (excluded from reported throughput).
- Measurement: 60 seconds per scenario.
- Two runs per scenario.
- Concurrency: 1, 10, and 50 connections.
- Modes:
  - plain TCP
  - DEFLATE (`COMPRESS DEFLATE`, RFC 8054 raw DEFLATE)
  - TLS (implicit TLS listener)
  - TLS + DEFLATE
- In-flight requests are allowed to finish cleanly.
- The benchmark does not intentionally cancel responses mid-frame.
- Latency is measured per completed request.
- Logical throughput is based on the 768000-byte article payload × completed requests.
- Wire throughput reflects bytes actually transmitted on the TCP stream after framing,
  compression, and encryption as applicable.

### Client/server CPU contention

The benchmark client and VectorNNTP.NNTPD server execute on the same physical host and therefore
compete for the same CPU, scheduler, memory, cache, and other host resources. The reported
throughput is consequently an end-to-end measurement of the complete benchmark system rather than
an isolated measurement of server-only capacity.

This makes the benchmark conservative as a measurement of isolated server capacity: the NNTP server
does not have exclusive access to the test host's compute resources. However, the benchmark does not
establish a linear relationship between available CPU resources and achievable throughput, so the
measured results must not be multiplied by an assumed factor such as 2×.

The observed CPU utilization across the scenarios demonstrates that the host was not fully
CPU-saturated during the benchmark. However, the benchmark did not independently attribute CPU
consumption between the benchmark client and the NNTP server. Accordingly, this report records the
measured end-to-end throughput and does not extrapolate an unmeasured server-only throughput.

The same-host arrangement means that some portion of the measured host CPU capacity was necessarily
consumed by the benchmark client, so the benchmark does not represent an isolated server-capacity
ceiling. The available headroom is real, but its effect on server-only throughput was not measured.

**Logical vs wire throughput:** logical Gbit/s counts the uncompressed article payload volume.
Wire Gbit/s counts socket-level bytes (read + write) during the measurement window. Under DEFLATE,
wire throughput can be far lower than logical throughput.

## iperf3 Baseline

| Streams | Aggregate |
| ------: | --------: |
|       1 | 37.5 Gbit/s |
|      10 | 262 Gbit/s |
|      50 | 192 Gbit/s |

- iperf3 version: 3.21
- 60-second TCP tests
- target: `198.18.0.66:5201`

iperf3 is a raw TCP throughput reference. BENCHIT performs actual NNTP request/response processing.
NNTP throughput is **not** expected to equal iperf3.

## Results

Complete benchmark table (both runs preserved):

| Mode        | Conn |             Req/s |      Logical Gbps |         Wire Gbps |   Comp |    p50 ms |      p95 ms |      p99 ms |     CPU % |
| ----------- | ---: | ----------------: | ----------------: | ----------------: | -----: | --------: | ----------: | ----------: | --------: |
| plain       |    1 |   2627.3 / 2592.1 |   16.142 / 15.926 |   16.144 / 15.927 |      — |      0.37 |   0.43–0.44 |   0.52–0.55 |       4.5 |
| plain       |   10 | 20325.3 / 20287.0 | 124.879 / 124.644 | 124.888 / 124.653 |      — |      0.49 |   0.56–0.57 |   0.64–0.65 | 41.8–42.5 |
| plain       |   50 | 18839.1 / 18847.5 | 115.748 / 115.799 | 115.756 / 115.807 |      — | 2.58–2.60 |   3.79–3.83 |   4.67–4.91 | 38.3–39.1 |
| deflate     |    1 |   2353.4 / 2392.1 |   14.459 / 14.697 |     0.109 / 0.110 | 133.40 | 0.40–0.41 |   0.48–0.51 |   0.57–0.62 |   5.7–6.4 |
| deflate     |   10 | 14604.6 / 14766.0 |   89.731 / 90.723 |     0.674 / 0.681 | 133.40 | 0.66–0.67 |   0.81–0.82 |        0.90 | 48.5–48.8 |
| deflate     |   50 | 14546.7 / 14634.0 |   89.375 / 89.911 |     0.671 / 0.675 | 133.40 | 3.35–3.37 |   4.52–4.54 |   7.62–7.99 | 51.1–51.4 |
| tls         |    1 |   1777.0 / 1763.9 |   10.918 / 10.837 |   10.934 / 10.853 |      — |      0.54 |   0.67–0.68 |   0.81–0.87 |       5.3 |
| tls         |   10 |   9189.3 / 9372.9 |   56.459 / 57.587 |   56.541 / 57.671 |      — | 1.06–1.07 |   1.25–1.30 |   1.39–1.48 | 44.3–44.7 |
| tls         |   50 | 10945.8 / 10691.1 |   67.251 / 65.686 |   67.349 / 65.781 |      — | 3.38–3.44 | 12.24–12.60 | 31.42–32.31 | 51.3–51.9 |
| tls+deflate |    1 |   2250.5 / 2230.5 |   13.827 / 13.704 |     0.109 / 0.108 | 127.55 |      0.43 |   0.53–0.55 |   0.64–0.67 |   5.6–5.7 |
| tls+deflate |   10 | 13748.8 / 13766.2 |   84.473 / 84.579 |             0.666 | 127.55 |      0.71 |   0.86–0.87 |   0.95–0.97 | 48.0–48.2 |
| tls+deflate |   50 | 13826.4 / 13852.5 |   84.949 / 85.110 |     0.669 / 0.671 | 127.55 | 3.53–3.55 |   4.78–4.82 |  9.55–11.76 | 52.2–53.3 |

## Interpretation

### Plain TCP

- Peak measured logical throughput: approximately 124.9 Gbit/s at 10 connections.
- 50 connections remain above 115 Gbit/s.
- No orders-of-magnitude collapse was observed.
- The 10→50 connection result plateaus rather than scaling linearly.
- Observed host CPU utilization remains below approximately 40–43% in the measured plain scenarios.

### DEFLATE

- Approximately 90 Gbit/s logical throughput at 10 and 50 connections.
- Observed host CPU rises into the ~49–51% range.
- Wire throughput falls to approximately 0.67–0.68 Gbit/s because the synthetic payload is extremely compressible.
- Compression ratio of approximately 133:1 is **not representative of real Usenet articles**.
- It is useful for measuring the CPU/wire behavior of the DEFLATE transport but must not be interpreted as a real-world expected compression ratio.

### TLS

- TLS 1.3 with `TLS_AES_256_GCM_SHA384`.
- Approximately 57.6 Gbit/s at 10 connections.
- Approximately 65–67 Gbit/s at 50 connections.
- TLS introduces substantial but bounded overhead compared with plain TCP.
- No catastrophic CPU saturation or throughput collapse was observed.
- The 50-connection latency tail is materially higher than the 10-connection result and should be retained as an observation, not prematurely classified as a defect.

### TLS + DEFLATE

- Approximately 84.5 Gbit/s at 10 connections.
- Approximately 85.1 Gbit/s at 50 connections.
- Observed host CPU approximately 48–53%.
- Wire throughput approximately 0.67 Gbit/s.
- On this highly compressible synthetic workload, DEFLATE substantially reduces the amount of data passed through TLS/network transmission and therefore the logical throughput of TLS+DEFLATE can exceed TLS-only throughput.
- This does NOT mean DEFLATE is intrinsically faster than TLS.
- It is an artifact of the benchmark workload's extremely high compressibility combined with logical-throughput accounting.

## iperf3 vs NNTP

```text
iperf3, 10 streams:      262 Gbit/s
NNTP plain, 10 clients: 124.9 Gbit/s
```

Approximately **48%** of the iperf3 aggregate throughput at the same concurrency level.

This ratio is a workload comparison, not a transport-efficiency percentage or a required
performance target. iperf3 measures raw TCP bulk throughput, whereas BENCHIT performs complete
NNTP request/response transactions through the production application and transport stack.

This compares:

- raw TCP bulk throughput

with:

- complete NNTP request/response transactions
- application framing
- command processing
- PipeWriter/PipeReader machinery
- transport pumps
- 64 KiB write/flush cadence
- per-request synchronization
- response parsing
- benchmark client request/response handling

Equality with iperf3 is neither expected nor required. The remaining gap is not treated as proof of a
specific bottleneck unless established by direct measurement.

## Transport Verdict

> **Baseline accepted. No transport stop-ship defect identified.**

- The production TCP/pipe/transport stack demonstrates >100 Gbit/s logical NNTP throughput under the benchmark workload.
- Plain TCP reaches approximately half of the raw 10-stream iperf3 ceiling while performing actual NNTP work.
- No orders-of-magnitude socket/pipe failure was observed.
- TLS and DEFLATE remain performant under concurrency.
- Observed host CPU does not saturate during the benchmark.
- The transport layer has demonstrated sufficient throughput and concurrency characteristics under this benchmark to proceed with implementation of the remaining NNTP server functionality without further transport optimization work being required by this baseline.

> **No transport optimization work is planned from this benchmark alone.**

Future optimization should be driven by realistic workloads and measured evidence.

## TAKETHIS

`TAKETHIS` is an RFC 4644 STREAM ingest workload. It is **not** a transport-capacity substitute for
BENCHIT, and BENCHIT is **not** a TAKETHIS ceiling.

The authoritative client is the .NET workload in `tools/VectorNNTP.NNTPD.Bench`
(`--benchmark TAKETHIS`). The historical Python client (`tools/nntp_takethis.py`) is **not** used
for the numbers in this document.

Workload characteristics:

- real TCP to the production `VectorNNTP.NNTPD` host (no in-process fake transport)
- plain TCP only (no DEFLATE, no TLS)
- `MODE STREAM` then pipelined `TAKETHIS`
- article is built once and reused (immutable)
- target body size argument: `768000` bytes (`750 KiB`)
- article body: `767930` bytes
- framed article on the wire: `768054` bytes (headers + body + multiline terminator)
- command + article: `768105` bytes
- TAKETHIS command Message-ID is unique per article
- article-header Message-ID is static so the payload is not rebuilt
- one vectored `Socket.SendAsync` per article (`TAKETHIS <message-id>\r\n` + article)
- does **not** rebuild the ~768 KiB article per transaction
- does **not** use two separate command/article socket writes
- does **not** use `FlushAsync` on the TAKETHIS hot path
- does **not** use the Python client
- bounded pipeline: `256` outstanding TAKETHIS commands per connection
- real `239` / `439` response processing
- no production-server bypass

### TAKETHIS methodology

Comparable timing and concurrency to the BENCHIT baseline:

- Warm-up: 5 seconds. Warm-up article counts are discarded.
- Measurement: 60 seconds per scenario.
- Two runs per scenario.
- Concurrency: 1, 10, and 50 connections.
- Same host and plain port as BENCHIT: `198.18.0.66:1199`.
- `--server-pid` samples the VectorNNTP.NNTPD process (same sampler as BENCHIT).
- The TAKETHIS client does not record per-article latency; p50/p95/p99 are not reported.
- In-flight TAKETHIS commands are allowed a short drain after the measure window.

Throughput definitions (as reported by the harness):

- **TAKETHIS/sec** = articles sent during the measurement window ÷ wall-clock elapsed of the run.
  The harness elapsed includes connect, warmup, measure, and drain (observed 65.0–65.2 seconds for
  these runs). Warm-up sends are excluded from the count.
- **Logical payload Gbit/s** = framed article bytes (`768054`) × articles sent ÷ elapsed.
- **Wire Gbit/s** = command + article bytes actually sent ÷ elapsed.

TAKETHIS/sec is **not** the same unit as BENCHIT req/s. Do not compare them as if they were the
same work item.

The same-host client/server CPU contention described under BENCHIT applies here as well. Reported
throughput is an end-to-end measurement of the complete benchmark system. Isolated server-only
capacity was not measured and must not be extrapolated.

### TAKETHIS results

Complete table (both runs preserved). Every cell is from the live production-host run on
`198.18.0.66:1199`. All runs completed with a 100% `239` response ratio and zero `439`, protocol,
connection, and temporary-`400` errors.

| Mode  | Conn |      TAKETHIS/s |     Logical Gbps |       Wire Gbps |            Sent |             239 | 439 | Err |     CPU % |
| ----- | ---: | --------------: | ---------------: | --------------: | --------------: | --------------: | --: | --: | --------: |
| plain |    1 | 1075.9 / 1072.9 |    6.611 / 6.593 |   6.611 / 6.593 |   70067 / 69784 |   70067 / 69784 |   0 |   0 |   6.1–5.9 |
| plain |   10 | 4374.7 / 4321.4 |  26.880 / 26.553 | 26.882 / 26.554 | 284631 / 281098 | 284631 / 281098 |   0 |   0 |      30.2 |
| plain |   50 | 4152.0 / 4326.5 |  25.511 / 26.584 | 25.513 / 26.586 | 270730 / 282026 | 270730 / 282026 |   0 |   0 | 31.0–30.9 |

Observed max outstanding TAKETHIS commands per connection was 11–14 (limit 256).

Raw harness output: `.artifacts/takethis-performance-md/takethis-results.txt`.

### TAKETHIS interpretation

- Measured single-connection TAKETHIS throughput: approximately 1073–1076 articles/s
  (approximately 6.59–6.61 Gbit/s logical) on this host and workload.
- Measured 10-connection TAKETHIS throughput: approximately 4321–4375 articles/s
  (approximately 26.6–26.9 Gbit/s logical).
- Measured 50-connection TAKETHIS throughput: approximately 4152–4327 articles/s
  (approximately 25.5–26.6 Gbit/s logical).
- The 10→50 connection result plateaus rather than scaling linearly.
- Server-process CPU as reported by the harness remains approximately 6% at 1 connection and
  approximately 30–31% at 10 and 50 connections.
- These figures are measurements of this STREAM/TAKETHIS ingest path on this host. They are not a
  transport ceiling, not a product SLA, and not a claim that VectorNNTP supports a universal Gbit/s
  rate.

## Workload comparison

BENCHIT and TAKETHIS are different workloads. Neither replaces the other.

| Aspect | BENCHIT | TAKETHIS |
| --- | --- | --- |
| What it measures | Production transport/TX path with a static multiline response | Real STREAM/TAKETHIS receive, framing, response, and ingestion processing, plus the relevant transport path |
| Direction of bulk data | Server → client | Client → server |
| Command | Internal unadvertised `BENCHIT` | RFC 4644 `MODE STREAM` + `TAKETHIS` |
| Pipelining | Non-pipelined request/response | Bounded pipeline (256 / connection) |
| Article accounting | `768000`-byte response payload | `768054`-byte framed article |
| Reported work unit | completed requests / s | articles sent / s |
| Modes in this document | plain, DEFLATE, TLS, TLS+DEFLATE | plain TCP only |
| Latency | p50 / p95 / p99 recorded | not recorded by this client |

On this host, the existing BENCHIT plain 10-connection measurement is approximately 124.9 Gbit/s
logical. The TAKETHIS 10-connection measurement is approximately 26.9 Gbit/s logical. Those numbers
are not a ranking and not a statement that one path is “the” ceiling. They count different work.

iperf3 remains a raw TCP reference (10 streams: 262 Gbit/s). It is not a TAKETHIS ceiling and not a
BENCHIT efficiency score.

## Reproduction

The server under test is the production `VectorNNTP.NNTPD` host bound to `198.18.0.66:1199` (and
`5633` for BENCHIT TLS). Start it separately, then run the client from the repository root.

`--benchmark BENCHIT` is the explicit workload selector. Omitting `--benchmark` keeps the historical
BENCHIT default.

```powershell
cd src\VectorNNTP.NNTPD
dotnet run -c Release --no-launch-profile
```

```powershell
# BENCHIT (default workload; --benchmark BENCHIT is optional)
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark BENCHIT `
  --host 198.18.0.66 --plain-port 1199 --tls-port 5633 `
  --server-pid <pid> --runs 2
```

TAKETHIS CLI defaults differ from this document (warmup 0 s, one run, 30 s measure) unless the
flags below are supplied. Use these flags to reproduce the table.

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark TAKETHIS `
  --host 198.18.0.66 --port 1199 `
  --connections 1 --warmup-seconds 5 --measure-seconds 60 --runs 2 `
  --pipeline-depth 256 --server-pid <pid>

dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark TAKETHIS `
  --host 198.18.0.66 --port 1199 `
  --connections 10 --warmup-seconds 5 --measure-seconds 60 --runs 2 `
  --pipeline-depth 256 --server-pid <pid>

dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark TAKETHIS `
  --host 198.18.0.66 --port 1199 `
  --connections 50 --warmup-seconds 5 --measure-seconds 60 --runs 2 `
  --pipeline-depth 256 --server-pid <pid>
```

`--duration` is an alias for `--measure-seconds`. `--port` is an alias for `--plain-port`.

## Limitations

- Static in-memory article.
- Highly compressible synthetic content.
- No article database lookup.
- No overview database lookup.
- No disk/storage latency.
- No realistic article-size distribution.
- No realistic article entropy distribution.
- No realistic client think time.
- BENCHIT is not intended to represent production application throughput.
- TAKETHIS is not intended to represent a universal production ingest SLA.
- TAKETHIS uses a static in-memory article and a unique command Message-ID; it is not a realistic
  article-size or entropy distribution.
- The measured TAKETHIS server does not write incoming article payloads to disk (the persist write
  is inactive). Message-ID hashing and incoming-directory setup still run.
- TAKETHIS/sec is not interchangeable with BENCHIT req/s.
- Logical Gbit/s should not be interpreted as network wire throughput when compression is active.
- Results are specific to the test host, network path, runtime, OS, and benchmark implementation.
- Benchmark client and server share the same physical host, so isolated server-only capacity was not measured.

## Future Performance Work

Future benchmarks should eventually measure realistic workloads such as:

- GROUP
- LIST
- LISTGROUP
- ARTICLE
- HEAD
- BODY
- STAT
- OVER
- HDR
- NEWNEWS
- NEWGROUPS
- POST
- additional streaming/ingest variants beyond the TAKETHIS workload documented above
- article-store/cache behavior
- overview database behavior
- realistic article size and entropy distributions
- mixed concurrent client workloads

No transport micro-optimizations are proposed at this stage. The current objective has been achieved:
establish that the socket/transport architecture is fundamentally sound.

## Validation

- Release build: passed
- `dotnet format --verify-no-changes`: passed
- BENCHIT tests: 4/4 passed
- Full test suite: 576/576 passed on rerun; the initial run had one transient failure in `Compress_Deflate_SessionRoundTrip_Repeated_AuthinfoRejected`, which passed on rerun
- `git diff --check`: clean
- Complete live BENCHIT benchmark suite completed
- Benchmark results retained at: `tools/VectorNNTP.NNTPD.Bench/results-full.txt`
- Complete live TAKETHIS benchmark (1 / 10 / 50 connections, 5 s warmup, 60 s measure, 2 runs)
  completed on 2026-09-23 against production `VectorNNTP.NNTPD` on `198.18.0.66:1199`
- TAKETHIS harness exit code 0 on every cell; 100% `239`; zero `439` / protocol / connection errors
- TAKETHIS raw output retained at: `.artifacts/takethis-performance-md/takethis-results.txt`
