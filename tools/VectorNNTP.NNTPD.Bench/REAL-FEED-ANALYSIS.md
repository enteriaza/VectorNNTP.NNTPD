# Real feed workload + continuous TAKETHIS data-plane investigation

**Status:** investigation / benchmark / prototype only.  
**Constraint:** no production code changes; no commit; no migration into `src/`.

Machine-readable companion: [`results/real-feed-analysis.json`](results/real-feed-analysis.json), [`results/continuous-stream-results.csv`](results/continuous-stream-results.csv), [`results/architecture-compare.txt`](results/architecture-compare.txt).

---

## 1. Executive summary

Real `news-*.log` capture (5 rotated daily files, ~4.8 days wall clock) shows:

| Population | Accepted articles | Total bytes | Share of bytes |
|---|---:|---:|---:|
| BlueWorldHosting | 45 081 | 241 MiB | **0.35%** |
| Giganews | 90 098 | **63.30 GiB** | **99.65%** |
| Combined | 135 179 | 63.53 GiB | 100% |

Giganews article sizes concentrate in **700–768 KiB** (84.7% of Giganews articles, **83.1% of Giganews bytes**). Measured median **740 484 B (~723 KiB)**. Articles ≥700 KiB carry **97.3%** of Giganews bytes. Articles ≥2 MiB carry only **1.7%** of Giganews bytes — the ~700 KiB operational observation is confirmed; **2 MiB is not** the representative binary size.

On a continuous MODE STREAM wire built from that median size, at production-like **4 KiB** Pipe segments (count-only sink, no article `byte[]`):

| Architecture | GiB/s | Gbit/s | Allocated (1.5 s run) |
|---|---:|---:|---:|
| A — Current materializing (`NntpMultilineDataReader`) | 0.87 | 7.5 | **~5.3 GiB** + Gen2 pressure |
| B — Continuous bulk framer | 5.28 | 45.3 | ~35 KiB |
| C — Continuous SIMD scanner | **8.80** | **75.6** | ~517 KiB |

**Interpretation (lab only):** continuous span-oriented framing is materially faster *and* nearly allocation-free versus materializing per article. SIMD helps the **real binary** workload when segments are ≥~1 KiB; it **hurts** at artificial 64 B segments. These numbers are parser microbenchmarks — not end-to-end production ingress (no TCP, TLS, disk, Channel, responses).

---

## 2. Log files analysed

**Directory (read-only):**  
`C:\Users\chrisk\source\repos\Vector.NNTP\Vector.NNTP.NNTPD\bin\x64\Debug\net8.0\win-x64\Logs`

### Included in feed statistics (`news-*.log` only)

| File | Size (at analysis) | Role |
|---|---:|---|
| `news-20260918.log` | 2 135 224 | Daily rotation |
| `news-20260919.log` | 8 509 606 | Daily rotation (Giganews heavy day) |
| `news-20260920.log` | 621 233 | Daily rotation |
| `news-20260921.log` | 685 905 | Daily rotation |
| `news-20260922.log` | 584 577 | Daily rotation (partial day at analysis time) |

Sorted lexicographically by filename (= chronological by date). Treated as one continuous capture for wall-clock span; overlaps not present (one file per UTC day). Gaps: days without Giganews traffic still contain BlueWorldHosting.

### Present but excluded from article-size stats

| File | Approx size | Notes |
|---|---:|---|
| `NNTPD-20260918.log` … `NNTPD-20260922.log` | up to ~89 MiB | Session/debug logs — not the accept/reject size ledger |

Rotation: daily `news-YYYYMMDD.log` / `NNTPD-YYYYMMDD.log`. No mid-day split files observed.

**Capture window (parsed timestamps):**  
`2026-09-18T00:00:04.216Z` → `2026-09-22T20:13:12.798Z` (~4.84 days).

Unparsed lines: **86** (all inspected samples are `c FeedName <id> Cancelling <id>` cancel records — not accept/reject size rows).

---

## 3. Log format / parser rules

Authoritative pattern (implemented in `NewsLogAnalyzer`):

```text
MMM d HH:mm:ss.fff + FeedName <message-id> sizeBytes ?
MMM d HH:mm:ss.fff - FeedName <message-id> reason...
```

