# Article TX data-plane experiment

**Status:** isolated benchmark / design investigation  
**Date:** 2026-09-22  
**Harness:** `tools/VectorNNTP.NNTPD.CommandResponsePathBench/` (`--article-tx`)  
**Results:** `tools/.../results/article-tx-experiment.json`  
**Corpus:** `C:\Temp\Incoming` (read-only)

Evidence labels: **PROVEN** / **OBSERVED** / **INFERRED** / **UNKNOWN**.

---

## 1. Objective

Establish what a production **article TX data plane** should look like when continuously transmitting real stored ~750 KiB binary articles for:

1. **Peer feeding** — `TAKETHIS <mid>` + restuffed body + `.\r\n`  
2. **Customer delivery** — `ARTICLE`-style `220 0 <mid>` + restuffed body + `.\r\n`

without implementing production TX yet.

---

## 2. Corpus

| Property | Value (**OBSERVED**) |
|---|---|
| Root | `C:\Temp\Incoming` |
| Indexed articles (digest-verified) | **10 610** |
| Total stored bytes | **7 806 078 914** (~7.27 GiB) |
| Layout | BLAKE3 fan-out `aa/bb/{64hex}` (existing Vector.NNTP corpus) |
| Storage semantics | Dot-**un**stuffed; CRLF preserved; terminator **not** stored (**PROVEN** via `ArticleWireReconstructor` remarks) |

Index built once per run (path, relative path, digest, message-id, stored length). Corpus files were not modified.

---

## 3. Corpus size distribution (**OBSERVED**)

```text
count=10610 totalBytes=7806078914 (7.27 GiB)
min=647  P50=740474  P90=741075  P95=793113  P99=1082806  max=2164763
```

| Bucket | Count | Bytes |
|---|---|---|
| &lt;256 KiB | 133 | 22.9 MiB |
| 256–512 KiB | 189 | 71.6 MiB |
| 512–700 KiB | 190 | 108.5 MiB |
| **700–768 KiB** | **9376** | **6.46 GiB** |
| 768 KiB–1 MiB | 527 | 399 MiB |
| 1–2 MiB | 183 | 205 MiB |
| &gt;2 MiB | 12 | 24.8 MiB |

**Typical article size:** ~740–775 KiB stored (P50 ≈ 740 474; nearest-to-750 KiB sample used in benches ≈ **774 583** bytes).

---

## 4. Article framing model

Outbound reconstruction uses existing bench helper `ArticleWireReconstructor.RestuffArticle` (same semantics as prior real-corpus replay):

```text
stored (destuffed)
  → restuff lines beginning with '.'
  → append .\r\n
```

| Mode | Header | Body |
|---|---|---|
| Peer TAKETHIS | `TAKETHIS <mid>\r\n` | restuffed + terminator |
| Customer ARTICLE | `220 0 <mid>\r\n` | restuffed + terminator |

Article boundaries: header → body chunks → (terminator included in restuff). Articles A then B then C are written strictly sequentially (**PROVEN** by single writer; **OBSERVED** sequence correctness).

---

## 5. Materialized source

```text
File.ReadAllBytes → RestuffArticle(byte[]) → TX chunks → Channel → Pipe → FlushAsync
```

Peaks simultaneous materialised buffers: stored array + full restuffed array (~1.5× article) before chunking (**PROVEN**).

---

## 6. Streamed source

```text
FileStream reads (64 KiB)
  → line extract + restuff
  → TX chunks
  → Channel → Pipe → FlushAsync
```

No full restuffed `byte[]`. Still allocates per-line arrays in this prototype (intentional simplicity; not ArrayPool).

---

## 7. Chunk strategies

Byte budgets: **16 / 32 / 64 / 128 / 256 KiB**, plus **8 MiB** (whole-article-as-one-Channel-item for ~750 KiB).

Pipe options unchanged: pause **64 KiB** / resume **32 KiB** / minSeg **4 KiB**. Channel capacity **4096**, Wait.

---

## 8. Correctness

**OBSERVED:** `ARTICLE-TX CORRECTNESS: ALL PASSED`

- Materialized == streamed == oracle restuff for smallest, P50, ~750 KiB, P95, P99, largest  
- Both PeerTakeThis and CustomerArticle  
- Multiple chunk sizes including whole-article chunk  
- Dot-leading stored line found in corpus and verified  
- Two-article sequence preserves order  

---

