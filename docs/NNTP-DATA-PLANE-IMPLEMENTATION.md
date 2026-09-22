# NNTP RX/TX data plane — implementation architecture

**Status:** authoritative implementation contract  
**Audience:** engineers implementing VectorNNTP.NNTPD article/response TX  
**Date consolidated:** 2026-09-22  

This document is the **source of truth** for the NNTP RX/TX data plane we are implementing.

It consolidates measured evidence and prior design audits. It does **not** describe speculative alternatives.

Labels used throughout:

| Label | Meaning |
|-------|---------|
| **MEASURED** | Demonstrated by a controlled experiment or instrumentation |
| **DECIDED** | We are committing to implement this |
| **REQUIRED** | Protocol, correctness, lifecycle, or ownership invariant |
| **DEFERRED** | Intentionally later; not a current requirement |
| **REJECTED** | Explicitly not to be implemented |

Earlier design documents that said “candidate” or “inferred” are **superseded** where this document states **DECIDED**.

---

## The Decision

**DECIDED:**

> We are implementing **one shared NNTP article TX/data plane**.  
> MODE READER and MODE STREAM differ in **scheduling**, not in article encoding or transport implementation.

```text
                    ┌───────────────────────────────────┐
                    │ Shared Transport                  │
                    │ TCP / TLS / DEFLATE                │
                    │ PROXY / lifecycle / duplex Pipes   │
                    │ ConnectionByteTransport           │
                    └─────────────────┬─────────────────┘
                                      │
                    ┌─────────────────▼─────────────────┐
                    │ Shared Article TX Data Plane      │
                    │                                   │
                    │  article source                   │
                    │    → restuff / wire reconstruction│
                    │    → bounded owned TX chunks      │
                    │    → bounded ordered TX queue     │
                    │    → PipeWriter                   │
                    │    → transport send pump          │
                    └─────────────────┬─────────────────┘
                                      │
                           ┌──────────┴──────────┐
                           │                     │
                 ┌─────────▼────────┐  ┌─────────▼─────────┐
                 │ MODE READER      │  │ MODE STREAM       │
                 │                  │  │                   │
                 │ serial           │  │ bounded pipelined │
                 │ request/response │  │ outstanding work  │
                 │ depth = 1        │  │ depth ≈ 4–16      │
                 └──────────────────┘  └───────────────────┘
```

**DECIDED:** Do **not** build separate STREAM article encoders, TX pipelines, or transports.

---

## Why we chose this

### Evidence summary

| Finding | Kind | Source |
|---------|------|--------|
| Per-line Channel item + FlushAsync is allocation/flush heavy for large multiline | **MEASURED** | `COMMAND-RESPONSE-PATH-AUDIT.md`, `CHUNKED-TX-EXPERIMENT.md` |
| Bounded owned chunks (≈64–256 KiB) cut Channel/Flush pressure; byte-correct vs baseline | **MEASURED** | `CHUNKED-TX-EXPERIMENT.md` |
| Peer TAKETHIS and customer ARTICLE share body TX cost; one primitive fits both | **MEASURED** | `ARTICLE-TX-DATA-PLANE-EXPERIMENT.md` |
| Materialized restuff ≫ prototype streamed throughput; streamed ≪ alloc | **MEASURED** | Article TX + real TCP experiments |
| Pipe FlushAsync ≠ peer delivery / socket completion | **MEASURED** | Command-path audit + real TCP SLOW runs |
| STREAM depth 1 ≈ sequential; depth 4–16 useful; depth 64 no further gain | **MEASURED** | `REAL-TCP-READER-VS-STREAM-EXPERIMENT.md` |
| STREAM advantage modest on localhost Plain TCP; clearer under TLS / multi-conn | **MEASURED** | Real TCP experiment |
| Slow receiver collapses READER and STREAM to receiver-limited rate | **MEASURED** | Real TCP experiment |
| No evidence that READER/STREAM need different article encoding stacks | **MEASURED** | Real TCP Q10 |
| Production already has ordered TX Channel + duplex Pipes + TAKETHIS enqueue-without-flush | **REQUIRED** baseline | Production `NntpResponseWriter`, `NntpConnection` |

### What earlier designs left open (now closed)