| Field | Rule |
|---|---|
| Timestamp | Local-looking `MMM d HH:mm:ss.fff`; **year from filename** `news-YYYYMMDD.log`; stored as UTC `DateTimeOffset` with `+00:00` (logs have no TZ offset). |
| Status | `+` = accepted (size present); `-` = rejected (reason text, **no size**). |
| Feed | Explicit token after status — **authoritative** classification. |
| Message-ID | Between `<` `>` including angle brackets stripped for storage. |
| Size | Decimal integer after message-id on accepts; trailing ` ?` observed. |
| Size meaning | Logged article size accompanying accept — treated as **article byte length as recorded by the feeder/server** (not proven wire-vs-payload here; used as workload size). |
| Rejects | Logged; **excluded from size distribution** (no size field). |
| Duplicates | Same message-id can appear if re-offered; records counted independently (no dedupe). |
| Cancels | Lines with status `c` … `Cancelling` — **unparsed / out of scope** for this size analysis. |

---

## 4. Feed classification rules

| Feed token (exact, case-sensitive match on log field) | Class |
|---|---|
| `BlueWorldHosting` | Text / small-article population |
| `Giganews` | Binary / large-article population |
| Any other | Tracked under `OtherAcceptedFeeds` (empty in this capture) |

**Do not** infer feed from message-id host. Giganews and BlueWorldHosting are distinguishable on every accepted/rejected size-bearing record via the feed field.

---

## 5. BlueWorldHosting statistics

| Metric | Value |
|---|---:|
| Accepted | 45 081 |
| Rejected | 909 |
| Total bytes | 241 239 048 (~230 MiB) |
| Min / Max | 512 / 406 735 |
| Mean | 5 351 |
| P50 / P75 / P90 / P95 / P99 / P99.9 | 2 139 / 3 849 / 9 400 / 44 938 / 45 002 / 51 950 |
| Articles/sec (wall) | 0.11 |
| Bytes/sec (wall) | 577 |

Dominant buckets: **1–4 KiB** (70% articles, 27% bytes); **16–64 KiB** only 6% articles but **48% of BWH bytes**.

---

## 6. Giganews statistics

| Metric | Value |
|---|---:|
| Accepted | 90 098 |
| Rejected | 688 |
| Total bytes | 67 970 502 082 (**63.30 GiB**) |
| Min / Max | 971 / 4 158 598 |
| Mean | 754 406 |
| **P50** | **740 484** |
| P75 | ~740 6xx (tight around median) |
| **P90 / P95 / P99** | **792 949 / 793 507 / 2 064 121** |
| Articles/sec (wall ~4.8d) | 0.22 |
| Bytes/sec (wall) | 162 458 (~1.3 Mbit/s average over full capture) |
| Bytes/sec (active seconds only) | much higher — see §10 |

**~700 KiB hypothesis:** validated. Median ≈723 KiB; mode bucket 700–768 KiB.

---

## 7. Combined statistics

| Metric | Value |
|---|---:|
| Accepted | 135 179 |
| Rejected | 1 597 |
| Unclassified accepts | 0 |
| Total bytes | 68 211 741 130 (63.53 GiB) |
| Giganews byte share | **99.65%** |
| P50 (combined) | 740 320 (Giganews-dominated) |
| Wall articles/sec | 0.32 |
| Wall bytes/sec | 163 034 |

**Article rate is a poor proxy for workload:** BWH is ~33% of articles but **0.35% of bytes**.

---

## 8. Article-size distribution

### Giganews (selected)

| Bucket | Articles % | Bytes % |
|---|---:|---:|
| 256–512 KiB | 3.3% | 1.6% |
| 640–700 KiB | 1.2% | 1.0% |
| **700–768 KiB** | **84.7%** | **83.1%** |
| 768 KiB–1 MiB | 7.2% | 7.6% |
| 1–2 MiB | 2.8% | 4.8% |
| 2–4 MiB | 0.5% | 1.7% |
| ≥4 MiB | 0% | 0% |

### BlueWorldHosting (selected)

| Bucket | Articles % | Bytes % |
|---|---:|---:|
| <1 KiB | 6.2% | 0.9% |
| 1–4 KiB | 70.0% | 26.6% |
| 4–16 KiB | 17.5% | 23.5% |
| 16–64 KiB | 6.2% | 47.8% |

