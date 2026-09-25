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

A later **CHECK** measurement is also recorded here. It is a session/application benchmark
(`NntpSession` + Pipes + fake Redis) of the frozen depth-16 CHECK pipeline. It is **not** a TCP
socket throughput measurement and is not a substitute for BENCHIT or TAKETHIS.

A later **IHAVE** measurement is the real serialized IHAVE **command** benchmark: TCP to the
production host, production HistoryDB/Redis, and the RFC 3977 two-stage exchange. It is **not**
the IHAVE Pipe-reader microbenchmark documented separately below.

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

#### A. Historical baseline

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

After the Transit queue became byte-budgeted, a 1-connection TAKETHIS regression
(`--connections 1 --warmup-seconds 5 --measure-seconds 60 --runs 2 --pipeline-depth 256`,
PID 23056) measured 1645.0 / 1705.1 TAKETHIS/s (10.108 / 10.477 Gbit/s), 100% `239`.
That is a historical regression check, not a claimed TAKETHIS optimization. Raw output:
`.artifacts/takethis-performance-md/takethis-1conn-byte-budget.txt`.

#### B. Completed TAKETHIS implementation (1 connection)

This is the production path after the RX handoff: one session RX task owns `Connection.Input`;
it parses TAKETHIS, starts HistoryDB Peek, frames the article with `IHaveArticleReader` into one
owned stuffed-wire buffer (terminator omitted), attaches that buffer to `TakeThisPipeline`
(depth 16), and returns. Peek / enqueue / Remember / ordered `239` run on pipeline completion
workers. TAKETHIS responses use `EnqueueLineImmediateAsync` (no TAKETHIS coalesce batch). Protocol
semantics are unchanged.

1-connection cell only (same methodology as above; 10- and 50-connection cells were not re-run).
Host `198.18.0.66:1199`, NNTPD PID **26364**, timing probe **off**, real HistoryDB/Redis, unique
command Message-IDs (`<bench-CC-SSSSSSSSSSSS@vectornntp.local>`). Command:

```text
dotnet run -c Release --project tools/VectorNNTP.NNTPD.Bench -- `
  --benchmark TAKETHIS --host 198.18.0.66 --port 1199 `
  --connections 1 --warmup-seconds 5 --measure-seconds 60 --runs 2 `
  --pipeline-depth 256 --server-pid 26364
