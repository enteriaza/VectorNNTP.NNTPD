# Command / response path audit

**Status:** read-only design / performance investigation  
**Date:** 2026-09-22  
**Context docs:** `REAL-DATA-PLANE-PROTOTYPE.md`, `RX-TX-PIPELINE-DESIGN.md`, `TX-POLICY-DESIGN.md`  
**Harness:** `tools/VectorNNTP.NNTPD.CommandResponsePathBench/`

Evidence labels used throughout:

| Label | Meaning |
|---|---|
| **PROVEN** | Established by reading production code and/or deterministic instrumentation |
| **OBSERVED** | Measured in this session’s harness / BenchmarkDotNet run |
| **INFERRED** | Reasonable extrapolation; not directly measured |
| **UNKNOWN** | Not established |

---

## 1. Executive summary

The current command RX path always materializes a **string command line** from the input Pipe (`Encoding.ASCII.GetString`) before tokenization (`string.Split`). That conversion is an **implementation choice**, not a protocol necessity. Pipe ownership is safe: sequences do not escape past `AdvanceTo`.

The current response TX path for multiline APIs is:

```text
string line
  → EncodeAscii (+ optional "." stuff)
  → new byte[]
  → Channel item (1 per line)
  → PipeWriter.GetMemory copy
  → FlushAsync (1+ per line)
```

**PROVEN:** `WriteMultilineDataAsync` produces **one Channel item and one `PipeWriter.FlushAsync` per record**. For 100 000 overview-like lines the instrumented writer issued **100 002** FlushAsync calls (~1.00002 per record including start/end).

**PROVEN:** `WriteLineAsync` / multiline flush-wait completes when the **output Pipe** accepts the write (reader progress / pause threshold). It does **not** wait for socket send completion. Socket progress is the separate send pump (`NntpConnection.SendAsync`). `WaitForOutboundDeliveryAsync` is the closer barrier for “left the application pipe toward transport.”

**PROVEN:** OVER / XOVER / HDR / LIST / NEWNEWS / NEWGROUPS / ARTICLE handlers are **NotImplemented** placeholders. Large-multiline cost is therefore a projection of `NntpResponseWriter` APIs, not live catalog I/O.

**OBSERVED:** Multiline writer allocates ~650–670 B per record at 1 000–100 000 scale (instrumentation + BDN MemoryDiagnoser), with throughput ~3.3e5–4.1e5 records/s on an in-memory drained Pipe (no TLS/socket).

Command-parse allocations (~664–864 B/op in BDN including Pipe setup) are small vs large multiline TX, but the **string-first** design is a clear seam for a future byte-oriented parser.

---

## 2. Current command RX path

```text
Socket
  → ConnectionByteTransport.ReadAsync
  → input Pipe.Writer (receive pump)
  → NntpSession.RunAsync
       NntpCommandLineReader.ReadLineAsync(Connection.Input)
         ReadAsync → SequenceReader.TryReadTo(CRLF)
         Encoding.ASCII.GetString(lineBytes)     // materialize
         AdvanceTo(consumed)
       optional Serilog RX (suppressed for BENCHIT / TAKETHIS)
       NntpCommandParser.TryParse(string)
         Split + ToUpperInvariant + token array
       NntpCommandDispatcher.DispatchAsync
         gates → handler
```

**PROVEN** from `NntpSession.RunAsync`, `NntpCommandLineReader`, `NntpCommandParser`, `NntpCommandDispatcher`.

---

## 3. Current command parsing allocation / copy map

### 3.1 Per transition

| Step | Source | Dest | Alloc? | Copy? | Ownership | Crosses await? | Channel? | Pipe? | Materialise? |
|---|---|---|---|---|---|---|---|---|---|
| Receive pump write | socket buf | Pipe segment | Pipe | yes | Pipe | yes | no | yes | into Pipe |
| `ReadAsync` | Pipe | `ReadOnlySequence` | no* | no | Pipe-owned until Advance | yes | no | yes | no |
| `TryReadTo(CRLF)` | sequence | `ReadOnlySequence` slice | no | no | still Pipe | no | no | yes | no |
| `Encoding.ASCII.GetString` | sequence | `string` | **yes** | **yes** | caller-owned string | no (before Advance) | no | no | **yes** |
| `AdvanceTo` | — | — | no | no | releases Pipe | no | no | yes | — |
| `string.Split` | string | `string[]` | **yes** | logical | owned | no | no | no | yes |
| `ToUpperInvariant` | verb | new string | **yes** | yes | owned | no | no | no | yes |
| `parts.AsSpan(1).ToArray()` | tokens | `string[]` | **yes** | shallow | owned | no | no | no | yes |
| `NntpParsedCommand` | refs | struct | no† | no | holds string refs | may cross await into handler | no | no | — |

