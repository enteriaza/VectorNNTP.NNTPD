# Chunked TX experiment

**Status:** isolated performance experiment (benchmark-only)  
**Date:** 2026-09-22  
**Baseline audit:** `docs/COMMAND-RESPONSE-PATH-AUDIT.md`  
**Harness:** `tools/VectorNNTP.NNTPD.CommandResponsePathBench/` (`--chunked-tx`, `--chunked-tx-correctness`)  
**Results JSON:** `tools/VectorNNTP.NNTPD.CommandResponsePathBench/results/chunked-tx-experiment.json`

Evidence labels: **PROVEN** / **OBSERVED** / **INFERRED** / **UNKNOWN**.

---

## 1. Objective

Measure what happens when multiline overview-like TX changes from:

```text
1 record → 1 Channel item → 1 PipeWriter FlushAsync
```

to:

```text
N records (or ≤B bytes) → 1 owned byte[] chunk → 1 Channel item → FlushAsync
```

without changing production `NntpResponseWriter` or protocol behaviour.

---

## 2. Current baseline

From the prior audit and this experiment’s CURRENT path (production `NntpResponseWriter.WriteMultiline*`):

| Metric (100 000 records, FAST drain) | Audit (prior) | This run (**OBSERVED**) |
|---|---|---|
| Channel items | ~100 002 | 100 002 (derived: records+2) |
| FlushAsync | ~100 002 | 100 002 |
| Allocated | ~65.6 MiB (~656 B/rec) | ~107.8 MiB (~1078 B/rec) |
| Elapsed | ~242 ms | ~157 ms |
| Records/s | ~4.1e5 | ~6.4e5 |

**Note on allocation delta:** prior audit used a different overview string shape / measurement window; **PROVEN** structural cost (1 item + 1 FlushAsync per line) matches. Absolute alloc/B/rec differ by corpus and GC noise — comparisons **within this experiment** are the authoritative relative results.

---

## 3. Experimental design

| Path | Implementation |
|---|---|
| **CURRENT** | Production `NntpResponseWriter` multiline APIs |
| **CHUNKED** | Bench-only `ExperimentalChunkedMultilineWriter` (Channel capacity 4096, Wait, single reader pump, await-flush per Channel item) |

Shared:

- Same `OverviewRecordCorpus` strings  
- Same status line `224 Overview information follows`  
- Same framing/dot-stuffing rules as production (`"."` prefix → stuff; ASCII; CRLF; final `.\r\n`)  
- Same Pipe options (pause 64 KiB / resume 32 KiB / minSeg 4 KiB)  
- `InstrumentedOutputPipe` drain + FlushAsync counting + full wire capture  

**Unavoidable difference (**PROVEN**):** CURRENT Channel depth / wait counters are not exposed by production writer (reported as −1). Channel item count for CURRENT is **derived** as `records + 2`. CHUNKED uses an independent but parallel Channel+pump (extra ~400 B/rec overhead at chunk=1 vs CURRENT — experimental tax, not a production claim).

---

## 4. Correctness model

Before performance:

```text
CURRENT wire == CHUNKED wire   (byte-for-byte)
```

for normal, dot-leading, empty-field, multi, 1 000, 10 000 records, and multiple chunk configs.

**OBSERVED:** `CORRECTNESS: ALL PASSED`.

Chunked path never touches a `PipeReader`; only owned `byte[]` cross the Channel (**PROVEN** by construction).

---

## 5. Chunk strategies

| Mode | Parameter |
|---|---|
| Record count | 1, 8, 32, 64, 256, 1024, 4096 records/chunk |
| Byte budget | 16, 32, 64, 128 KiB target (single oversized record may exceed) |

Neither is asserted as the future production policy.

---

## 6. Benchmark methodology

- Release, .NET 10, machine `CHRIS-PC`  
- Workloads: 1 000 / 10 000 / 100 000 deterministic records  
- Drain: FAST / MEDIUM / SLOW (delay scaled by drained bytes)  
- Metrics: elapsed, r/s, output bytes, Channel items, FlushAsync, Pipe Advance calls, alloc (`GC.GetTotalAllocatedBytes`), Gen0/1/2 deltas, peak Channel queued bytes (CHUNKED), peak Pipe unflushed  