```

| Mode  | Conn |      TAKETHIS/s |     Logical Gbps |       Wire Gbps |            Sent |             239 | 439 | Err |     CPU % |
| ----- | ---: | --------------: | ---------------: | --------------: | --------------: | --------------: | --: | --: | --------: |
| plain |    1 |  836.8 /  665.3 |    5.142 / 4.088 |   5.142 / 4.088 |   54478 / 43277 |   54478 / 43277 |   0 |   0 |   6.2 / 3.7 |

Max outstanding: 29 / 31 (client pipeline limit 256). Response ratio 100% `239`. Zero `439`,
temporary `400`, protocol, and connection errors.

This is **not** a throughput improvement over section A. The measured 1-connection rate is lower
than the historical 1073–1076 TAKETHIS/s cell and lower than the later 1645.0 / 1705.1
byte-budget check. Those earlier cells were different architecture and HistoryDB occupancy; they
are not a like-for-like optimization delta.

The remaining receive-interval cost is transport / input-Pipe refill under the production
`NntpPipeOptions.PauseWriterThreshold` of 64 KiB (resume 32 KiB). That is the session Pipe default
for all commands, not a TAKETHIS implementation defect. HistoryDB Peek, enqueue, Remember, and
`239` enqueue were measured on the completed path at approximately microsecond-to-sub-millisecond
scale; they do not dominate this 768 KiB article workload.

### TAKETHIS interpretation

Historical (section A):

- Measured single-connection TAKETHIS throughput: approximately 1073–1076 articles/s
  (approximately 6.59–6.61 Gbit/s logical) on this host and workload.
- Measured 10-connection TAKETHIS throughput: approximately 4321–4375 articles/s
  (approximately 26.6–26.9 Gbit/s logical).
- Measured 50-connection TAKETHIS throughput: approximately 4152–4327 articles/s
  (approximately 25.5–26.6 Gbit/s logical).
- The 10→50 connection result plateaus rather than scaling linearly.
- Server-process CPU as reported by the harness remains approximately 6% at 1 connection and
  approximately 30–31% at 10 and 50 connections.

Completed implementation (section B, 1 connection, PID 26364):

- Measured 836.8 / 665.3 TAKETHIS/s (5.142 / 4.088 Gbit/s wire) with 100% `239`.
- Client max outstanding 29–31; server window remains `TakeThisPipeline.Depth` = 16.
- Run 2 reused the same command Message-ID sequence on a new connection (established client
  methodology), so HistoryDB occupancy differs between the two runs. Both runs are reported.

These figures are measurements of this STREAM/TAKETHIS ingest path on this host. They are not a
transport ceiling, not a product SLA, and not a claim that VectorNNTP supports a universal Gbit/s
rate.

## CHECK

Production CHECK pipeline depth: **16 (fixed)**. Depth is not configurable and is not a benchmark
parameter.

`CHECK` is a **session/application** benchmark. It exercises the production `NntpSession` CHECK
path (`CheckPipeline`, HistoryDB, response writer) over in-process duplex Pipes with a bench-only
fake Redis. It does **not** open a TCP socket and must not be read as Internet-scale or TCP CHECK
throughput.

The authoritative client is `tools/VectorNNTP.NNTPD.Bench --benchmark CHECK`. Workload parameters
match the frozen validation session bench (`.artifacts/check-historydb-bench/PipelineSessionBench.cs`).

### CHECK methodology

- Benchmark type: session/application (`NntpSession` + Pipes + fake Redis).
- Not measured: TCP socket throughput, TLS, DEFLATE, multi-session concurrency.
- Sessions: one in-process session per workload row.
- Fake Redis: `CheckDelayedRedis` (`Task.Delay` for 1/2/5 ms; optional first-call failure).
- Local HistoryDB: production `HistoryDb` constructed with the public constructor.
- CHECK count: 2000 when Redis delay is 0 ms; 200 when delay is 1, 2, or 5 ms.
- Warm-up: none. `GC.Collect` + `GC.WaitForPendingFinalizers` + `GC.Collect` before each measure
  (same as the validation session bench). Allocation B/op is **not** reported: session setup and
  GC around this harness are not a trustworthy per-CHECK allocation measure.
- Redis delays: 0 / 1 / 2 / 5 ms. Delayed rows are 1 / 2 / 5 ms only for redis-hit, redis-miss,
  and local-hit. Cooldown is 0 ms.
- Concurrency: production CHECK overlap only (`CheckPipeline.Depth` = 16). No extra `Task.Run`.
- Ordering: hard fail if a response Message-ID is not in command send order. A successful run
  therefore reports `Ordered = True` for every row.
- Peak in-flight: `CheckPipeline.PeakOccupied`. The run fails if the peak exceeds 16.

Workload semantics:

| Workload | Local HistoryDB | Redis EXISTS | Expected response |
| --- | --- | --- | --- |
| redis-hit | miss | true | `438` |
| redis-miss | miss | false | `238` |
| local-hit | hit | must not be consulted | `438` |
| cooldown | miss | first EXISTS throws; remaining CHECKs see cooldown | `431` |

### CHECK results

Single Release run on the test host (`Windows 10.0.26200`, Intel Core i9-12900KF). Every row
completed with `Ordered = True`. Allocation figures are omitted.

| Workload | Redis Delay | CHECKs | CHECK/s | Peak In-Flight | Redis EXISTS | Responses | Ordered |
|---|---:|---:|---:|---:|---:|---|---|
| redis-hit | 0 ms | 2000 | 50691 | 1 | 2000 | 438×2000 | True |
| redis-hit | 1 ms | 200 | 1025 | 16 | 200 | 438×200 | True |
| redis-hit | 2 ms | 200 | 990 | 16 | 200 | 438×200 | True |
| redis-hit | 5 ms | 200 | 986 | 16 | 200 | 438×200 | True |
| redis-miss | 1 ms | 200 | 986 | 16 | 200 | 238×200 | True |
| redis-miss | 2 ms | 200 | 984 | 16 | 200 | 238×200 | True |
| redis-miss | 5 ms | 200 | 983 | 16 | 200 | 238×200 | True |
| local-hit | 0 ms | 2000 | 115079 | 1 | 0 | 438×2000 | True |
| local-hit | 1 ms | 200 | 161577 | 1 | 0 | 438×200 | True |
| local-hit | 2 ms | 200 | 264620 | 1 | 0 | 438×200 | True |
| local-hit | 5 ms | 200 | 157704 | 1 | 0 | 438×200 | True |
| cooldown | 0 ms | 2000 | 226817 | 1 | 1 | 431×2000 | True |

Elapsed wall times recorded by the harness (same run): redis-hit 0 ms = 39.5 ms; redis-hit
1/2/5 ms = 195.2 / 202.0 / 202.8 ms; redis-miss 1/2/5 ms = 202.9 / 203.2 / 203.4 ms;
local-hit 0 ms = 17.4 ms; local-hit 1/2/5 ms = 1.2 / 0.8 / 1.3 ms; cooldown = 8.8 ms.

### CHECK interpretation

- CHECK execution is bounded to 16 outstanding operations per session. Delayed Redis workloads
  reached peak in-flight 16. Zero-delay and local-hit workloads completed with peak in-flight 1
  because Redis returned before the next CHECK was admitted.
- Delayed redis-hit and redis-miss throughput on this Windows host was approximately 983–1025
  CHECK/s for 200 commands. The 1 / 2 / 5 ms `Task.Delay` rows did not produce distinct elapsed
  times. These are measured session/application numbers with a fake delayed Redis, not TCP
  throughput.
- local-hit did not consult Redis (`EXISTS = 0`). The 1 / 2 / 5 ms local-hit rows still ignore
  Redis; their CHECK/s variation is from a ~1 ms elapsed window over 200 commands and is not a
  Redis-latency result.
- cooldown: first Redis EXISTS failed; the remaining CHECKs returned `431`; EXISTS count stayed at
  1. Redis was not hammered after cooldown.
- Do not extrapolate these figures to production network throughput.

## IHAVE

`IHAVE` is the **real serialized IHAVE command benchmark**. It is **not** the
IHAVE Pipe-reader / RAW article-receive microbenchmark.

The authoritative client is `tools/VectorNNTP.NNTPD.Bench --benchmark IHAVE`.
That flag now runs the command path:

```
IHAVE <unique-message-id>
← 335
<raw stuffed corpus article>
<CRLF>.<CRLF>
← 235
```

over real TCP to production `VectorNNTP.NNTPD`. HistoryDB is the production
server path (local memory + Redis `198.18.0.70:6379`). IHAVE is not pipelined
(RFC 3977 §6.3.2). Unexpected `435` / `436` / `437` fails the run.

The earlier receive-only Pipe measure remains below as a forensic baseline. It
is not selected by `--benchmark IHAVE`.

Workload characteristics:

- real TCP to the production `VectorNNTP.NNTPD` host (no in-process fake transport)
- plain TCP only (no DEFLATE, no TLS)
- serialized: one in-flight IHAVE per connection (wait for 335, send article, wait for 235)
- production command parser / IHAVE handler / HistoryDB `PeekAsync` + `Remember`
- production raw IHAVE receive (`IHaveArticleReader`), queue admission, downstream destuff
- corpus: `.artifacts/Articles` (10,610 destuffed stored files, catalog order)
- wire preparation: restuff leading dots and append `CRLF . CRLF` (no client destuff)
- prepared in-memory prefix: 368 articles / 268,185,292 wire bytes (256 MiB budget), cycled
- command Message-ID is unique per IHAVE (`<iIIIIIIIIII-CC-SSSSSSSSSSSS@vectornntp.local>`)
- TAKETHIS / CHECK / STREAM are not used
- no production-server bypass

### IHAVE methodology

Comparable timing to the BENCHIT / TAKETHIS 1-connection cell:

- Warm-up: 5 seconds. Warm-up article counts are discarded. Sequence continues so HistoryDB
  does not see a later duplicate.
- Measurement: 60 seconds.
- Two runs.
- Concurrency: 1 connection (IHAVE is serialized). `--connections` is honoured if supplied;
  each connection remains serialized.
- Same host and plain port as BENCHIT/TAKETHIS: `198.18.0.66:1199`.
- HistoryDB / Redis: production `HistoryDb` on the running server (`Redis:Host` `198.18.0.70`,
  port 6379; server log at start: `Redis connection established (PING 0 ms)`).
- `--server-pid` samples the VectorNNTP.NNTPD process (same sampler as BENCHIT/TAKETHIS).
- The IHAVE client does not record per-article latency; p50/p95/p99 are not reported.

Throughput definitions (as reported by the harness, same convention as TAKETHIS):

- **IHAVE/sec** = `235` accepted during the measurement window ÷ wall-clock elapsed of the run.
  The harness elapsed includes connect, warmup, and measure (observed 65.0 seconds). Warm-up
  sends are excluded from the count.
- **Logical payload Gbit/s** = article bytes sent ÷ elapsed (command line excluded).
- **Wire Gbit/s** = command + article bytes actually sent ÷ elapsed.

IHAVE/sec is **not** the same unit as BENCHIT req/s or TAKETHIS/sec. Do not compare them as if
they were the same work item.

The same-host client/server CPU contention described under BENCHIT applies here as well.

### IHAVE results

Complete table (both runs preserved). Live production-host run on `198.18.0.66:1199` on
2026-09-24. Both runs completed with 100% `235` and zero `435` / `436` / `437` / protocol /
connection errors.

| Mode  | Conn | Serialization |      IHAVE/s |     Logical Gbps |       Wire Gbps |            Sent |             235 | 435 | Err |     CPU % |
| ----- | ---: | ------------ | -----------: | ---------------: | --------------: | --------------: | --------------: | --: | --: | --------: |
| plain |    1 | serialized   | 771.9 / 777.3 |    4.500 / 4.532 |   4.501 / 4.532 |   50183 / 50524 |   50183 / 50524 |   0 |   0 |   9.2–9.8 |

Prepared corpus prefix: 368 of 10,610 catalog articles (268,185,292 wire bytes), cycled.
Command line: 54 bytes. Average prepared article on the wire: 728,764 bytes.

Raw harness output: `.artifacts/ihave-command-bench/results.txt`.

### Transit article-queue memory (`TransitQueueMemoryLimit`)

Transit article buffering is **byte-budgeted** rather than fixed at 256 articles.

| Setting | Default | This host during the after-run |
| --- | ---: | ---: |
| `Nntpd:TransitQueueMemoryLimit` | `1073741824` (1 GiB) | `4294967296` (4 GiB, `appsettings.json`) |

The budget is the sum of owned queued article payload lengths
(`InboundArticle.Payload.Length`). It is not process-wide memory. The historical
256-article count bound was only a memory-safety choke and is no longer an
admission limit. A larger queue is intended to absorb bursts between ingress and
downstream workers; it is not a throughput optimization by itself.

### IHAVE after byte-budgeted queue

Same command, same corpus, same host, after replacing the 256-article cap with
the byte budget. Server PID 23056. 100% `235`, zero rejects/errors.

| Mode  | Conn | Serialization |      IHAVE/s |     Logical Gbps |       Wire Gbps |            Sent |             235 | 435 | Err |     CPU % |
| ----- | ---: | ------------ | -----------: | ---------------: | --------------: | --------------: | --------------: | --: | --: | --------: |
| plain |    1 | serialized   | 740.8 / 765.3 |    4.319 / 4.462 |   4.319 / 4.462 |   48179 / 49748 |   48179 / 49748 |   0 |   0 |   9.2–9.6 |

Raw harness output: `.artifacts/ihave-command-bench/results-byte-budget.txt`.

This is **not** an optimization victory. Serialized 1-connection IHAVE/s did not
improve versus the 256-entry baseline above (771.9 / 777.3). The result is
reported as measured.

### IHAVE non-blocking queue admission (final)

IHAVE uses `TransitQueueMemoryLimit` as **non-blocking** backpressure. It does
not wait for queue memory. Temporary inability to accept is `436`.

- Before `335`: `TryProbeCapacity` (any remaining payload byte; no MaxSize
  reservation).
- After receive: `TryAdmit` (atomic reserve of the actual owned payload length).
- Article larger than the entire budget remains `437`.

Same command, same corpus, same host. Server PID 9520. 100% `235`, zero
`435` / `436` / `437` / errors.

| Mode  | Conn | Serialization |      IHAVE/s |     Logical Gbps |       Wire Gbps |            Sent |             235 | 435 | Err |     CPU % |
| ----- | ---: | ------------ | -----------: | ---------------: | --------------: | --------------: | --------------: | --: | --: | --------: |
| plain |    1 | serialized   | 772.8 / 778.2 |    4.506 / 4.537 |   4.506 / 4.537 |   50262 / 50590 |   50262 / 50590 |   0 |   0 |  9.4–10.1 |

Raw harness output: `.artifacts/ihave-command-bench/results-nonblocking-admission.txt`.

This is a regression/validation run, not an IHAVE optimization. IHAVE is
complete at this admission contract.

### IHAVE interpretation

- Baseline (256-article queue): approximately 772–777 articles/s
  (approximately 4.50–4.53 Gbit/s logical) on this host and workload.
- After byte-budgeted queue: approximately 741–765 articles/s
  (approximately 4.32–4.46 Gbit/s logical). Not higher than the baseline.
- After non-blocking IHAVE admission: approximately 773–778 articles/s
  (approximately 4.51–4.54 Gbit/s logical). In the same range as the original
  256-entry command-bench baseline.
- Every accepted IHAVE completed the production HistoryDB peek, raw article receive, queue
  admission, and `235` response. Duplicate Message-IDs were not used.
- Max outstanding was 1 (serialized). IHAVE was not pipelined.
- Server-process CPU as reported by the harness remains approximately 9–10% at 1 connection.
- These figures are measurements of this serialized IHAVE command path on this host. They are
  not a transport ceiling, not a product SLA, and not interchangeable with TAKETHIS/sec or
  the forensic IHAVE Pipe-reader MB/s numbers.

### IHAVE Serialized Command Timing — Diagnostic

This section is **not** the authoritative IHAVE throughput result. It does not replace
the 771.9 / 777.3 IHAVE/s table above. `--benchmark IHAVE --timing` samples client-visible
phases with `Stopwatch.GetTimestamp`. Production IHAVE / HistoryDB / Redis were not
instrumented. `--timing` is off by default and is not used by the duration throughput path.

Configuration (2026-09-24, same host as the command benchmark):

- `--benchmark IHAVE --timing --samples 4000 --host 198.18.0.66 --port 1199 --connections 1 --warmup-seconds 5 --server-pid 29136`
- Real TCP to production `VectorNNTP.NNTPD`; production HistoryDB + Redis `198.18.0.70:6379`
- Same prepared `.artifacts/Articles` prefix (368 articles, restuffed wire in memory)
- Serialized; 5 s warmup not sampled; 4000 completed `235` samples; 0 unexpected rejects
- Clock: `Stopwatch.GetTimestamp` (monotonic)
- Raw output: `.artifacts/ihave-command-bench/timing.txt`, `timing.csv`

**What `235` means:** `IHave.ExecuteAsync` writes `235` after `HistoryDb.PeekAsync`, the
`335` write, `IHaveArticleReader.ReadAsync` (frame + own stuffed wire), `EnqueueAsync`,
and `Remember`. `IhaveArticleInterpreter` destuff/`Article` construction runs later in
`IncomingSpoolWriterService` and is **not** on the `235` path.

Phase timings (microseconds), last clean 4000-sample run:

| Phase | Mean | P50 | P90 | P95 | P99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| command send | 18.8 | 16.0 | 34.0 | 40.0 | 51.0 |
| IHAVE sent → 335 | 273.1 | 228.0 | 359.0 | 425.0 | 590.0 |
| 335 → article sent | 153.4 | 147.0 | 174.0 | 206.0 | 289.0 |
| article sent → 235 | 1086.6 | 567.0 | 1086.4 | 2915.6 | 14979.1 |
| transaction (IHAVE→235) | 1533.3 | 991.5 | 1657.5 | 4284.4 | 15518.2 |

Average sampled article: 728,575 wire bytes. Mean client article send: 38.0 Gbit/s
(729 KiB in 153 µs). That is not the 1.29 ms budget.

An earlier 4000-sample repeat in the same session (also 100% `235`) measured mean
transaction 1233.3 µs (P50 921.0). Tails on `article sent → 235` were already present
(P99 ≈ 12 ms).

Interpretation (measured, not predicted):

- **Most of the serialized transaction is `article sent → 235`**, not HistoryDB.
  That interval is server receive/frame/own + `EnqueueAsync` + `Remember` + `235` write
  + one RTT. P50 is ~0.5–0.6 ms; the mean is pulled up by a long tail (P99 12–15 ms),
  consistent with occasional queue wait (`EnqueueAsync` waits when the 256-deep queue is
  full). Worker destuff is after `235` but can still delay the next `EnqueueAsync`.
- **`IHAVE sent → 335` is ~0.23–0.27 ms mean** (P99 < 0.6 ms). That is the entire
  command parse/dispatch + HistoryDB `PeekAsync` (local miss then Redis `EXISTS`) +
  `335` write + RTT. Isolated Redis latency was **not** measured; there is no
  production HistoryDB timing seam. On this host Redis PING at server start was 0 ms.
  Calling this path “1 ms Redis” is not supported by the client-visible offer time.
- Client article transfer is ~0.15 ms mean and is not the dominant term.
- Command send is ~0.02 ms.

Reconciliation with ~777 IHAVE/s:

- Published 777.3 IHAVE/s uses wall elapsed including 5 s warmup (65.0 s), so
  1e6/777.3 = **1286.5 µs**. Measure-window-only for that run is 50183/60 =
  **836.4 IHAVE/s** → **1196 µs**.
- Diagnostic mean 1233 µs (earlier repeat) implies 811 IHAVE/s; P50 921 µs implies
  1086 IHAVE/s. Those sit next to the measure-window 836/s figure. The published
  777/s is lower because warmup is in the denominator.
- The last timing run’s mean 1533 µs (652 IHAVE/s) is slower than the duration
  table; its P99 tail is heavier. That is reported, not discarded.

Limitations:

- Client-side only. HistoryDB Peek vs `335` write vs RTT are not split.
- Redis `EXISTS` distribution was not recorded inside `HistoryDb`.
- No HistoryDB-bypass control: the live host has no bench-only seam, and
  `history is null` is a production unconfigured path, not a diagnostic switch.
- Sampling 4000 timed transactions adds `Stopwatch` reads on that mode only.
- After this diagnostic work, a duration IHAVE rerun (same flags, **no** `--timing`)
  measured 739.9 / 675.6 IHAVE/s. The default path still has no per-article timers.
  That after-result is a check, not a replacement of the published table.

Candidate follow-up (not implemented): measure `EnqueueAsync` wait vs reader time
vs worker destuff rate; optional off-by-default HistoryDB/Redis probe if a later
task needs a true EXISTS distribution.

## IHAVE RAW receive microbenchmark (forensic)

This section is a **forensic** session/application Pipe-reader measure of production
`IHaveArticleReader` (frame `CRLF . CRLF`, own stuffed wire; destuff is
downstream in `IhaveArticleInterpreter`). It is **not** the IHAVE command benchmark
and is **not** selected by `--benchmark IHAVE`. It is **not** a TCP socket throughput
measurement. One `PipeReader.ReadAsync` is not one socket read.

The report remains at `.artifacts/ihave-corpus-bench/REPORT.md`.
Input is the complete real corpus at `.artifacts/Articles` (10,610 destuffed
stored articles; the harness restuffs leading dots and appends the NNTP
terminator plus a leftover `DATE` command). File list:
`.artifacts/ihave-corpus-bench/used-files.txt`. Raw report:
`.artifacts/ihave-corpus-bench/REPORT.md`.

TAKETHIS production code was not modified. The same corpus, chunk sizes, Pipe
options, and leftover check were passed through the existing unmodified STREAM
reader (`NntpContinuousRxReader.ReadUnitAsync`) for an observational compare
only.

### Forensic IHAVE reader methodology

- Benchmark type: session/application (`IHaveArticleReader` + in-process Pipes).
- Not measured: TCP, TLS, DEFLATE, HistoryDB, Redis, the response writer, queue persist.
- Corpus: complete `.artifacts/Articles` (7,806,078,914 bytes). One pass per mode.
- Mode A (prebuffered): entire framed article is written and flushed before the reader starts. Pipe `pauseWriterThreshold` = 8 MiB.
- Mode B (streaming): a concurrent producer writes 4 / 16 / 64 / 256 KiB chunks. Pipe `pauseWriterThreshold` = `2 × chunk` so the producer cannot prebuffer the whole article.
- ReadAsync / AdvanceTo counts come from a bench-only `PipeReader` wrapper. Production hot-path instrumentation was not added.
- Safety: every article left `DATE\r\n` intact (0 leftover failures).

### Forensic IHAVE reader corpus

| Metric | Value |
|---|---:|
| articles | 10610 |
| total bytes | 7806078914 |
| average | 735728.5 |
| median / p50 | 740474 |
| p95 | 793113 |
| p99 | 1082806 |
| min | 647 |
| max | 2164763 |

95.13% of articles are 500 KiB–1 MiB. There are no articles above 5 MiB. The
previous ~750 KiB estimate is close to this measured average (718.5 KiB) but
must not replace the measured number.

Classification (production `ArticleTypeClassifier`; flags overlap): yEnc
10,574 (99.66%); MIME 33 (0.31%); BASE64 0; UUENCODE 0; binary 10,574;
text (`Default`/`None`) 3; multipart 2. No article has `Content-Length`.
10,574 articles have a `Bytes:` header; the production reader does not
consult it. 7,943 yEnc articles expose `size=`; the mean declared yEnc size
is 625,533,225 bytes (decoded/original size, not wire bytes).

### Forensic IHAVE reader results

Single Release run on the test host (`Windows 10.0.26200`, Intel Core
i9-12900KF) after the production raw-wire receive change. Complete corpus,
one iteration per row. Destuff/classification is not on this receive path.

| Mode | Chunk | ReadAsync/art | AdvanceTo/art | bulk ops/art | bulk bytes/art | B/read | MB/s | art/s |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| A prebuffered | (all first) | 1.00 | 1.00 | 1.00 | 735729 | 735732 | 2589.8 | 3691.1 |
| B streaming | 4 KiB | 90.90 | 90.90 | 1.00 | 735729 | 8093 | 1277.5 | 1820.7 |
| B streaming | 16 KiB | 22.93 | 22.93 | 1.00 | 735729 | 32080 | 1709.1 | 2435.8 |
| B streaming | 64 KiB | 6.10 | 6.10 | 1.00 | 735729 | 120649 | 2029.4 | 2892.3 |
| B streaming | 256 KiB | 1.99 | 1.99 | 1.00 | 735729 | 369345 | 2388.5 | 3404.2 |

Leftover failures: 0. Prebuffered receive is 2.48× the previous destuff-on-ingress
result (1042.5 MB/s) on this same harness.

Observational TAKETHIS STREAM (unmodified; framed wire copy, no destuff), same
corpus and arrival:

| Mode | Chunk | ReadAsync/art | MB/s | art/s |
|---|---|---:|---:|---:|
| A prebuffered | (all first) | 1.00 | 2128.7 | 3033.8 |
| B streaming | 4 KiB | 90.87 | 1173.4 | 1672.4 |
| B streaming | 16 KiB | 22.93 | 1428.5 | 2035.9 |
| B streaming | 64 KiB | 6.08 | 1778.5 | 2534.7 |
| B streaming | 256 KiB | 1.99 | 1762.0 | 2511.2 |

### Forensic IHAVE reader interpretation

- Prebuffered IHAVE uses one `ReadAsync` because the whole article is already
  in the Pipe when the reader starts. That is not evidence of streaming or
  socket performance.
- Streaming IHAVE `ReadAsync` counts track the arrival quantum (about
  `2 × chunk` bytes per read because `pauseWriterThreshold` is `2 × chunk`),
  not a constant “one read per article”.
- Production IHAVE receive copies stuffed wire and stops at `CRLF . CRLF`.
  Destuff and `Article` construction run in `IhaveArticleInterpreter` after
  queue admission, not in this receive benchmark.
- The terminator remained authoritative; leftover `DATE` was never consumed.
- TAKETHIS STREAM used the same arrival model. On this after-run, prebuffered
  IHAVE (2589.8 MB/s) exceeded observational STREAM (2128.7 MB/s). These are
  Pipe figures, not TCP ingest rates.
- Do not treat these Pipe MB/s figures as Internet or TCP ingest rates.

## Workload comparison

BENCHIT, TAKETHIS, CHECK, and IHAVE are different workloads. None replaces the others.

| Aspect | BENCHIT | TAKETHIS | IHAVE command |
| --- | --- | --- | --- |
| What it measures | Production transport/TX path with a static multiline response | Real STREAM/TAKETHIS receive, framing, response, and ingestion processing, plus the relevant transport path | Real serialized IHAVE command: HistoryDB peek, 335, raw article receive, queue, 235 |
| Direction of bulk data | Server → client | Client → server | Client → server |
| Command | Internal unadvertised `BENCHIT` | RFC 4644 `MODE STREAM` + `TAKETHIS` | RFC 3977 `IHAVE` |
| Pipelining | Non-pipelined request/response | Bounded pipeline (256 / connection) | Not pipelined (one in flight / connection) |
| Article accounting | `768000`-byte response payload | `768054`-byte framed article | cycling `.artifacts/Articles` wire (mean 728,764 bytes in the prepared prefix) |
| Reported work unit | completed requests / s | articles sent / s | `235` accepted / s |
| Modes in this document | plain, DEFLATE, TLS, TLS+DEFLATE | plain TCP only | plain TCP only |
| HistoryDB | not used | not the admission path | production HistoryDB + Redis |
| Latency | p50 / p95 / p99 recorded | not recorded by this client | not recorded by this client |

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

CHECK does not use the TCP host/port flags. It runs in-process:

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- --benchmark CHECK
```