Full histograms: `results/real-feed-analysis.json`.

---

## 9. Byte-weighted thresholds

### Giganews — % of total Giganews bytes from articles ≥ threshold

| Threshold | % of bytes |
|---|---:|
| ≥64 KiB | 100.00% |
| ≥128 KiB | 99.99% |
| ≥256 KiB | 99.97% |
| ≥512 KiB | 98.39% |
| ≥640 KiB | 98.29% |
| **≥700 KiB** | **97.28%** |
| ≥768 KiB | 14.17% |
| ≥1 MiB | 6.55% |
| ≥2 MiB | 1.74% |
| ≥4 MiB | 0.00% |

### Combined

≥700 KiB → **96.93%** of all bytes.

**Conclusion:** optimizing the large-article (~700 KiB) data plane targets essentially the entire byte workload.

---

## 10. Temporal / burst behaviour

| Metric | Combined | Giganews | BWH |
|---|---:|---:|---:|
| Peak 1 s articles | 242 | 242 | 10 |
| Peak 1 s bytes | 176 316 832 (~168 MiB/s ≈ **1.41 Gbit/s**) | same | 414 336 |
| Peak 10 s bytes | 962 349 605 (~92 MiB/s avg in window) | same | — |
| Peak 60 s bytes | 5 495 748 466 (~87 MiB/s avg in window) | same | — |

Giganews first/last: `2026-09-18T14:09:24Z` → `2026-09-19T08:51:46Z` (18.71 h wall).  
**848 distinct active UTC seconds** → **~106 articles/active-sec** during activity.

**Empirical pattern:** sustained high-volume runs (dense active seconds), not isolated single large articles. Outside that window the capture is essentially BlueWorldHosting-only (low byte rate).

Wall-clock averages dilute Giganews heavily because most of the multi-day capture has no Giganews traffic.

---

## 11. Representative workload derivation

| Profile | Bytes | Source |
|---|---:|---|
| PROFILE-TEXT | 2 139 | BWH P50 |
| PROFILE-BINARY-MEDIAN | 740 484 | Giganews P50 |
| PROFILE-BINARY-P90 | 792 949 | Giganews P90 |
| PROFILE-BINARY-P95 | 793 507 | Giganews P95 |
| PROFILE-BINARY-P99 | 2 064 121 | Giganews P99 |
| PROFILE-BYTE-WEIGHTED | 740 504 | Size nearest cumulative byte midpoint |
| PROFILE-LARGE | 4 158 598 | Observed max |
| PROFILE-PATHOLOGICAL | 4 158 598 | Same as max (no larger outlier) |

**REAL METADATA** = log sizes. **SYNTHETIC PAYLOAD** = deterministic filled bodies of those lengths for framing benches (not real Giganews content).

---

## 12. Continuous stream design

Prototype: `ContinuousTakethisParser` under `tools/VectorNNTP.NNTPD.MultilineFramerBench/ContinuousStream/`.

```text
MODE STREAM wire:
  TAKETHIS <id>\r\n
  <article bytes>
  \r\n.\r\n          (empty article: .\r\n)
  TAKETHIS <id2>\r\n
  ...
```

- Input: `ReadOnlySequence<byte>` (multi-segment aware).
- Output sink: count articles/bytes; optional checksum; **no disk, no Channel, no spool**.
- Framing: line-anchored `\r\n.\r\n`; does not treat `..\r\n` / `...\r\n` as terminator.
- Preserves wire form (no unstuffing in prototype).

---

## 13. Current vs bulk vs continuous

| Path | Behaviour |
|---|---|
| **A Current** | Command loop → `NntpMultilineDataReader` → materialize `byte[]` → (production would enqueue) |
| **B Continuous bulk** | Stream command scan → `SequenceReader`/`TryReadTo` `\r\n.\r\n` → span sink |
| **C Continuous SIMD** | Same as B but article body scanned via `SimdBulkMultilineFramer` CR-candidate scan |

Head-to-head @ **740 484 B × 32 articles**, **4 KiB segments**, count-only sink (~1.5 s):

