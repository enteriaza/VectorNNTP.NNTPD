# Real TCP: MODE READER vs MODE STREAM experiment

Benchmark / experiment only. **No production `src/` behavior was modified for this work.**

Raw JSON: `tools/VectorNNTP.NNTPD.CommandResponsePathBench/results/real-tcp-reader-vs-stream.json`  
Console log: `tools/VectorNNTP.NNTPD.CommandResponsePathBench/results/real-tcp-reader-vs-stream-console.log`

---

## 1. Objective

Determine whether MODE READER and MODE STREAM **materially benefit from different RX/TX scheduling / data-plane behavior** when transmitting **real NNTP articles** over **actual localhost TCP** (and TLS), using the established `C:\Temp\Incoming` corpus.

This experiment supplies **TCP evidence** before deciding whether a separate STREAM data plane is justified.

## 2. Hypothesis

**H1 (scheduling):** STREAM with bounded multi-article outstanding depth can achieve higher application throughput than sequential READER-style wait-for-receive under FAST receivers, by keeping the TCP send window fed across article boundaries.

**H2 (data plane):** Materialized vs streamed article encoding remains the dominant TX throughput/allocation tradeoff; READER vs STREAM should **not** require duplicated article restuff/TX implementations.

**H3 (backpressure):** Real TCP backpressure (SLOW receiver) collapses end-to-end throughput to the receive rate for both traffic models; Pipe `FlushAsync` must not be confused with socket delivery.

## 3. Environment

| Item | Value |
|------|-------|
| Machine | `CHRIS-PC` |
| OS | Windows 10.0.26200 (`win-x64`) |
| .NET | 10.0.12 / SDK 10.0.401 |
| UTC run | 2026-09-22T20:12:06Z (approx. end) |
| Transport | Loopback TCP; TLS 1.2/1.3 via `SslStream` + self-signed localhost cert |
| DEFLATE | **Deferred** (not implemented this iteration) |

## 4. Corpus

Same semantics as `docs/ARTICLE-TX-DATA-PLANE-EXPERIMENT.md`:

| Metric | Value |
|--------|-------|
| Root | `C:\Temp\Incoming` |
| Articles | 10,610 |
| Stored | ≈ 7.27 GiB |
| P50 stored | ≈ 740 KiB |
| Dominant bucket | 700–768 KiB |
| Stored form | Dot-unstuffed; TX uses production restuff (`ArticleWireReconstructor`) |

Workloads:

| Set | Articles | Stored bytes |
|-----|----------|--------------|
| Matrix | 145 | ≈ 100 MiB |
| Headline | 717 | ≈ 500 MiB |
| Multi-conn | 4 slices of matrix (round-robin) | ≈ 25 MiB each |

## 5. Experimental methodology

Harness: `tools/VectorNNTP.NNTPD.CommandResponsePathBench` flag `--real-tcp-reader-stream`.

Architecture (benchmark-only):

```
Article source (mat / stream restuff)
  → ExperimentalArticleTxWriter (owned chunks → Channel → PipeWriter.FlushAsync)
  → TcpSendPump (Pipe → Stream.WriteAsync on NetworkStream / SslStream)
  → localhost TCP
  → BenchReceiver (FAST drain / SLOW paced drain)
```

- Warmup: 5 articles, then measured runs.
- Headline: 500 MiB × 3 iterations (materialized, 64 KiB chunks, FAST).
- Matrix: 100 MiB × 1 iteration (mat/stream × 64/256 KiB × FAST; SLOW for mat 64 KiB only).
- Multi-connection: 4 concurrent connections, Plain TCP, mat 64 KiB, FAST.
- Handshake time recorded separately; **excluded** from steady-state stopwatch (connect/TLS completes before measurement window).
- Throughput uses **application** bytes (protocol payload after TLS). Wire/ciphertext bytes not separately instrumented (plaintext TCP app≈wire; TLS wire > app).

## 6. Traffic models