Command:

```text
dotnet run -c Release --project tools/VectorNNTP.NNTPD.CommandResponsePathBench -- --chunked-tx
```

---

## 7. Results (FAST drain, highlights)

### 100 000 records (**OBSERVED**)

| Config | Channel items | FlushAsync | Alloc (B/rec) | Records/s | Peak queued bytes |
|---|---|---|---|---|---|
| CURRENT | 100 002 | 100 002 | 1078 | 6.37e5 | n/a |
| records/chunk=1 | 100 002 | 100 002 | 1469 | 6.24e5 | 265 |
| records/chunk=8 | 12 502 | 12 502 | 868 | 3.44e6 | 953 |
| records/chunk=32 | 3 127 | 3 127 | 781 | 6.00e6 | 3 452 |
| records/chunk=64 | 1 565 | 1 565 | 772 | 7.51e6 | 6 878 |
| records/chunk=256 | 393 | 393 | **766** | 8.38e6 | 27 433 |
| records/chunk=1024 | 100 | 198 | 834 | 7.92e6 | 109 176 |
| records/chunk=4096 | 27 | 173 | 841 | 8.60e6 | **436 201** |
| bytes/chunk=16 KiB | 655 | 655 | 836 | 8.53e6 | 16 370 |
| bytes/chunk=32 KiB | 328 | 328 | 834 | 8.33e6 | 32 767 |
| bytes/chunk=64 KiB | 165 | 165 | 833 | **9.66e6** | 65 536 |
| bytes/chunk=128 KiB | 84 | 165 | 833 | 9.18e6 | 131 071 |

FlushAsync can exceed Channel items when a single chunk is larger than the pump’s 64 KiB copy loop (**PROVEN** in pump: FlushAsync per ≤64 KiB slice).

### Answers to the experiment questions (measurement-backed)

1. **Allocation:** vs CURRENT, batched modes cut ~200–300 B/rec at mid sizes; largest gains are vs the experimental chunk=1 tax. Absolute floor ~770 B/rec still includes formatting + Channel plumbing (**OBSERVED**).  
2. **FlushAsync:** falls from 100 002 → 393 (256-rec) → 165 (64 KiB) (**OBSERVED**).  
3. **Channel items:** same order-of-magnitude drop as FlushAsync (until large chunks split across 64 KiB flush loops).  
4. **Throughput:** rises ~10–15× on FAST from CURRENT to mid/large batches (**OBSERVED**).  
5. **At 100 k:** improvement continues; plateau ~8–10 M r/s (**OBSERVED**).  
6. **Slow drain:** see §8.  
7. **Byte vs record batching:** similar peak throughput; byte budgets bound peak queued bytes more predictably (**OBSERVED**).  
8. **Diminishing returns:** after ~64–256 records/chunk or ~16–64 KiB, FlushAsync keeps falling but r/s gains shrink (**OBSERVED**).  
9. **Peak memory:** peak Channel payload grows with chunk size (up to ~427 KiB at 4096-rec); Pipe unflushed capped ~64 KiB by pause threshold (**OBSERVED**).  
10. **Ordering/framing:** unchanged — correctness passed (**OBSERVED** / **PROVEN**).

---

## 8. Backpressure results (10 000 records)

| Drain | CURRENT r/s | chunk=64 r/s | chunk=4096 r/s | bytes=64 KiB r/s |
|---|---|---|---|---|
| Fast | 7.2e5 | 9.6e6 | 7.0e6 | 1.1e7 |
| Medium | 7.3e5 | 1.1e5 | 4.1e4 | 4.0e4 |
| Slow | 2.1e4 | 2.1e4 | 3.8e4 | 4.1e4 |

**OBSERVED:**

