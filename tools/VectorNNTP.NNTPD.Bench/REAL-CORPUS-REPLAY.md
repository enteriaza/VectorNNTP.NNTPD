# Real Giganews corpus → TAKETHIS wire replay investigation

**Status:** investigation / benchmark / prototype only.  
**Constraints satisfied:** no `src/` changes, no production config changes, `C:\Temp\Incoming` untouched, no commit.

Artifacts:
- [`results/real-corpus-analysis.json`](results/real-corpus-analysis.json)
- [`results/real-corpus-analysis.csv`](results/real-corpus-analysis.csv)
- [`results/real-corpus-replay.csv`](results/real-corpus-replay.csv) (latest = 500 MiB subset)
- [`results/real-corpus-replay.txt`](results/real-corpus-replay.txt)
- [`results/real-corpus-replay-500mib.txt`](results/real-corpus-replay-500mib.txt)
- Prior 100 MiB + backpressure: see git history / re-run `--corpus-replay --target-mib 100 --backpressure`

Prototype code: `tools/VectorNNTP.NNTPD.MultilineFramerBench/Corpus/`

---

## 1. Corpus layout (discovered, not assumed)

**Root:** `C:\Temp\Incoming` (immutable for this work)

| Metric | Value |
|---|---:|
| Files | **10 610** |
| Directories | 9 636 |
| Total bytes | **7 806 078 914** (**7.27 GiB**) |
| Max depth | 3 (`aa/bb/digest`) |
| Top-level shards | 256 (`00`…`ff`) |
| Extensions | none (leaf = 64 hex chars) |
| Duplicate basenames | 0 |
| Non-regular files | 0 |
| Min / Max size | 647 / 2 164 763 |
| P50 / mean | **740 474** / 735 728 |

**Shard layout:** `{Incoming}/{digest[0..2]}/{digest[2..4]}/{digest}`  
Example: `00\02\00024a9fbd6824b3529c34dabdb6af28505b5be684936bdca64279279736e7ad`

This matches **Vector.NNTP** `SpoolDirectoryUtilities.GetArticleFilePath`, **not** the current VectorNNTP `IncomingSpoolFilePersister` naming (`{sha256}_{ticks}.article` flat).

---

## 2. Filename / message-ID mapping (authoritative)

**User note said “MD5” — that is incorrect for this corpus.**

| Step | Actual rule (from Vector.NNTP `HistoryKeyEncoder`) |
|---|---|
| Input string | TAKETHIS message-id **including** `<` `>` |
| Encoding | UTF-8 (ASCII IDs identical) |
| Hash | **BLAKE3** → 32 bytes |
| Filename | 64 **lowercase** hex characters (no extension) |
| Shard | first 2 hex / next 2 hex / full digest |

Verified: `<3810724$5fcf702$2a675b8@82ecaa5ff0.e67ff>` → `00024a9f…e7ad` matches on-disk file.

Current VectorNNTP `BuildFileName` uses **SHA-256(ASCII(message-id))** + ticks + `.article` — **different layout**; not used by `C:\Temp\Incoming`.

---

## 3. Storage vs wire (critical)

Stored payloads are **de-stuffed article bytes** (headers + body), **not** TAKETHIS wire:

| Concern | Behaviour |
|---|---|
| Terminator `.\r\n` | **Not stored** |
| Dot-stuffing | Removed on receive (`..line` → `.line` stored) |
| CRLF | Preserved per line (`\r\n` appended after each decoded line) |
| Empty article | Empty stored payload |
| Other transforms | No recompression observed; Path may use **bare LF** line endings in some articles |

Sources: Vector.NNTP `NntpArticleBodyReader.ProcessLine` (strip one leading `.` when line starts with `..`); VectorNNTP `NntpMultilineDataReader.AppendUnstuffedLine` (same RFC 3977 receive rule).

**Reconstruction:**

```text
stored destuffed bytes
  → for each line: if starts with '.' prepend '.'
  → emit line + CRLF
  → emit terminator .\r\n
  → prefix TAKETHIS <mid>\r\n
```

---

## 4. Message-ID recovery + digest verification