| | GiB/s | vs Current | Alloc |
|---|---:|---:|---:|
| A Current | 0.873 | 1.0× | 5.3 GiB |
| B Continuous bulk | 5.277 | **6.0×** | 35 KiB |
| C Continuous SIMD | 8.796 | **10.1×** | 517 KiB |

Text PROFILE @ 2 139 B × 2000, 4 KiB segments: Current 0.49 / Bulk 4.14 / SIMD 5.73 GiB/s — continuous still wins; SIMD helps less absolutely than on large bodies but still ahead.

---

## 14. SIMD design

- Runtime detection: `Vector` / `Vector128` / `Vector256` / `Vector512`, AVX2, AVX-512F.
- This host: Vector=Y, V128=Y, V256=Y, V512=N, AVX2=Y, AVX512F=N.
- Strategy: vector scan for `CR` candidates → scalar validate `\r\n.\r\n` with ≤4-byte lookbehind across segments → scalar fallback always available.
- Production SIMD code untouched; prototype only in `SimdBulkMultilineFramer`.

---

## 15. Segment-size experiment (continuous, 740 484 B × 48)

| Segment | Bulk GiB/s | SIMD GiB/s | Winner |
|---:|---:|---:|---|
| 64 B | **2.51** | 1.79 | Bulk (SIMD worse) |
| 256 B | 4.21 | **5.12** | SIMD |
| 1 KiB | 4.37 | **6.48** | SIMD |
| **4 KiB (Pipe min)** | 4.59 | **8.25** | SIMD |
| 16 KiB | 4.55 | **8.34** | SIMD |
| 64 KiB | 4.44 | **9.09** | SIMD |
| 256 KiB | 4.70 | **9.10** | SIMD |
| contiguous | 5.93 | **9.08** | SIMD |

- **SIMD best:** ~64–256 KiB / contiguous (~9.1 GiB/s).
- **SIMD worst:** 64 B segments (and loses to bulk).
- Production Pipe minimum segment **4 KiB** is already in the “SIMD helps” regime.

Allocations (continuous): bulk ~1–7 KiB per timed window; SIMD ~50–500 KiB (scanner scratch / buffers) — still orders of magnitude below Current’s multi-GiB materialization.

---

## 16. Stream-size experiment

Earlier matrix (pre count-only fix) and architecture compare show throughput stable from ~8 MiB to ~256 MiB wire sizes at ~740 KiB articles — no cliff. **1 GiB optional run not required for correctness;** not claimed here as a separate formal pass after the sink fix (re-run if needed; expect similar GiB/s plateau).

Primary metric remains **byte throughput**, not article count.

---

## 17. Workload profile results

| Profile | Continuous bulk @4 KiB | Continuous SIMD @4 KiB |
|---|---:|---:|
| TEXT (2139 B) | 3.51 GiB/s | 5.39 GiB/s |
| BINARY median (740 484 B) | 4.59 GiB/s | 8.25 GiB/s |
| Arch-compare binary | 5.28 GiB/s | 8.80 GiB/s |
| Arch-compare text | 4.14 GiB/s | 5.73 GiB/s |

HOSTILE-FRAMING (ManyCrlf / ManyDotsHostile) was previously measured in single-article BDN: SIMD wins on CR-dense content; not re-run as continuous MODE STREAM in this pass — prior result stands for scanner stress, not real feed mix.

---

## 18. Allocation results

| Path | Alloc character |
|---|---|
| Current | ~articleBytes × articles × iterations → **multi-GiB**, Gen1/Gen2 thrash on large articles |
| Continuous bulk | Near-zero (few KB) |
| Continuous SIMD | Low hundreds of KB; no Gen2 in 1.5 s windows |

This is the strongest practical argument for span/sequence sinks even when raw GiB/s deltas vary.

---

## 19. Correctness results

`dotnet test` filter `Framing|ContinuousTakethis`: **135 passed**, 0 failed (Release).

Coverage includes empty/normal articles, `.` / `..` / `...` lines, dot-stuffing, CRLF and `\r\n.\r\n` splits, TAKETHIS splits, multi-article segments, CHECK/QUIT escape, incomplete/malformed, INN corpus fixtures (existing), prior bulk/SIMD framing tests.

---

## 20. Control-plane escape analysis

Prototype model (not production):

