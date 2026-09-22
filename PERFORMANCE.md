# VectorNNTP.NNTPD Performance

## Purpose

This document records the current baseline performance of the VectorNNTP.NNTPD socket/transport
stack using a real TCP benchmark against the production server implementation.

The purpose of this benchmark was to answer:

> Is the production socket/pipe/transport architecture fundamentally limiting NNTP throughput on the target hardware?

The benchmark was deliberately performed before implementing the remaining heavy NNTP data-plane and
storage functionality so that transport performance could be isolated.

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
- Server CPU remains below approximately 40–43% in the measured plain scenarios.

### DEFLATE

- Approximately 90 Gbit/s logical throughput at 10 and 50 connections.
- CPU rises into the ~49–51% range.
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
- CPU approximately 48–53%.
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
- CPU does not saturate during the benchmark.
- The transport layer is therefore sufficiently validated to proceed with implementation of the remaining NNTP server functionality.

> **No transport optimization work is planned from this benchmark alone.**

Future optimization should be driven by realistic workloads and measured evidence.

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
- Logical Gbit/s should not be interpreted as network wire throughput when compression is active.
- Results are specific to the test host, network path, runtime, OS, and benchmark implementation.

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
- streaming commands
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
- Full test suite: 575/576 initially, with the known flaky `Compress_Deflate_SessionRoundTrip_Repeated_AuthinfoRejected` passing on rerun
- `git diff --check`: clean
- Complete live BENCHIT benchmark suite completed
- Benchmark results retained at: `tools/VectorNNTP.NNTPD.Bench/results-full.txt`