| Earlier idea | Resolution |
|--------------|------------|
| Optional “specialized MODE STREAM data plane” (`RX-TX-PIPELINE-DESIGN.md`) | **REJECTED** as a separate article TX/transport stack. **DECIDED:** shared data plane + STREAM scheduling only. |
| Whether READER vs STREAM need different encoding | **DECIDED:** no — resolved by real TCP experiment. |
| Whether in-memory Pipe conclusions alone justify STREAM separation | **DECIDED:** no — real TCP required; they did not justify separation. |

---

## Current production baseline (what already exists)

These are facts about today’s code. Implementation builds **on** them; it does not redesign the transport.

```text
Socket
  └─ ConnectionByteTransport (NetworkStream | SslStream | Deflate wrapper)
       ├─ receive pump → input Pipe.Writer
       └─ send pump    ← output Pipe.Reader

NntpSession.RunAsync
  └─ NntpResponseWriter(Connection.Output)   // ordered TX owner
  └─ serial command loop:
       ReadLineAsync(Input) → parse → DispatchAsync → handler
         handler may ReadArticleAsync(Input)
         handler uses WriteLineAsync | EnqueueLineAsync | WriteMultiline*
```

| Component | Location | Role |
|-----------|----------|------|
| Duplex pipes | `Networking/Transport/NntpConnection.cs` | Separate RX/TX pumps |
| Pipe defaults | `Networking/Transport/NntpPipeOptions.cs` | Pause 64 KiB / resume 32 KiB / minSeg 4 KiB |
| Ordered TX | `Session/Commands/NntpResponseWriter.cs` | Bounded Channel (4096, Wait) → PipeWriter |
| TAKETHIS | `Session/Commands/TakeThis.cs` | Enqueue 239/439 without awaiting network flush |
| Barriers | `StartTls`, `Compress`, `Quit` | Quiesce outbound / pause reads as required |
| MODE | `Session/Commands/Mode.cs` | Sets session mode; does not own TX encoding |

**REQUIRED:** Preserve this layering. Article TX feeds the existing transport; it does not replace it.

**Note:** `NntpResponseWriter` today uses a dedicated Channel pump (`Task.Run`). That established collector pattern remains. **DECIDED:** do **not** add further `Task.Run` layers on the transport/data-plane hot path (e.g. per-article or per-chunk `Task.Run`).

---

## Shared article TX data plane

### Shape

**DECIDED:** Production large-article / large-multiline TX uses:

```text
article source
    ↓
wire reconstruction / restuff (as required)
    ↓
bounded owned TX chunks
    ↓
bounded ordered TX queue   (NntpResponseWriter or successor API on same path)
    ↓
[future] Admit(bytes, class)   ← DEFERRED bandwidth policy seam
    ↓
PipeWriter.FlushAsync
    ↓
transport send pump
    ↓
optional DEFLATE → optional TLS → NetworkStream → Socket
```

### Who shares it

**DECIDED:** One shared path for:

- ARTICLE (customer)
- BODY (where applicable)
- TAKETHIS acceptance responses and future peer article **feed** TX
- Other large multiline article-shaped responses

**DECIDED:** Do **not** create separate materialized or streamed implementations for READER vs STREAM.

### Chunking

**MEASURED:** Per-line Channel/Flush for large bodies is the wrong model.

**DECIDED:** Use **bounded owned chunks** in the experimentally supported range **≈ 64 KiB – 256 KiB**.

**DECIDED:** Keep existing Pipe thresholds:

| Constant | Value | Owner |
|----------|-------|-------|
| `PauseWriterThreshold` | 64 KiB | `NntpPipeOptions` |
| `ResumeWriterThreshold` | 32 KiB | `NntpPipeOptions` |
| `MinimumSegmentSize` | 4 KiB | `NntpPipeOptions` |

**DECIDED:** Do **not** arbitrarily redesign Pipe thresholds as part of article TX work.

**MEASURED / REQUIRED to remember:** One Channel item does **not** necessarily equal one `PipeWriter.FlushAsync`. Chunks larger than the pump’s write slice (commonly 64 KiB) are split across multiple FlushAsync calls.

**REQUIRED:** TX queues remain **bounded**. Do not enqueue a whole multi-megabyte article as an unbounded queue payload merely for convenience. Prefer chunk budgets that keep peak queued memory O(chunk × depth), not O(article × connections) unbounded.