\* Pipe may allocate segments under the hood.  
† Struct itself; referenced strings already allocated.

### 3.2 Framing behaviours (**PROVEN** by code)

| Scenario | Behaviour |
|---|---|
| Single complete command | One `ReadAsync`, find CRLF, GetString, Advance consumed |
| Split across segments | Incomplete → `AdvanceTo(examined=End)`, loop ReadAsync until CRLF complete |
| CRLF split (`\r` \| `\n`) | Same examined/consume loop; SequenceReader searches delimiter across segments |
| Multiple commands one segment | First line consumed; next session loop ReadAsync may `TryRead` remaining without new socket I/O |
| Long arguments | Entire line becomes one string; no length cap in reader (**UNKNOWN** DoS bound — not audited here) |
| Malformed / empty | Parser returns false → session writes 501 via `WriteLineAsync` |
| Unknown command | Dispatcher writes 500 via `WriteLineAsync` |
| ASCII protocol syntax | Assumed ASCII (`GetString` / later ASCII encode on TX) |
| Message-IDs / AUTHINFO / GROUP / ARTICLE / TAKETHIS / CHECK / MODE STREAM | Same string path; no special-case byte parse |

### 3.3 Unavoidable vs implementation choice

| Conversion | Classification |
|---|---|
| Socket → Pipe buffer | **Unavoidable** (transport buffering) |
| Locating CRLF in `ReadOnlySequence` | **Unavoidable** for framing |
| `GetString` before parse | **Implementation choice** — grammar is ASCII bytes |
| `Split` / `ToUpperInvariant` / token `string[]` | **Implementation choice** — could be span/token offsets into owned buffer or UTF-8 bytes |
| Handlers needing `string` args today | **Current contract** — changing would be an interface/design task (out of scope) |

---

## 4. Pipe ownership / lifetime analysis

### 4.1 Command lines — **PROVEN** safe

```text
ReadAsync
  → Buffer (Pipe-owned)
  → TryReadLine → GetString (copy out)
  → AdvanceTo(consumed)   // on success: AdvanceTo(buffer.Start, buffer.Start) after slice
  → return string
```

No `ReadOnlySequence` / `ReadOnlyMemory` escapes past `AdvanceTo`. Async continuation after return only holds the **string**.

### 4.2 TAKETHIS article — **PROVEN** materialising copy

`NntpMultilineDataReader.ReadArticleAsync`:

1. `ReadAsync` / line scan on Pipe sequence.  
2. Unstuff into `ArrayBufferWriter<byte>` (copies; multi-segment lines rent `ArrayPool`).  
3. On terminator: `AdvanceTo` then `output.WrittenMemory.ToArray()` → owned `byte[]`.  
4. Returned `ReadOnlyMemory` is that array (safe across awaits / Channel enqueue to ingestion).

Lifetime is safe **because a copy occurs**. Matches `REAL-DATA-PLANE-PROTOTYPE.md`: do not retain Pipe sequences after Advance.

Incomplete/cancel: Advance and return without enqueue (**PROVEN** in `TakeThis`).

---

## 5. Current response TX path

```text
Handler
  → NntpResponseWriter API
       EncodeLine / EncodeAscii / ToArray
       Channel.WriteAsync(WriteRequest { byte[] Payload, TCS? })
  → PumpAsync (SingleReader)
       GetMemory / copy Payload / Advance
       PipeWriter.FlushAsync          ← flush-wait barrier for Write* APIs
       TCS.TrySetResult
  → output Pipe
  → NntpConnection.SendAsync
       PipeReader.ReadAsync
       ConnectionByteTransport.WriteAsync (+ FlushAsync for DEFLATE)
       AdvanceTo
  → [DEFLATE] → [TLS] → Socket
```

---

## 6. Response allocation / copy map

### 6.1 API matrix (**PROVEN** from `NntpResponseWriter.cs`)