IHAVE CLI defaults differ from this document (warmup 0 s, one run, 30 s measure) unless the
flags below are supplied. Use these flags to reproduce the command-benchmark table.

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark IHAVE `
  --host 198.18.0.66 --port 1199 `
  --connections 1 --warmup-seconds 5 --measure-seconds 60 --runs 2 `
  --server-pid <pid>
```

`--benchmark IHAVE` is the real serialized command path. It is not the forensic Pipe-reader
microbenchmark.

Diagnostic timing (does not replace the duration table):

```powershell
dotnet run -c Release --project tools\VectorNNTP.NNTPD.Bench -- `
  --benchmark IHAVE --timing --samples 4000 `
  --host 198.18.0.66 --port 1199 `
  --connections 1 --warmup-seconds 5 --server-pid <pid>
```

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
- CHECK is a session/application measure (`NntpSession` + Pipes + fake Redis). It is not TCP CHECK
  throughput and is not comparable to BENCHIT req/s or TAKETHIS/sec.
- CHECK Redis latency is `Task.Delay` in a bench-only fake. On this Windows host the 1 / 2 / 5 ms
  delayed rows completed in essentially the same elapsed time.
- CHECK allocation B/op is not reported.
- IHAVE command/sec is not interchangeable with BENCHIT req/s or TAKETHIS/sec.
- The IHAVE command benchmark cycles a prepared prefix of `.artifacts/Articles` (368 of
  10,610 files, 256 MiB budget) because holding the full 7.8 GiB corpus in the client is
  not practical. Catalog order is preserved. Command Message-IDs are unique so HistoryDB
  does not reject later iterations.
- The forensic IHAVE Pipe-reader MB/s figures are not TCP ingest rates and are not the
  `--benchmark IHAVE` command result.

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
- CHECK session/application benchmark (`--benchmark CHECK`, Release) completed on 2026-09-24;
  every workload reported `ordered=True`; peak in-flight 16 on delayed Redis rows; local-hit
  EXISTS 0; cooldown EXISTS 1
- IHAVE real serialized command benchmark (`--benchmark IHAVE`, Release) completed on
  2026-09-24 against production `VectorNNTP.NNTPD` on `198.18.0.66:1199` with production
  HistoryDB/Redis (`198.18.0.70:6379`); 1 connection; 5 s warmup; 60 s measure; 2 runs;
  100% `235`; zero `435` / `436` / `437` / protocol / connection errors; raw output at
  `.artifacts/ihave-command-bench/results.txt`
- Forensic IHAVE Pipe-reader measure remains at `.artifacts/ihave-corpus-bench/REPORT.md`
  and is not selected by `--benchmark IHAVE`