| Metric | Count |
|---|---:|
| Files scanned | 10 610 |
| Message-IDs recovered | **10 610 (100%)** |
| BLAKE3 digest matches filename | **10 610 (100%)** |
| Digest mismatches | **0** |
| Missing Message-ID | **0** (after LF-tolerant header parse) |
| Duplicate Message-IDs | 0 unique collisions recorded as extras |

**Parsing note:** ~676 articles initially failed extraction because `Path:` was terminated with **bare LF** (`\n`) rather than CRLF, so a CRLF-only splitter swallowed `Message-ID:` into the Path line. Fix: accept LF or CRLF when scanning headers. Documented + unit-tested.

Authoritative mid for TAKETHIS / digest = header `Message-ID` value with brackets (matches log / HistoryDB).

---

## 5. Correlation vs news logs

| Metric | Value |
|---|---:|
| Log Giganews accepted (records) | 105 926 |
| Log unique Giganews message-ids | ~105 k |
| Corpus unique message-ids | 10 610 |
| Matched | **10 574** |
| Stored but not in logs | **36** |
| Logged but missing from storage | **~95 352** |

Interpretation: corpus is a **post-cleanup subset** (~7.3 GiB of the ~63 GiB logged Giganews bytes). Size distribution still matches the log-derived ~740 KiB median.

---

## 6. Size distribution (stored corpus)

Agrees with prior log analysis: mass of articles and bytes in **700–768 KiB**.

| Bucket (approx) | Role |
|---|---|
| 700–768 KiB | Dominant article + byte share |
| ≥2 MiB | Rare (max observed ~2.1 MiB) |

Full histogram: `results/real-corpus-analysis.csv`.

---

## 7. Reconstruction correctness

Tests (`ArticleWireReconstructorTests`, 145 framing-related tests green):

- Normal / leading-dot / multi-dot restuff
- Lone `.` content line → `..\r\n.\r\n` (does not terminate early)
- Empty → `.\r\n`
- Destuff→restuff round-trip vs production `NntpMultilineDataReader`
- QUIT/CHECK/TAKETHIS **inside** article body are not control-plane escapes
- BLAKE3 path mapping fixture
- Bare-LF Path header Message-ID extraction

**Cross-check on real 100 MiB / 500 MiB prepared streams:** Current / Continuous bulk / Continuous SIMD / Bulk framer agree on **article counts**; Current destuffed payload sum equals stored byte sum; bulk/continuous wire body bytes agree with each other.

---

## 8. Benchmark method (contamination controls)

1. **Preparation (not timed):** enumerate, recover MID, verify digest, restuff, concatenate continuous TAKETHIS stream into memory.
2. **CORPUS-IN-MEMORY (timed):** segment the prepared wire; run A/B/C parsers; count-only sink (no checksum, no disk).
3. **Backpressure (separate):** Pipe 64 KiB pause / 32 KiB resume; slow consumer; measure FlushAsync pauses.

Filesystem directory walk and BLAKE3 are **outside** the timed data plane.

---

## 9. Results — CORPUS-IN-MEMORY (real Giganews bytes)

### 100 MiB subset (146 articles, wire ≈ 100.4 MiB)

| Segment | Current GiB/s | Bulk GiB/s | SIMD GiB/s |
|---:|---:|---:|---:|
| 64 B | 0.81 | **3.87** | 2.30 |
| 256 B | 1.04 | 6.67 | **7.03** |
| 1 KiB | 1.13 | 7.60 | **11.81** |
| **4 KiB** | **1.25** | **8.29** | **13.71** |
| 16 KiB | 1.26 | 8.12 | **14.64** |
| 64 KiB | 1.32 | 8.04 | **14.88** |
| 256 KiB | 1.28 | 8.12 | **15.02** |
| contiguous | 1.25 | 10.29 | **14.33** |

### 500 MiB subset (716 articles, wire ≈ 500.4 MiB)

| Segment | Current GiB/s | Bulk GiB/s | SIMD GiB/s |
|---:|---:|---:|---:|
| 64 B | 0.69 | **3.49** | 2.28 |
| **4 KiB** | **1.47** | **6.82** | **13.21** |
| 64 KiB | 1.40 | 7.27 | **14.73** |
| contiguous | 1.72 | 10.17 | **14.28** |