| API | Alloc encode? | Copy to Channel? | Enqueue? | Await Channel capacity? | Await Pipe FlushAsync? | Await network? | Order preserved? | Caller memory retained? | 1 Channel item / line? |
|---|---|---|---|---|---|---|---|---|---|
| `WriteLineAsync` | yes (`$"{code} {text}\r\n"` + `GetBytes`) | yes (`byte[]`) | yes | yes if full | **yes** (via TCS) | **no** | yes | no | yes |
| `EnqueueLineAsync` | yes | yes | yes | yes if full | no (caller) | no | yes | no | yes |
| `WriteMultilineStartAsync` | via WriteLine | yes | yes | yes | **yes** | no | yes | no | yes |
| `WriteMultilineDataAsync` | yes (stuff `string` + `GetBytes`) | yes | yes | yes | **yes** | no | yes | no | **yes** |
| `WriteMultilineEndAsync` | `DotCrlf.ToArray()` | yes | yes | yes | **yes** | no | yes | no | yes (terminator) |
| `WriteBytesAndFlushAsync` | **`payload.ToArray()`** even if already `byte[]` | yes | yes | yes | **yes** | no | yes | no | one item / call |

Pump additionally **copies** `Payload` into PipeWriter memory (second copy). Payload arrays become GC-eligible after write; not pooled (**PROVEN**).

### 6.2 Multiline cost shape (**PROVEN** + **OBSERVED**)

```text
record → string → encoded byte[] → Channel item → Pipe write → FlushAsync
```

once per record when using `WriteMultilineDataAsync`.

Instrumented (Release, drained Pipe, `GC.GetTotalAllocatedBytes`):

| Case | Records | FlushAsync | Channel items | Alloc ≈ | Records/s | Bytes advanced |
|---|---|---|---|---|---|---|
| Multiline_10 | 10 | 12 | 12 | 26.8 KiB | ~4.2e3* | 757 |
| Multiline_1000 | 1000 | 1002 | 1002 | 652 KiB (~652 B/rec) | ~3.3e5 | 78 597 |
| Multiline_10000 | 10000 | 10002 | 10002 | 6.54 MiB (~654 B/rec) | ~3.7e5 | 815 696 |
| Multiline_100000 | 100000 | 100002 | 100002 | 65.6 MiB (~656 B/rec) | ~4.1e5 | 8.46 MiB |

\* Small-N timing noise.

BDN MemoryDiagnoser (`WriteMultiline_PerLineFlush`, short job):

| LineCount | Mean | Allocated |
|---|---|---|
| 1 | ~99 µs | 2.01 KB |
| 10 | ~151 µs | 7.91 KB |
| 1000 | ~2.88 ms | 671.52 KB |

(**OBSERVED**; high CI noise due to short InvocationCount — treat as order-of-magnitude.)

EnqueueLine TAKETHIS-style (239 lines + drain): ~210 B/line alloc; FlushAsync still ≈1/item in pump (**OBSERVED**).

---

## 7. FlushAsync semantics

| Location | What it waits for | **PROVEN**? |
|---|---|---|
| `NntpResponseWriter` → `PipeWriter.FlushAsync` | Output Pipe reader to consume enough that pause threshold is satisfied | Yes |
| Completes while bytes remain in output Pipe / transport / socket buffers? | **Yes** — flush ≠ socket ACK | Yes |
| Used as ordering barrier for `WriteLineAsync`? | **Yes** — TCS after successful flush of that payload | Yes |
| Multiline per record? | **Yes** — each `WriteMultilineDataAsync` awaits flush | Yes |
| `EnqueueLineAsync` | Does not await flush; pump still flushes each item | Yes |
| Transport `FlushAsync` after `WriteAsync` | DEFLATE sync-flush / stream flush — still not TCP ACK | Yes (`SendAsync`) |
| `WaitForOutboundDeliveryAsync` | Send pump idle generation after outbound work | Yes (STARTTLS/COMPRESS/QUIT) |

Separation matches `RX-TX-PIPELINE-DESIGN.md`: response ordering (Channel) ≠ transport backpressure (Pipe/socket) ≠ future TX policy.

---

## 8. XOVER / large multiline analysis

### 8.1 Current handlers — **PROVEN**

| Command | Status |
|---|---|
| OVER | NotImplemented |
| HDR | NotImplemented |
| LIST* | NotImplemented (inventory registered) |
| NEWGROUPS / NEWNEWS | NotImplemented |
| ARTICLE / BODY / HEAD | NotImplemented |
| XOVER | Not a separate registry key; OVER is the RFC 3977 command |