- Under **Slow**, CURRENT and chunk=1 converge (~21 k r/s) — drain delay dominates.  
- Larger chunks can finish *sooner* on Slow because fewer await-flush round-trips (each flush waits on larger drained slices but fewer times).  
- Under **Medium**, large chunks can be *slower* than CURRENT: delay ∝ bytes drained per flush amplifies large payloads.  
- Batching does **not** defeat Pipe pause (peak unflushed ~64 KiB) but **does** raise peak Channel-queued bytes with chunk size.

---

## 9. Allocation results

| Scale | CURRENT B/rec | Best CHUNKED B/rec (this run) |
|---|---|---|
| 1 000 | 991 | ~678 (64 KiB budget) |
| 10 000 | 940 | ~686 (64 KiB) |
| 100 000 | 1078 | ~766 (256-rec) / ~833 (byte budgets) |

**INFERRED:** residual alloc includes per-record `FormatBodyLine` `byte[]` before append, List growth, Channel/`TaskCompletionSource` per chunk, and GC accounting noise.

Gen0/1/2 deltas are recorded in JSON; not restated here as primary evidence (short runs).

---

## 10. Flush / Channel reduction (100 k)

| | CURRENT | 256-rec | 64 KiB |
|---|---|---|---|
| Channel items / 1000 rec | 1000.02 | 3.93 | 1.65 |
| FlushAsync / 1000 rec | 1000.02 | 3.93 | 1.65 |

≈ **255×** fewer Channel items at 256-rec vs CURRENT; ≈ **606×** at 64 KiB (**OBSERVED**).

---

## 11. Memory behaviour

- Pipe unflushed peak ≈ pause watermark (64 KiB) across large batches (**OBSERVED**).  
- Channel peak bytes ≈ one in-flight chunk (status/terminator small) — scales with chunk policy (**OBSERVED**).  
- No whole-response `MemoryStream` in the CHUNKED path (**PROVEN**).  
- Giant chunks (4096-rec ≈ 400 KiB+) increase peak queued memory without proportional r/s gain on FAST (**OBSERVED**).

---

## 12. Byte-for-byte correctness

**OBSERVED:** all listed correctness cases passed, including 1 k / 10 k and multiple chunk modes.

---

## 13. Interpretation

**PROVEN mechanisms that improve:** fewer Channel items and fewer producer-side flush awaits when records share a chunk.

**OBSERVED:** mid-size batches (tens–hundreds of records, or tens of KiB) capture most FAST-path gains; very large chunks mainly trade FlushAsync count for higher peak queue memory and more 64 KiB flush slices.

**INFERRED for production design (qualified):** a future streaming/chunked TX API *could* target bounded chunks in the “diminishing-return knee,” but this experiment does **not** select a production size, does not prove NIC/TLS behaviour, and does not authorize changing `NntpResponseWriter` yet.

**OBSERVED caution:** Medium drain shows large chunks can regress latency/throughput vs CURRENT — batching is not universally “faster” under all backpressure models.

---

## 14. Unknowns

- Behaviour under real TLS/socket RTT and auto-windowing  
- Interaction with future TX bandwidth policy admission  
- Whether production should await-flush per chunk vs enqueue-only for overview bodies  
- Optimal policy: record-count vs byte-budget vs hybrid  
- Cost of formatting from catalog bytes without intermediate `string`  
- Why experimental chunk=1 allocates more than production CURRENT (implementation tax)

---

## 15. Production design implications (not implementation)

Candidate seams (from `RX-TX-PIPELINE-DESIGN.md` / this experiment):

1. Chunked multiline body API with bounded owned buffers  
2. Keep Channel+ordering; reduce items/flushes  
3. Prefer byte-budget caps to bound peak queue memory  
4. Measure under Slow/Medium before picking sizes  
5. Do not skip Pipe FlushAsync semantics when comparing  

**Do not** treat any table row as the chosen production default.

---

## 16. Explicit NOT IMPLEMENTED

- No `src/` changes  
- No production `NntpResponseWriter` changes  
- No production streaming API / TX policy / ArrayPool / SIMD / unsafe  
- No commit  

Only: bench harness under `tools/.../CommandResponsePathBench/ChunkedTx/` and this document.