### Source strategy (independent of mode)

**MEASURED** (real TCP, ~100 MiB workload, illustrative):

| Source | App throughput | Allocated |
|--------|----------------|-----------|
| Materialized (`ReadAllBytes` → restuff → chunks) | ≈ 560–600 MiB/s | ≈ 402 MiB |
| Prototype streamed (`FileStream` → line restuff → chunks) | ≈ 310–330 MiB/s | ≈ 229 MiB |

**DECIDED:**

1. Source strategy is **orthogonal** to READER vs STREAM scheduling.
2. Production TX abstraction **must** allow the source strategy to evolve without forking mode-specific stacks.
3. Current implementation **favours the measured high-throughput (materialized) path** unless production memory/concurrency evidence requires a better streaming source.
4. Do **not** depend on the current **prototype** streamer as the production streaming design.

**DEFERRED:** Improved true streaming article source (lower alloc without the present throughput cliff).

---

## MODE READER scheduling

**DECIDED:** READER uses the **shared** TX data plane with **serial** scheduling:

```text
command → execute → produce response/article → transmit (depth = 1) → next request
```

**REQUIRED:**

- Ordered, conservative request/response behaviour.
- No separate article encoding implementation for READER.
- ARTICLE/BODY eventually use shared article/body TX primitives (Phase 2).

**DECIDED:** Outstanding article TX depth for READER customer delivery is **1** (do not pipeline multiple ARTICLE responses for a single connection’s READER traffic model).

---

## MODE STREAM scheduling

**DECIDED:** STREAM uses the **same** TX data plane. The difference is **bounded pipelining**.

**MEASURED:**

| Depth | Behaviour |
|-------|-----------|
| 1 | ≈ sequential (same outstanding policy as READER) |
| 4–16 | Useful pipelining benefit |
| 64 | No additional benefit; sometimes regression (esp. TLS) |
| Slow peer | Depth does not make the receiver faster |

**DECIDED:**

- STREAM **must** use **bounded** outstanding work.
- Do **not** implement unbounded STREAM queues.
- Do **not** implement a separate STREAM TX stack.
- Do **not** duplicate restuff/chunking for STREAM.

### Initial pipeline depth parameter

**MEASURED:** Useful band is **4–16**; experiments did **not** prove one exact production integer.

**DECIDED (implementation parameter):**

| Parameter | Initial value | Notes |
|-----------|---------------|-------|
| `StreamOutstandingArticleDepth` | **8** | Middle of measured useful band |
| Allowed tuning range | **4–16** | Outside this band requires new measurement |

Expose as a configuration seam if appropriate; treat **8** as the starting production default, not as a claim that 8 was uniquely optimal.

---

## Backpressure (six boundaries)

Document and implement these **separately**. Do not collapse them into one concept.

| # | Boundary | Mechanism (conceptual) |
|---|----------|------------------------|
| 1 | Article source | Disk/catalog pace; do not prefetch unboundedly |
| 2 | TX Channel | Bounded Channel + `Wait` |
| 3 | PipeWriter | Pause/resume thresholds |
| 4 | Transport write | Send pump `WriteAsync` / stream flush |
| 5 | TCP send window | Kernel / peer window |
| 6 | Peer receiver | Application drain rate |

**REQUIRED:**

```text
PipeWriter.FlushAsync()  ≠  peer received the bytes
```

**MEASURED:** Under a slow receiver, READER and STREAM converge on receiver-limited throughput; deeper STREAM spends more time blocked in transport writes without finishing articles faster.

**REQUIRED:** Slow clients induce **backpressure**, not unbounded RAM growth.

---

## Ownership rules

**REQUIRED:**

1. Queued TX payloads are **owned** buffers (typically `byte[]` or equivalent with clear ownership). Never queue Pipe `ReadOnlySequence` slices.
2. After a chunk enters the TX queue, the producer **must not mutate** it.
3. The TX consumer owns the payload until write/flush of that item completes (or the item is cancelled/faulted).
4. Pipe memory is valid only between `ReadAsync` and the corresponding `AdvanceTo`.
5. Consumers **must not** retain Pipe memory after `AdvanceTo`.
6. `PipeWriter.GetMemory` buffers are pump-local; Advance/Flush before transferring ownership elsewhere.