| Model | Framing | Scheduling |
|-------|---------|------------|
| **READER** | `220 0 <mid>\r\n` + restuffed article + `.\r\n` | Depth forced to 1: fully wait until receiver has consumed each article before starting the next |
| **STREAM** | `TAKETHIS <mid>\r\n` + restuffed article + `.\r\n` | Bounded outstanding depths **1 / 4 / 16 / 64**; producer may advance after socket-path flush while prior articles are still being drained by the receiver |

STREAM depth **1** is intentionally sequential (same outstanding policy as READER); framing differs only. Depths **≥4** allow multiple articles in flight at the receive-ack boundary.

## 7. Transport matrix

| Transport | Status |
|-----------|--------|
| Plain TCP | Measured |
| TLS over TCP (`SslStream`) | Measured |
| Plain TCP + DEFLATE | **Deferred** |
| TLS + DEFLATE | **Deferred** |

## 8. TX implementation matrix

| Source | Path |
|--------|------|
| Materialized | `File.ReadAllBytes` → restuff → bounded TX chunks |
| Streamed | `FileStream` → line extract/restuff → bounded TX chunks |

Chunks: **64 KiB** and **256 KiB**. Same `ExperimentalArticleTxWriter` for both traffic models.

## 9. Receiver / backpressure model

| Pace | Behavior |
|------|----------|
| **FAST** | Read up to 64 KiB as fast as possible |
| **SLOW** | Read up to **32 KiB**, then **Delay(2 ms)** ≈ **16 MiB/s** ceiling if reads are full |

SLOW constrains the **receiver**, inducing real TCP window backpressure (not a sender-side sleep).

Instrumentation distinction:

| Counter | Meaning |
|---------|---------|
| `PipeFlushAsyncCalls` | App-level `PipeWriter.FlushAsync` (Channel pump → send Pipe) |
| `SocketWriteCalls` / `SocketWriteMs` | `Stream.WriteAsync` on TCP/TLS stream |
| Receiver byte waits | Scheduling gate for READER / STREAM depth |

**Pipe FlushAsync ≠ socket delivery.**

## 10. Correctness results

**ALL PASSED** before performance matrix:

| Check | Result |
|-------|--------|
| TCP Reader materialized vs oracle (byte-identical capture) | OK (774,621 B) |
| TCP Reader streamed vs oracle | OK |
| TLS Reader vs oracle | OK (handshake ≈ 47 ms first / ≈ 3–5 ms later) |
| TCP Stream materialized / streamed | OK (774,624 B; framing differs by `TAKETHIS` vs `220 0`) |
| TLS Stream | OK |
| Expected total application bytes per run | Matched receiver |
| Clean connection end (EOF after send complete) | OK for all measured runs (`Ok=true`) |

No throughput row from a failed correctness run was accepted.

## 11. Raw benchmark results

See JSON + console log. Headline excerpt (iter 1–3, mat, 64 KiB, FAST, 717 articles):

### Plain TCP (MiB/s application)

| Traffic | Depth | Iter1 | Iter2 | Iter3 |
|---------|-------|-------|-------|-------|
| Reader | 1 | 432.9 | 568.0 | 567.7 |
| Stream | 1 | 509.4 | 560.6 | 570.9 |
| Stream | 4 | 578.4 | 550.3 | 567.2 |
| Stream | 16 | 577.3 | 545.2 | 577.1 |
| Stream | 64 | 556.6 | 564.8 | 567.0 |

**Variance note:** Reader iter1 (432.9) is a clear cold/outlier relative to iter2–3 (~568). Do not hide this.

### TLS (MiB/s application)

| Traffic | Depth | Iter1 | Iter2 | Iter3 |
|---------|-------|-------|-------|-------|
| Reader | 1 | 466.2 | 451.0 | 462.4 |
| Stream | 1 | 466.4 | 468.9 | 466.8 |
| Stream | 4 | 472.8 | 482.2 | 479.6 |
| Stream | 16 | 475.2 | 478.0 | 473.3 |
| Stream | 64 | 473.9 | 443.2 | 471.6 |