```text
MODE STREAM
  → continuous parser
       → TAKETHIS → article scanner → sink → next command
       → CHECK    → escape counter / handoff hook
       → QUIT     → escape
       → other    → escape → “normal parser” conceptually
```

Soundness notes (investigation only):

- Command boundaries are CRLF-terminated lines **outside** article state.
- Inside article state, only `\r\n.\r\n` (or empty `.\r\n`) ends the article — CHECK/QUIT bytes inside body are payload.
- Pipelining: continuous buffer can hold many TAKETHIS transactions; responses still require ordered emission in a real server (not implemented here).
- Malformed TAKETHIS / incomplete article → Incomplete result; recovery policy left to session layer.
- Connection close mid-article → Incomplete; no partial enqueue (matches production intent).

---

## 21. Observed bottlenecks (lab)

1. **Materializing `byte[]` per article** — allocation + GC dominate Current path.
2. **Artificial tiny segments (64 B)** — destroy SIMD efficiency; do **not** model production Pipe (4 KiB min).
3. **Checksumming every payload byte in the sink** — initially skewed continuous benches; disabled for throughput runs (`ComputeChecksum` optional).
4. Real feed byte bottleneck is **Giganews ~740 KiB sustained runs**, not text article rate.

---

## 22. What the data proves

1. Giganews is **~99.65% of bytes**; text feed is noise for byte-rate design.
2. Representative binary size is **~740 KiB**, not 2 MiB.
3. **≥700 KiB articles carry ~97% of binary bytes.**
4. Continuous non-materializing framing is **materially higher throughput** and **orders of magnitude lower allocation** than Current on this workload (lab).
5. SIMD **materially helps** continuous binary scanning at ≥~1 KiB / 4 KiB segments on this CPU (AVX2).
6. SIMD **does not** help (and can hurt) at 64 B segments.

---

## 23. What the data does **not** prove

1. Production end-to-end Gbit/s (no TCP/TLS/disk/Channel/responses measured).
2. That 20 000 articles/sec is achievable in production.
3. That SIMD is always faster for all payloads (hostile vs sparse CR content differs).
4. Storage sink design (external object store, DMA, etc.).
5. Correctness of a full MODE STREAM session with response ordering under load.

**Theoretical only (not measured production):**  
20 000 articles/s × 740 484 B ≈ 14.8 GB/s ≈ **118 Gbit/s** wire — labelled theoretical upper-bound arithmetic, not a claim about VectorNNTP.

Peak logged 1 s byte rate ≈ **1.4 Gbit/s** is the strongest *observed* real-feed intensity in this capture.

---

## 24. Recommended NEXT experiment

**PipeReader → continuous SIMD/bulk framer → zero-copy handoff to a non-allocating storage sink**, driven by a synthetic feeder replaying PROFILE-BINARY-MEDIAN sizes at sustained rates approaching the measured peak (~100+ articles/s, ~1 Gbit/s+), still **outside** production `src/`:

1. Measure backpressure when sink is slower than scanner.
2. Validate response-ordering + CHECK/QUIT escape under pipelining.
3. Compare pause writer thresholds (64 KiB) vs article size (~740 KiB).
4. Only then consider a production design proposal — **do not migrate this prototype yet**.

---

## Environment

| Item | Value |
|---|---|
| CPU | 12th Gen Intel Core i9-12900KF |
| Logical processors | 24 |
| OS | Windows 10.0.26200 (win-x64) |
| .NET SDK | 10.0.401 |
| .NET runtime (Host) | 10.0.12 |
| BenchmarkDotNet | (project PackageReference; used in sibling BDN jobs) |
| SIMD | Vector/V128/V256=yes; V512=no; AVX2=yes; AVX-512F=no |

---

## Tooling

```text
dotnet run -c Release --project tools/VectorNNTP.NNTPD.MultilineFramerBench -- --analyze-news-logs [dir]
dotnet run -c Release --project tools/VectorNNTP.NNTPD.MultilineFramerBench -- --continuous-bench --article-bytes 740484 --articles 48 --segment-size 4096 [--simd]
dotnet run -c Release --project tools/VectorNNTP.NNTPD.MultilineFramerBench -- --compare-architectures --article-bytes 740484 --articles 32 --segment-size 4096
```

Default log directory: the Vector.NNTP runtime Logs path above.