## 9. Benchmark methodology

Release, in-memory instrumented Pipe with FAST/MEDIUM/SLOW drain. Large replays disable wire capture. TCP/TLS **not run** this iteration (**UNKNOWN**).

---

## 10. FAST results (highlights)

### Single ~775 KiB article (PeerTakeThis)

| Mode | Chunk | art/s | out MiB/s | Channel | FlushAsync | Alloc MiB | PeakQ |
|---|---|---|---|---|---|---|---|
| Materialized | 16 KiB | ~843 | ~623 | 48 | 48 | ~5.0 | 16 KiB |
| Materialized | 64 KiB | ~629–883 | ~465–652 | 12 | 12 | ~4.9 | 64 KiB |
| Materialized | 256 KiB | ~864 | ~639 | 3 | 12 | ~4.9 | 256 KiB |
| Materialized | 8 MiB | ~751 | ~555 | **1** | 12 | ~6.4 | ~775 KiB |
| Streamed | 64 KiB | ~429 | ~317 | 12 | 12 | ~3.6 | 64 KiB |
| Streamed | 256 KiB | ~511 | ~377 | 3 | 12 | ~3.6 | 256 KiB |

**OBSERVED:** Chunk size strongly cuts Channel items; FlushAsync bottoms near **~12** for one article because the pump still flushes ≤64 KiB Pipe slices even when Channel holds one large item.

### Sustained replay (PeerTakeThis, FAST)

| Workload | Mode | Chunk | art/s | out MiB/s | Alloc MiB |
|---|---|---|---|---|---|
| ~100 MiB / 145 arts | Mat / 256 KiB | **1083** | **747** | ~401 |
| ~100 MiB | Stream / 64 KiB | 642 | 443 | ~229 |
| ~500 MiB / 717 | Mat / 256 KiB | **1084** | **757** | ~2006 |
| ~500 MiB | Stream / 64 KiB | 680 | 474 | ~1145 |
| ~1 GiB / 1463 | Mat / 256 KiB | **1097** | **768** | ~4108 |
| ~1 GiB | Stream / 64 KiB | 680 | 476 | ~2344 |
| 10 000 arts | Mat / 64 KiB | 639 | 449 | ~28191 |
| 10 000 arts | Stream / 64 KiB | 604 | 424 | ~16056 |

---

## 11. MEDIUM results

10 articles: all modes ≈ **5.8 art/s**, ~4 MiB/s (**OBSERVED**). Drain delay dominates; chunk size / stream vs materialize do not change throughput meaningfully.

---

## 12. SLOW results

Same pattern as Medium in this harness (~5.7–5.8 art/s) (**OBSERVED**). Backpressure equalizes sources.

---

## 13. Memory / allocation results

| | Materialized | Streamed |
|---|---|---|
| Single ~775 KiB alloc | ~4.9–6.4 MiB | ~3.6–5.1 MiB |
| 1 GiB replay alloc | ~4.1 GiB | ~2.3 GiB |
| Peak Channel bytes | ≈ chunk size (or full article if 1 item) | same |
| Peak Pipe unflushed | ≈ 64 KiB | ≈ 64 KiB |

**OBSERVED:** Streaming reduces **allocation volume** (~40–45% on large replays) but does **not** improve FAST throughput in this prototype (often slower). Peak Pipe memory is pipe-limited; peak Channel memory tracks chunk policy.

---

## 14. Materialized vs streamed

| Question | Answer from data |
|---|---|
| Does streaming reduce allocation? | **Yes** (**OBSERVED**) |
| Does streaming reduce peak Pipe memory? | **No meaningful difference** (64 KiB pause) |
| Does streaming win throughput? | **No** on FAST in this prototype (**OBSERVED**) |
| Why slower? | **INFERRED:** per-line restuff + many small writes into chunk builder + FileStream overhead vs one RestuffArticle + bulk WriteBytes |

Streaming is therefore a **memory/GC** lever, not automatically a **throughput** win.

---

## 15. Peer TAKETHIS model

Same TX machinery as customer ARTICLE with a different header. Sustained peer feed ≈ **650–1100 art/s** / **450–770 MiB/s** in-memory depending on materialize+chunk (**OBSERVED**). Ordering is sequential single-writer.

---

## 16. Customer ARTICLE model

**OBSERVED:** essentially identical Channel/Flush/alloc/throughput to PeerTakeThis for the same body. Header is a few bytes; body dominates.