TLS handshake excluded from these rates (recorded separately, typically 3–5 ms steady-state).

## 12. Summary tables

### Headline-only medians (717 articles / ~500 MiB, n=3)

| Traffic | Depth | Transport | Median MiB/s | Min | Max | Median art/s | Median alloc MiB | Median sock write ms |
|---------|-------|-----------|--------------|-----|-----|--------------|------------------|----------------------|
| Reader | 1 | PlainTcp | **567.7** | 432.9 | 568.0 | 813.7 | 2010 | 166 |
| Stream | 1 | PlainTcp | 560.6 | 509.4 | 570.9 | 803.5 | 2010 | 167 |
| Stream | 4 | PlainTcp | 567.2 | 550.3 | 578.4 | 812.8 | 2010 | 167 |
| Stream | 16 | PlainTcp | **577.1** | 545.2 | 577.3 | 827.1 | 2010 | 168 |
| Stream | 64 | PlainTcp | 564.8 | 556.6 | 567.0 | 809.4 | 2010 | 172 |
| Reader | 1 | Tls | 462.4 | 451.0 | 466.2 | 662.7 | 2010 | 369 |
| Stream | 1 | Tls | 466.8 | 466.4 | 468.9 | 669.1 | 2010 | 367 |
| Stream | 4 | Tls | **479.6** | 472.8 | 482.2 | 687.4 | 2010 | 369 |
| Stream | 16 | Tls | 475.2 | 473.3 | 478.0 | 681.1 | 2010 | 367 |
| Stream | 64 | Tls | 471.6 | 443.2 | 473.9 | 675.9 | 2010 | 371 |

PipeFlushAsyncCalls = SocketWriteCalls = **8519** for all headline mat/64 KiB runs (same chunking).

### Matrix highlights (100 MiB, 1 iter)

| Config | App MiB/s | Alloc MiB | Notes |
|--------|-----------|-----------|-------|
| Reader mat 64k FAST TCP | 566 | 402 | |
| Stream d=16 mat 64k FAST TCP | 592 | 402 | |
| Reader streamed 64k FAST TCP | 310 | 229 | ~45% less alloc, ~45% less throughput |
| Stream d=4 streamed 64k FAST TCP | 331 | 229 | |
| Reader/Stream SLOW TCP | ≈ 2.0 | ≈ 402 | Receiver-limited |
| Reader/Stream SLOW TLS | ≈ 1.0 | ≈ 403 | TLS + slow drain |

### Multi-connection (4 × ~25 MiB, Plain TCP, mat 64 KiB, FAST)

| Traffic | Depth | Aggregate app MiB/s | Aggregate art/s |
|---------|-------|---------------------|-----------------|
| Reader | 1 | 1501 | 2175 |
| Stream | 16 | 1576 | 2284 |

(~5% STREAM edge on aggregate wall-clock throughput.)

## 13. Allocation / GC observations

**Measured fact:** For the same corpus/chunk/transport, READER vs STREAM allocations are essentially identical (~2010 MiB alloc for 500 MiB headline mat; ~402 MiB for 100 MiB mat).

**Measured fact:** Streamed source cuts alloc ~**43%** vs materialized (229 vs 402 MiB on 100 MiB matrix) and Gen0/1/2 collections drop sharply (e.g. Gen2 2 vs ~34), at ~45% lower FAST throughput.

**Interpretation:** Allocation pressure is dominated by **article source strategy**, not traffic scheduling model.

**Limitation:** Concurrent multi-connection `GC.GetTotalAllocatedBytes` is process-wide and can over-count when connections run in parallel (multi-conn alloc figure inflated).

## 14. Backpressure observations

Under SLOW receiver (~16 MiB/s design ceiling; observed ~2 MiB/s TCP / ~1 MiB/s TLS on this run — delay + TLS/read overhead):