---

## Ordering

**REQUIRED:**

- NNTP response **byte order** on a connection is defined by the ordered TX queue (FIFO).
- STREAM may have multiple articles **outstanding**, but responses remain **ordered**.
- Message-IDs stay associated with the correct response (RFC 4644 correlation rules for CHECK/TAKETHIS); server emission remains enqueue-ordered.
- Do **not** trade protocol ordering for throughput.

RFC anchors (local library): RFC 3977 §3.5 (pipeline order); RFC 4644 (CHECK/TAKETHIS); RFC 4642/8143 (STARTTLS); RFC 8054 (COMPRESS).

---

## RX / TX separation

**DECIDED:** Preserve the existing split:

```text
RX:  Socket → ConnectionByteTransport → input Pipe → session/command processing
TX:  handler/session → TX queue → output Pipe → ConnectionByteTransport → Socket
```

**DECIDED:** The TX pump remains independent of the socket receive loop where protocol semantics allow.

**DECIDED:** TAKETHIS / streaming status responses must **not** wait for network flush merely to continue the next eligible pipelined operation (already: `EnqueueLineAsync`).

### Hard barriers (must not weaken)

| Barrier | Behaviour |
|---------|-----------|
| **STARTTLS** | Quiesce outbound; pause/discard unsafe pipelining; upgrade transport |
| **COMPRESS** | Must not pipeline across activation; 206 last uncompressed response |
| **QUIT** | Terminal after acceptance; wait for 205 delivery semantics already defined |
| **AUTHINFO / mode / session state** | Single-writer session invariants |
| **POST / IHAVE body phases** | Exclusive body RX; not free duplex concurrency |

**REJECTED:** Replacing the entire session with unconstrained concurrent command execution.

---

## TAKETHIS

**DECIDED:** TAKETHIS remains a primary STREAM data-plane workload. Intended flow:

```text
receive TAKETHIS
  → parse Message-ID
  → receive article (bounded)
  → bounded ingestion enqueue
  → enqueue 239/439 on ordered TX path
  → continue next eligible pipelined TAKETHIS
```

**REQUIRED:**

- 239/439 enqueue into the ordered TX path **without** awaiting socket delivery.
- Existing bounded ingestion semantics remain.
- Do **not** redesign TAKETHIS protocol semantics during TX implementation.
- Partial articles on disconnect are not enqueued (existing behaviour).

---

## Large customer responses (ARTICLE / BODY / …)

**DECIDED:** Large multiline responses move onto the shared chunked TX data plane.

**REJECTED** for large article bodies (where the new architecture applies):

```text
string → byte[] → Channel item → Pipe copy → FlushAsync   (per line)
```

**DECIDED** future large-response model:

```text
catalog / source cursor
  → bounded owned chunk
  → TX policy / admission point (future)
  → PipeWriter
  → FlushAsync
  → transport
```

Keep source/cursor and scheduling separate from transport.

**DEFERRED:** Concrete large-response catalog cursor implementation details.

---

## Bandwidth / TX policy seam

**DECIDED (placement only):** Future bandwidth admission sits between chunk selection and PipeWriter admission:

```text
bounded TX chunk → Admit(bytes, class) → PipeWriter → transport
```

**DECIDED:** Accounting uses **application-level NNTP octets** (before TLS/DEFLATE) unless a later explicit requirement changes this.

**DEFERRED:** Implementing bandwidth limiting. Mention of the seam is **not** authorization to build it now.

**REJECTED as primary design:** Putting Mbps checks or sleeps inside ARTICLE/TAKETHIS handlers.

---

## Transport (established — do not redesign)

```text
Application octets
  → optional DEFLATE
  → optional TLS
  → NetworkStream
  → Socket
```

**REQUIRED** to preserve:

- TLS upgrade semantics and certificate lease lifetime
- PROXY protocol trust model
- DEFLATE ordering rules
- Pipe ownership and cancellation
- Lifecycle and disconnect handling

Article TX **feeds** this stack.

---

## Performance principles

**DECIDED** implementation principles:

1. Do not optimize localhost numbers in isolation.
2. Do not equate Pipe throughput with TCP throughput.
3. Do not use unbounded queues to obtain throughput.
4. Do not duplicate implementations merely because READER and STREAM schedule differently.
5. Keep article encoding/data production independent of traffic mode.
6. Prefer bounded owned chunks over per-line TX items for large bodies.
7. Preserve response ordering.
8. Preserve backpressure.
9. Do not add locks/semaphores to the hot path unless justified by measurement.
10. Do not add `Task.Run` to transport/data-plane code (beyond the established single TX pump model).
11. Measure before optimizing.
12. Keep benchmark-only conclusions separate from production guarantees.

---

## What not to do

This section exists so we do not rediscover rejected designs.

1. Do **not** add a second article restuffer “for STREAM.”
2. Do **not** add a second Channel→Pipe TX pipeline “for STREAM.”
3. Do **not** invent a STREAM-only transport stack.
4. Do **not** set STREAM outstanding depth to unbounded / “just use Channel capacity.”
5. Do **not** ship large ARTICLE bodies as one Channel item per overview-style line.
6. Do **not** treat `FlushAsync` success as peer ACK.
7. Do **not** buffer whole articles in RAM to “help” slow clients.
8. Do **not** couple source strategy (mat vs stream) to MODE READER vs MODE STREAM.
9. Do **not** change `NntpPipeOptions` thresholds casually to chase bench numbers.
10. Do **not** modify production merely to make a benchmark harness easier.
11. Do **not** add SIMD/unsafe “because articles are binary” without profiling evidence.
12. Do **not** weaken STARTTLS / COMPRESS / QUIT barriers for throughput.

---

## Rejected / not planned

| Item | Status |
|------|--------|
| Separate STREAM article encoder | **REJECTED** |
| Separate STREAM TX pipeline | **REJECTED** |
| Separate STREAM transport | **REJECTED** |
| Unbounded STREAM pipelining | **REJECTED** |
| Per-line Channel/Flush TX for large article bodies | **REJECTED** |
| Treating Pipe FlushAsync as peer delivery | **REJECTED** |
| Unbounded buffering for slow clients | **REJECTED** |
| Duplicate READER and STREAM article implementations | **REJECTED** |
| Premature SIMD/low-level production optimization without profiling | **REJECTED** |
| Production changes merely to accommodate benchmark harnesses | **REJECTED** |
| Unconstrained concurrent command executor replacing the session loop | **REJECTED** |
| Bandwidth limiter inside command handlers | **REJECTED** (as primary design) |

---

## Deferred

| Item | Notes |
|------|-------|
| DEFLATE article TX benchmark | Real TCP experiment deferred compression |
| Cross-host / 100G NIC testing | Localhost only so far |
| WAN / RTT / loss testing | Not measured |
| Improved production streaming article source | Prototype streamer not adopted as-is |
| Production bandwidth admission | Seam only |
| Multi-connection fairness | Partial multi-conn evidence only |
| NUMA considerations | Not studied |
| Large-response catalog cursor | Phase 2 design detail |
| Final production chunk-size tuning | Start in 64–256 KiB; retune with production metrics |
| Final STREAM depth tuning | Start at **8** (range 4–16); retune with production metrics |
| Process-wide memory budgets across connections | Per-connection bounds exist; global not specified here |
| Byte-oriented command parser (vs string-first) | Audit identified seam; not part of article TX phases |

Deferred items are **not** current requirements.

---

## Implementation phases

Do not optimize before the production path is functionally correct.

### Phase 1 — Shared TX primitive

**Status: implemented (2026-09-22).**

Implement the common bounded article TX mechanism on the ordered TX path:

- owned chunks in ≈64–256 KiB band  
- bounded Channel admission  
- PipeWriter flush semantics preserved  
- byte-correct framing/restuff vs oracle  

#### Phase 1 implementation record (details only — decisions unchanged)