**INFERRED:** one article TX primitive can serve both headers without divergent data paths — still a design inference, not a production decision.

---

## 17. Multiline comparison

| | Overview multiline experiment | Article experiment |
|---|---|---|
| Record size | ~80–200 B lines | ~750 KiB bodies |
| Win from batching | huge (100 k lines → hundreds of chunks) | moderate (48→3 Channel items); FlushAsync floor from Pipe 64 KiB slices |
| Dominant cost | per-line encode/flush | restuff + file I/O + copies into Pipe |
| Shared primitive | owned chunk → Channel → Pipe → FlushAsync | **same shape** (**OBSERVED** workable) |

Article data does **not** need 1 Channel item per “line”; it needs **byte-budget chunks** sized relative to Pipe pause (and optionally whole-article Channel items with multi-slice Flush).

---

## 18. Copy / ownership analysis (**PROVEN** paths)

```text
MATERIALIZED
  disk → stored byte[]                    (copy 1)
  stored → restuffed byte[]               (copy 2 + restuff)
  restuffed → List/chunk → Channel byte[] (copy 3)
  Channel → PipeWriter.GetMemory          (copy 4)
  Pipe → drain/capture                    (copy 5 in harness)
  [future] Pipe → DEFLATE → TLS → socket  (additional; NOT MEASURED)

STREAMED
  disk → read buffer                      (copy 1)
  line → emit buffers → chunk List        (copies per line)
  chunk → Channel byte[]                  (copy)
  Channel → PipeWriter                    (copy)
  … same Pipe onward
```

Not zero-copy. Pipe-owned memory never retained after Advance (**PROVEN** by design).

---

## 19. TX policy seam analysis

Natural admission point (**INFERRED** from architecture + this path):

```text
article source (mat or stream)
  → bounded owned TX chunk
  → [future] ordered TX bandwidth Admit(bytes, class)
  → PipeWriter / FlushAsync
  → transport
```

Do **not** put Mbps policy in ARTICLE/TAKETHIS handlers or inside restuff. Chunk boundary is where byte counts are known and ordering is already FIFO.

---

## 20. Proven facts

1. Corpus is ~7.27 GiB / 10 610 articles; mass in 700–768 KiB.  
2. Restuff oracle + streamed restuff match byte-for-byte.  
3. Peer and customer headers share the same body TX path cost.  
4. Channel item count ≈ ceil(wireBytes / chunkSize) (+ small remainder).  
5. FlushAsync count is lower-bounded by Pipe 64 KiB write loop.  
6. Streaming cuts alloc; materialized wins FAST throughput here.  
7. Medium/Slow backpressure erases source differences.

---

## 21. Observations

- Whole-article Channel item (8 MiB budget) → **1** Channel item but still **~12** FlushAsync for ~775 KiB.  
- 256 KiB chunks often best FAST materialised sustained MiB/s in this run.  
- 10 k article replay: ~640 art/s mat vs ~604 stream; alloc 28 GiB vs 16 GiB (**OBSERVED** GC totals, includes retained measurement artifacts — treat relatively).

---

## 22. Unknowns

- Real TCP / TLS / DEFLATE throughput and CPU  
- Production disk layout / cache hit rates  
- Whether a production streamer with fewer per-line allocs closes the throughput gap  
- Interaction with future TX policy under Slow clients  
- Overlap of disk prefetch with TX (not tested)

---

## 23. Production design implications (qualified)

**INFERRED candidates (not decisions):**

1. One ordered article TX primitive for TAKETHIS feed and ARTICLE body.  
2. Prefer **byte-budget chunks** (~64–256 KiB) aligned with Pipe pause, not per-line items.  
3. Keep materialised restuff as a viable hot path for throughput; consider streaming for memory-constrained multi-connection hosts.  
4. Place future bandwidth Admit after chunk formation.  
5. Do not expect “1 Channel item = 1 FlushAsync” when chunks exceed 64 KiB.

---

## 24. Explicit NOT IMPLEMENTED

- No `src/` / `NntpResponseWriter` / session / TAKETHIS / ARTICLE handler changes  
- No production streaming API, TX policy, pooling, SIMD, unsafe  
- No real TCP/TLS probe this iteration  
- No commit  

---

## Run command

```text
dotnet run -c Release --project tools/VectorNNTP.NNTPD.CommandResponsePathBench -- --article-tx
```