| Observation | Kind |
|-------------|------|
| End-to-end app MiB/s ≈ identical for Reader and all Stream depths | Measured |
| Stream depth ≥4: `SocketWriteMs` ≈ 2× Reader (producer blocked on full TCP window while trying to keep pipeline full) | Measured |
| Pipeline depth does **not** improve article-completion throughput when the receiver is the bottleneck | Measured |
| Peak Channel bytes stayed at one chunk (65536) — single-writer awaits each Channel→Pipe flush | Measured |

## 15. TCP vs Pipe observations

| Signal | What it showed |
|--------|----------------|
| PipeFlushAsync count == SocketWrite count (this harness) | Each owned chunk flushed once through Pipe then written once to stream |
| PeakPipeUnflushed reported 0 | Instrumentation gap: peak unflushed sampling did not capture meaningful Pipe fill (do not treat as “Pipe empty”) |
| SocketWriteMs under FAST | Small fraction of wall time (~165 ms of ~1.3 s headline) — most time in disk/restuff/CPU |
| SocketWriteMs under SLOW | Dominates wall time for deep STREAM — real TCP backpressure |

**Do not claim** Pipe FlushAsync equals peer delivery. Receiver byte counts are the completion oracle.

## 16. READER vs STREAM comparison

| Comparison | Result |
|------------|--------|
| Stream d=1 vs Reader (FAST) | Within noise / framing-only (same sequential wait policy) |
| Stream d=4–16 vs Reader (FAST Plain TCP) | Median ~0–2% higher than Reader median; within Reader min–max span |
| Stream d=4 vs Reader (FAST TLS) | Median **+3.7%** (479.6 vs 462.4) — small but consistent across iters |
| Stream d=64 | No further gain; sometimes worse (TLS iter variance) |
| SLOW | No throughput advantage to STREAM |
| 4 connections | ~5% aggregate STREAM advantage |

**Interpretation:** Scheduling/pipelining helps modestly when the receiver can keep up and TLS/crypto or RTT-like effects leave headroom; on localhost FAST Plain TCP the gain is often lost in disk/CPU/cache variance.

## 17. Interpretation

1. **Shared article TX path is adequate.** Materialized vs streamed dominates performance; READER/STREAM share the same Channel→Pipe→TCP pump without conflict in these results.
2. **Different scheduling policies are plausible.** Bounded STREAM outstanding depth (about **4–16**) is the only scheduling knob that showed a small, repeatable TLS and multi-conn benefit.
3. **Not evidence for a separate STREAM encoding/data-plane stack.** Framing differs (`220` vs `TAKETHIS`); restuff/chunking/Channel/Pipe should stay shared.
4. **In-memory Pipe conclusions survive only partially:** chunked TX still matters; “Pipe backpressure == delivery” does not — real TCP SLOW runs separate those clocks.

## 18. Limitations

- Localhost only (no WAN RTT, loss, or cross-host NIC).
- DEFLATE not measured.
- 8-connection matrix not run (4-conn only).
- Headline variance high on first Reader Plain TCP iteration (cache/thermal).
- Peak Pipe unflushed instrumentation ineffective (0).
- Concurrent GC allocation accounting imperfect.
- Harness uses benchmark `ExperimentalArticleTxWriter` + `TcpSendPump`, not production `NntpResponseWriter` / session stack (production was intentionally not modified).
- Wire/ciphertext byte counters not collected under TLS.
- JSON “medians” group mixes 100 MiB matrix + 500 MiB headline when keys collide; **headline-only table in §12 is authoritative** for READER vs STREAM.

## 19. Architectural implications

Evidence favors:

```
Shared transport (TCP/TLS/DEFLATE)
        |
Shared article TX / data path (restuff + chunked Channel→Pipe)
        |
   +----+----+
   |         |
READER     STREAM
scheduling scheduling
(depth=1)  (bounded depth ~4–16)
```

**Not** favored by these measurements: duplicate materialized/streamed implementations per mode, or a wholly separate STREAM TX pipeline.