| Item | Choice |
|------|--------|
| Ordered TX owner | Existing `NntpResponseWriter` Channel (capacity 4096, `FullMode.Wait`) — **no second Channel / pump / PipeWriter** |
| API | `NntpResponseWriter.WriteArticleAsync(stored, framing[, chunkBytes])` |
| Framing | `NntpArticleTxFraming` / `NntpArticleTxFrameKind` (`CustomerArticle`, `PeerTakeThis`) — mode-independent |
| Restuff | Production `Session.Framing.ArticleWireReconstructor` (oracle-aligned with bench) |
| Chunk budget | `NntpArticleTxChunkBudget` — default **64 KiB**, production range **64–256 KiB** (internal constant seam; not appsettings) |
| Ownership | Fresh `byte[]` per chunk; ownership transfers at Channel enqueue; producer does not mutate after enqueue |
| Flush semantics | Awaits per-chunk flush into outbound Pipe (same as `WriteLineAsync`); not peer delivery |
| Source strategy | Materialized restuff for Phase 1 (high-throughput path) |
| Not in Phase 1 | ARTICLE/BODY handlers, STREAM depth, TAKETHIS feed wiring, bandwidth Admit, streamed source |

### Phase 2 — ARTICLE / BODY integration

**Status: BLOCKED on article retrieval/catalog (2026-09-22).**

Handlers `ARTICLE` / `BODY` / `HEAD` / `STAT` remain deliberate `NntpCommandNotImplemented` placeholders
(`Session/Commands/Article.cs`). There is **no** production article lookup, group/current-article
selection, or catalog API — only TAKETHIS **ingestion** into the spool. Inventing a storage layer
was explicitly out of scope for this phase.

#### What Phase 2 did implement (TX readiness)

| Item | Detail |
|------|--------|
| Shared TX path | Unchanged: one `NntpResponseWriter` Channel → pump → PipeWriter |
| ARTICLE convenience | `WriteCustomerArticleAsync(stored, messageId, articleNumber?)` |
| BODY convenience | `WriteCustomerBodyAsync(fullStored, …)` strips headers at `\r\n\r\n`; `WriteCustomerBodyFromBodyAsync` for pre-split body |
| Framing | `NntpArticleTxFrameKind.CustomerBody` → `222 n mid`; ARTICLE status line supports non-zero article numbers |
| Split helper | `ArticleWireReconstructor.TrySplitHeadersAndBody` |
| Reply codes | `BodyFollows` (222), `HeadFollows` (221), `ArticleExists` (223) |
| Tests | `NntpArticleBodyTxIntegrationTests` — wire correctness + chunked shape without a catalog |

#### Integration boundary (when storage lands)

```text
handler resolves article (FUTURE catalog)
  → WriteCustomerArticleAsync / WriteCustomerBodyAsync
  → restuff → owned chunks → existing Channel → pump → Pipe → transport
```

Do **not** use `WriteMultilineDataAsync` per line for ARTICLE/BODY bodies.

#### Deferred until catalog exists

- Wiring `Article.HandleArticleAsync` / `HandleBodyAsync` to a real lookup
- GROUP / current-article selection semantics
- HEAD / STAT implementation
- Error responses 423/430 for missing articles
- End-to-end session tests against live storage

### Phase 3 — STREAM scheduling integration

Use the same TX primitive with bounded STREAM outstanding depth (default **8**, range 4–16). READER remains depth 1.

### Phase 4 — TAKETHIS integration

Ensure TAKETHIS continues to use ordered TX enqueue-without-flush for 239/439; any peer feed TX uses the shared article primitive. Do not alter protocol semantics.

### Phase 5 — Backpressure / cancellation / disconnect hardening

Verify all six backpressure boundaries under slow consumers and disconnects. No unbounded growth. No Pipe lifetime escapes.

### Phase 6 — Measurement

Benchmark the **production** implementation against the evidence base. Tune chunk size and STREAM depth only with new measurements. Do not temporarily regress invariants for numbers.

---

## Acceptance invariants

Implementation is not done unless all of the following remain true:

- [ ] READER and STREAM share the article TX implementation
- [ ] STREAM outstanding work is bounded (default 8; range 4–16 unless re-measured)
- [ ] READER customer article scheduling remains depth 1
- [ ] TX queues are bounded
- [ ] Response order on a connection is preserved (FIFO TX queue)
- [ ] Message-IDs cannot cross response boundaries
- [ ] No Pipe sequence escapes its lifetime (`AdvanceTo` ends validity)
- [ ] No mutable buffer is reused while queued
- [ ] Slow clients induce backpressure rather than unbounded memory growth
- [ ] `PipeWriter.FlushAsync` is never treated as peer delivery
- [ ] STARTTLS remains a barrier
- [ ] COMPRESS remains a barrier
- [ ] QUIT remains terminal
- [ ] Disconnect cancels pending work; partial TAKETHIS articles are not enqueued
- [ ] No additional transport/data-plane `Task.Run` beyond the established single TX pump model
- [ ] No blocking socket I/O on session/data-plane paths
- [ ] No secrets logged
- [ ] Article framing remains byte-correct
- [ ] Dot-stuffing / restuffing remains correct
- [ ] TCP / TLS / DEFLATE remain layered correctly
- [ ] PROXY / TLS certificate lease / lifecycle semantics remain intact
- [ ] Source strategy (materialized vs streamed) is not forked by MODE
- [ ] Bandwidth policy (when added) sits at the admission seam, not in handlers
- [ ] `NntpPipeOptions` thresholds unchanged unless a dedicated, measured change authorizes it

---

## Configuration parameters (initial)

| Name (conceptual) | Initial | Kind |
|-------------------|---------|------|
| Article TX chunk budget | **64 KiB** default; allowed **64–256 KiB** via `NntpArticleTxChunkBudget` (code constant; not appsettings in Phase 1) | Implementation parameter |
| STREAM outstanding article depth | **8** | Implementation parameter |
| STREAM depth allowed range | 4–16 | Guardrail |
| Pipe pause / resume / minSeg | 64 KiB / 32 KiB / 4 KiB | **REQUIRED** keep unless separately authorized |
| TX Channel capacity | Existing 4096 Wait | Preserve unless separately authorized |

Exact option names are left to the implementation task; values above are the architectural targets. Retune chunk size only with production measurement (Phase 6).

---

## Evidence base

| Document | Contribution |
|----------|--------------|
| `tools/.../REAL-DATA-PLANE-PROTOTYPE.md` | Continuous TAKETHIS parse on real corpus; ordered responses without socket wait; Pipe backpressure under slow sinks; ownership of Pipe sequences |
| `tools/.../RX-TX-PIPELINE-DESIGN.md` | Production RX/TX map; barrier matrix; TAKETHIS enqueue semantics; **superseded** on “separate STREAM data plane” |
| `tools/.../TX-POLICY-DESIGN.md` | Bandwidth admission placement (after ordered selection, before/at PipeWriter); reject handler-local limiting |
| `docs/COMMAND-RESPONSE-PATH-AUDIT.md` | Per-line TX cost; FlushAsync ≠ socket delivery; command string-first RX cost |
| `docs/CHUNKED-TX-EXPERIMENT.md` | Chunking reduces Channel/Flush; 64–256 KiB useful; correctness vs production writer wire |
| `docs/ARTICLE-TX-DATA-PLANE-EXPERIMENT.md` | Real corpus; mat vs stream; peer vs customer framing share body path; policy seam at chunk boundary |
| `docs/REAL-TCP-READER-VS-STREAM-EXPERIMENT.md` | **Decisive** READER vs STREAM TCP/TLS evidence; depth 4–16; reject separate TX stacks |

### Preserved measured facts (real TCP)

- Localhost Plain TCP READER vs STREAM: medians within noise / small %; high variance possible (cold start).
- TLS: STREAM depth ~4 clearer (~+4% median vs READER in that run).
- Depth 4–16 useful; depth 64 no further benefit.
- Materialized ≈ 560–600 MiB/s vs streamed prototype ≈ 310–330 MiB/s (matrix FAST).
- Streamed ≈ 43% less allocation on that workload.
- SLOW receiver: both modes receiver-limited; deep STREAM blocks longer in writes.
- Multi-connection (4): small STREAM aggregate edge (~5%); no inversion of mat/stream tradeoff.
- **No evidence** for separate article TX/data planes per mode.

Detailed tables live in the experiment docs; this file preserves the architectural evidence only.

---

## Document maintenance

When implementation proceeds:

1. Update phase checkboxes / status in PRs that complete a phase.
2. Do **not** silently change **DECIDED** items without a new measured experiment and an explicit decision update.
3. Keep experiment logs under `tools/.../results/`; do not paste full logs back into this file.

**This document is the architectural source of truth for the NNTP RX/TX data plane.**