If a future handler used today’s `WriteMultiline*` APIs for `XOVER 1-` / `OVER 1-`:

| Records | Channel items | Encode ops | Min FlushAsync | Temp owned payloads | Coupled to Pipe backpressure? | Unbounded producer state? |
|---|---|---|---|---|---|---|
| 10 | 12 | 12 | 12 | 12 arrays | Yes (await flush each line) | Only if cursor buffers all records first (**INFERRED** risk) |
| 1 000 | 1 002 | 1 002 | 1 002 | 1 002 | Yes | Same |
| 10 000 | 10 002 | 10 002 | 10 002 | 10 002 | Yes | Same |
| 100 000 | 100 002 | 100 002 | 100 002 | 100 002 | Yes | Same |

Channel capacity 4096: at 100 000 lines the producer **blocks on Channel.WriteAsync** when the pump lags (**PROVEN** FullMode.Wait) — bounded queue, but still O(n) encode+flush scheduling.

### 8.2 Architectural comparison (design only)

| Current (if WriteMultiline*) | Future direction (`RX-TX-PIPELINE-DESIGN` / `TX-POLICY-DESIGN`) |
|---|---|
| handler builds/holds strings | catalog cursor |
| string → byte[] per line | bounded formatting chunk |
| Channel item per line | ordered TX admission (optional policy) |
| FlushAsync per line | PipeWriter + FlushAsync (chunk / backpressure-sized) |

**Not implemented.** Seams only.

---

## 9. Benchmark methodology

**Harness:** `tools/VectorNNTP.NNTPD.CommandResponsePathBench`  
**Modes:**

1. `--instrument` — deterministic counts (FlushAsync, Channel item estimates, `GC.GetTotalAllocatedBytes`, timing).  
2. BenchmarkDotNet — `CommandParseBenchmarks`, `ResponseWriterBenchmarks` with MemoryDiagnoser; short jobs (noisy absolute times; allocation figures more stable).

**Environment (OBSERVED):** Windows 11, .NET 10.0.12, i9-12900KF, Release.

**Not measured:** TLS, real sockets, catalog I/O, Serilog hot path (session suppresses BENCHIT/TAKETHIS RX logs).

Results artifacts:

- `tools/.../results/command-response-path-instrumentation.json`  
- `tools/.../results/bdn-parse/`, `bdn-response/`

---

## 10. Benchmark results (summary)

### Command parse (BDN, includes Pipe create/fill/complete)

| Method | Mean | Allocated |
|---|---|---|
| Parse_Quit | 2.47 µs | 664 B |
| Parse_Date | 2.28 µs | 664 B |
| Parse_ModeStream | 2.15 µs | 792 B |
| Parse_Group | 2.22 µs | 800 B |
| Parse_AuthInfoUser | 2.29 µs | 856 B |
| Parse_AuthInfoPass | 2.35 µs | 864 B |
| Parse_TakeThis | 2.52 µs | 840 B |
| Parse_Check | 2.41 µs | 824 B |
| Three commands / buffer | 5.08 µs | 848 B |
| Split CRLF | 2.43 µs | 728 B |
| Split command | 2.60 µs | 824 B |

Instrument-only parse (~5 000 iters) showed ~592–792 B/op including Pipe overhead — same order (**OBSERVED**).

### Response writer

See §6.2. Key **PROVEN** ratio: **FlushAsync ≈ records + 2** for multiline start/data/end.

---

## 11. BENCHIT measurement boundaries

**PROVEN** from `BenchIt.cs` + session:

| Included in BENCHIT timing (client RTT) | Notes |
|---|---|
| Command line read + parse + dispatch | Yes (normal session) |
| Handler “execution” | Minimal — returns precomputed `WireResponse` |
| Response formatting / encoding | **No** — static init once |
| Channel enqueue | Yes (`WriteBytesAndFlushAsync`) |
| Extra `ToArray` copy | Yes inside writer |
| Channel dequeue + Pipe write + Pipe FlushAsync | Yes |
| Transport send + socket | Yes (real connection benches) |
| Per-request TX Serilog | **No** (skipped `NntpCommandExecution`) |
| RX Serilog | **No** (session suppresses) |

**Obscures:** parser-only vs encode-only vs Channel vs Pipe vs NIC. BENCHIT is a **transport path** probe with frozen payload, not a multiline formatter / XOVER probe.