## 20. Explicit answers (10 questions)

1. **Does MODE STREAM achieve materially different TCP throughput than MODE READER on the same articles?**  
   **Mostly no on FAST Plain TCP medians** (within ~2% and inside Reader variance). **Small yes under TLS** (~+4% at depth 4) and **~+5% aggregate** with 4 connections.

2. **Does STREAM benefit from multiple articles in flight?**  
   **Yes, modestly** — depth 4–16 beats depth 1 under TLS and often under matrix FAST TCP; depth 1 ≈ Reader.

3. **At what pipeline depth does throughput stop improving?**  
   **Around 4–16.** Depth **64** did not improve further and sometimes regressed (TLS).

4. **Does the advantage survive TLS?**  
   **Yes, the small pipelining advantage is clearest under TLS** in this localhost run; absolute TLS throughput is lower than Plain TCP (~460–480 vs ~560–580 MiB/s).

5. **Does materialized TX remain faster than streamed over real TCP?**  
   **Yes.** ~560–600 vs ~310–330 MiB/s FAST Plain TCP (matrix).

6. **Does streaming materially reduce allocation pressure over real TCP?**  
   **Yes.** ~43% less allocated bytes and far fewer Gen2 collections for the same workload.

7. **Does real TCP backpressure change in-memory conclusions?**  
   **Partially.** Chunking/Channel behavior still holds. Under SLOW TCP, **end-to-end rate is receiver-bound for both modes**; STREAM deep pipelines burn more time blocked in `WriteAsync` without finishing articles faster. Pipe flush timing ≠ delivery.

8. **Do multiple concurrent connections change relative behavior?**  
   **Slightly.** Both scale up aggregate throughput; STREAM d=16 retained a small edge (~5%). No inversion of mat vs stream tradeoffs observed.

9. **Evidence that READER and STREAM should have different scheduling/control planes?**  
   **Weak-to-moderate yes:** use depth=1 (request/response) for READER; allow bounded outstanding articles for STREAM. Not a large absolute gain on localhost FAST Plain TCP.

10. **Evidence they require different article encoding/data-plane implementations?**  
   **No.** Same mat/stream relative costs under both traffic models; allocations and chunk/flush counts track source+chunk size, not READER vs STREAM.

---

### Verdict on architecture choice

| Option | Supported? |
|--------|------------|
| Shared article TX/data plane + different scheduling | **Best supported** |
| Separate READER/STREAM scheduling only (shared everything else) | **Yes** (same as above) |
| Deeper separation (duplicate TX stacks per mode) | **Not supported** by this evidence |

---

## Commands used

```text
dotnet build tools/VectorNNTP.NNTPD.CommandResponsePathBench/VectorNNTP.NNTPD.CommandResponsePathBench.csproj -c Release
dotnet run --project tools/VectorNNTP.NNTPD.CommandResponsePathBench/VectorNNTP.NNTPD.CommandResponsePathBench.csproj -c Release --no-build -- --real-tcp-reader-stream
dotnet build VectorNNTP.NNTPD.sln -c Release
dotnet test VectorNNTP.NNTPD.sln -c Release --no-build
dotnet format tools/VectorNNTP.NNTPD.CommandResponsePathBench/VectorNNTP.NNTPD.CommandResponsePathBench.csproj --verify-no-changes
```

## Validation notes

- Benchmark exit code: **0**; correctness **ALL PASSED**.
- Solution build: **succeeded**.
- `dotnet format` on bench project: **verify-no-changes OK**.
- `dotnet test`: **796 passed, 3 failed** — failures are in pre-existing WIP (`TakeThisCommandTests` spool writer, `NntpCompressTests`), correlated with already-dirty `src/` / test changes **not introduced by this experiment**. This experiment did not modify `src/`.
- Production `src/` and production configuration were **not** edited for this task. Pre-existing dirty `src/` files in the working tree were left untouched.
- **Not committed.**