Allocations (representative 100 MiB @ 4 KiB, ~1.5 s window):

| Arch | Allocated | Gen2 |
|---|---:|---:|
| Current | ~7.9 GiB | 28 |
| Bulk | ~3 KiB | 0 |
| SIMD | ~740 KiB | 0 |

---

## 10. Real vs synthetic 740 KiB

| Workload @ 4 KiB | Current | Bulk | SIMD |
|---|---:|---:|---:|
| Synthetic fixed 740 484 B | ~0.87 GiB/s | ~5.3 GiB/s | ~8.8 GiB/s |
| **Real Giganews corpus** | ~1.25–1.47 | ~6.8–8.3 | **~13.2–13.7** |

SIMD remains beneficial on **real** article bytes and is **faster** than the synthetic median-body case (real yEnc/CRLF structure is CR-friendlier for the candidate scanner). Do **not** treat GiB/s as production NIC throughput.

---

## 11. Backpressure (100 MiB wire, Pipe pause 64 KiB)

| Mode | Elapsed | Peak buffered (est.) | Producer pause flushes |
|---|---:|---:|---:|
| Unbounded | ~13 ms | ~49 KiB | 16 |
| Fast | ~8 ms | ~49 KiB | 12 |
| Medium | ~25 s | ~49 KiB | 1606 |
| Slow | ~50 s | ~49 KiB | 1607 |

**Proven:** Pipe pause/resume bounds buffer growth near the pause threshold; slow sinks block the producer via `FlushAsync` instead of unbounded RAM growth.  
**Not proven:** end-to-end behaviour with a real storage sink or TCP.

---

## 12. Control-plane escape

Retained in continuous parser: outside article → TAKETHIS / CHECK / QUIT / other; inside article → payload until `\r\n.\r\n`. Real corpus bodies containing those tokens as text do not escape (tested).

---

## 13. Conclusions

### Proven
1. Real Incoming corpus is BLAKE3-sharded Vector.NNTP spool, fully recoverable (100% MID + digest).
2. Stored bytes are destuffed; wire can be reconstructed and round-tripped through production destuff.
3. Continuous non-materializing parse of **real** reconstructed TAKETHIS streams is **materially faster** than Current and nearly allocation-free.
4. SIMD remains a **large win** on real Giganews bytes at ≥1 KiB / 4 KiB segments; loses at 64 B segments.
5. Pipe backpressure can bound memory when the sink is slow.

### Observed
- Corpus P50 ≈ 740 KiB aligns with log metadata.
- Real SIMD @4 KiB ≈ 13–14 GiB/s (lab, in-memory wire).

### Not proven
- Production end-to-end Gbit/s (TCP/TLS/disk/Channel/responses).
- That migrating continuous parsing into `src/` is safe under all MODE STREAM + response-ordering cases.
- Full 7.27 GiB in-memory replay (subsets used; preparation would dominate RAM).

### Recommended next experiment
**PipeReader continuous SIMD/bulk framer → non-allocating storage sink** driven by prepared real-corpus wire (or filesystem streaming after prep list), with response-ordering stubs and sink rates matching spool — still outside production `src/`.

---

## Environment

Same host as prior feed analysis: i9-12900KF, 24 LP, Windows 10.0.26200, .NET 10.0.12, AVX2 yes / AVX-512 no.

## Commands

```text
dotnet run -c Release --project tools/VectorNNTP.NNTPD.MultilineFramerBench -- --analyze-corpus [C:\Temp\Incoming]
dotnet run -c Release --project tools/VectorNNTP.NNTPD.MultilineFramerBench -- --corpus-replay --target-mib 100 --backpressure
dotnet run -c Release --project tools/VectorNNTP.NNTPD.MultilineFramerBench -- --corpus-replay --target-mib 500
```

## Integrity

| Check | Result |
|---|---|
| Production `src` changed by this task | **NO** |
| Production config changed | **NO** |
| `C:\Temp\Incoming` modified | **NO** |
| Commit created | **NO** |