---

## 12. Proven facts

1. Command path: Pipe sequence → ASCII string → Split parser.  
2. Command Pipe ownership is correct (copy-before-Advance).  
3. Article path materialises `byte[]` before leaving the reader.  
4. Response multiline = 1 Channel item + ≥1 FlushAsync per line.  
5. Writer FlushAsync ≠ network completion.  
6. OVER/LIST/NEW*/ARTICLE bodies not implemented.  
7. `WriteBytesAndFlushAsync` always `ToArray()`s.  
8. TAKETHIS status uses `EnqueueLineAsync` (no caller flush wait).

---

## 13. Likely bottlenecks

| Item | Label | Why |
|---|---|---|
| Per-line FlushAsync on large OVER | **PROVEN** mechanism; **LIKELY** hot under real TX | O(n) async barriers + Pipe interactions |
| Per-line `byte[]` + Channel item | **PROVEN** | GC pressure ~650 B/record observed |
| String encode path for overview | **LIKELY** | Double representation if catalog already bytes |
| TAKETHIS article `ToArray` | **PROVEN** copy; impact **UNKNOWN** vs disk | Prototype showed non-materializing parse gains |
| Command `GetString`+`Split` | **PROVEN** alloc; **LIKELY** minor vs article/OVER | ~0.7–0.9 KB/cmd |
| BENCHIT `ToArray` | **PROVEN** extra copy | Small vs 750 KiB payload |

---

## 14. Unknowns

- Real OVER backend record production cost vs writer cost.  
- Whether per-line FlushAsync dominates under saturated NIC/TLS (needs socket bench).  
- Gen0/Gen1 rates under multi-connection production load.  
- Optimal chunk size for a future streaming writer (design decision).  
- Whether AUTHINFO PASS string retention policies interact with parse spans (security/design).  
- Command line maximum length / reject policy.

---

## 15. Architectural constraints (do not violate in future work)

From prior design docs + this audit:

- Preserve response ordering (Channel FIFO / single pump).  
- Do not retain Pipe buffers after `AdvanceTo`.  
- Do not conflate FlushAsync with socket completion.  
- Do not move bandwidth policy into handlers (`TX-POLICY-DESIGN.md`).  
- Do not introduce unconstrained concurrent command execution (`RX-TX-PIPELINE-DESIGN.md`).  
- STARTTLS / COMPRESS / QUIT barriers remain as implemented.  
- TAKETHIS `EnqueueLineAsync` semantics must stay unless explicitly redesigned.

---

## 16. Candidate future seams — WITHOUT implementation

1. **Byte-oriented command line tokenizer** (optional owned buffer) — avoid `GetString`+`Split` for hot verbs.  
2. **Chunked multiline TX API** — format N records into one owned buffer / one Channel item / fewer FlushAsync.  
3. **PipeWriter lease streaming** — cursor → GetMemory → Advance → Flush under backpressure (skip Channel for bulk body) while keeping ordering rules explicit.  
4. **WriteBytes without forced `ToArray`** when caller already owns immutable bytes (BENCHIT).  
5. **Separate measurement probes** — parser-only, encode-only, Channel+Pipe, full socket (extend BENCHIT story without collapsing them).

No recommendation to implement any of these in this phase.

---

## 17. Explicit NOT IMPLEMENTED

This investigation did **not**:

- Modify `src/`  
- Change protocol behaviour or public interfaces  
- Add production config / pooling / unsafe / concurrent execution  
- Add streaming response writer or TX policy  
- Change TAKETHIS, STARTTLS, COMPRESS, QUIT, BENCHIT production code  
- Commit

**Created only:** isolated bench tool + this document + result JSON under `tools/` / `docs/`.

---

## Appendix — files

**Inspected (production):**  
`NntpSession`, `NntpCommandLineReader`, `NntpCommandParser`, `NntpCommandDispatcher`, `NntpResponseWriter`, `NntpMultilineDataReader`, `NntpConnection` (send pump), `ConnectionByteTransport` (write path), `TakeThis`, `BenchIt`, `Article`/`Over`/`Hdr`/`List` placeholders, design MDs above.

**Created/modified:**  
`tools/VectorNNTP.NNTPD.CommandResponsePathBench/**`, `docs/COMMAND-RESPONSE-PATH-AUDIT.md`, harness `results/*`.
