# Vector Article Transfer Protocol (VATP)

Status: Phase 1 Common foundation + Phase 2 BackFiller VATP **server** are
implemented. The NNTPD VATP client, RabbitMQ `articleId` extensions, and
ARTICLE/HEAD/BODY/STAT integration are **not** implemented yet.

Owner: `VectorNNTP.Common` (`VectorNNTP.Common.Transport.ArticleTransfer`).
BackFiller adapter: `VectorNNTP.BackFiller.Listener.VatpListenerSession` on the
existing TLS `CacheListenerService` (`BackFiller:BindPortTls`).
Consumers: `VectorNNTP.NNTPD`, `VectorNNTP.BackFiller`, and future Storage. Common
does not reference any of those applications.

## Purpose

VATP is a byte-oriented, multiplexed TCP framing protocol for transferring one
CanonicalV1 `ArticleRecord` per stream. RabbitMQ remains the control plane.
Article bytes do not travel over RabbitMQ, JSON, XML, or protobuf.

## Byte order and header

All multi-byte integers are **big-endian**. Fixed header is **16 bytes**:

| Offset | Size | Field |
|-------:|-----:|-------|
| 0 | 1 | Version (`0x01`) |
| 1 | 1 | Type (`VatpFrameType`) |
| 2 | 2 | HeaderLength (must be 16) |
| 4 | 4 | StreamId (`0` = connection-level) |
| 8 | 4 | PayloadLength (this frame only) |
| 12 | 4 | Flags (bit 0 = FIN on DATA; bits 1–31 MBZ) |

## Frame types

| Value | Name | StreamId | Payload |
|------:|------|----------|---------|
| `0x00` | HELLO | 0 | magic `VNATP01\0` + `u32` maxFramePayload |
| `0x01` | OPEN | ≠0 | 16-byte RequestId GUID + 32-byte `ArticleId` |
| `0x02` | META | ≠0 | 76-byte META |
| `0x03` | DATA | ≠0 | ArtData chunk; Flags.FIN on last |
| `0x04` | END | ≠0 | empty |
| `0x05` | FAIL | 0 or ≠0 | `u16` error + optional ASCII reason ≤ 256 |
| `0x06` | CANCEL | ≠0 | empty |
| `0x07` | WINDOW | ≠0 | `u32` add-credit |

Unknown types, reserved flags, wrong StreamId for the type, and payloads larger
than the HELLO-negotiated max are rejected.

## HELLO

After TLS, peers exchange HELLO on StreamId 0. There is no version negotiation
framework beyond Version = 1 and exact magic match.

**Direction (BackFiller server):** the client sends HELLO first; the server
validates magic / maxFramePayload / StreamId 0, then replies with HELLO using
`min(clientMax, serverDefault)`. OPEN is rejected until the client HELLO is
accepted. Duplicate or malformed HELLO terminates the connection.

Default advertised `maxFramePayload` is **64 KiB** (`VatpProtocol.DefaultMaxFramePayload`).
That default is also the sensible DATA payload policy default; hosts may advertise a
different value within protocol rules.

## META (76 bytes)

| Offset | Size | Field |
|-------:|-----:|-------|
| 0 | 8 | ArtHash (XXH3-64 of ArtData) |
| 8 | 4 | ArtLines |
| 12 | 4 | ArtSize |
| 16 | 1 | SelectedDateHdr (`NntpArticleHeaderName` Date-family) |
| 17 | 3 | Pad (MBZ) |
| 20 | 56 | FieldTable — 7 × (`i32` offset, `i32` length) |

Not on the wire: ArtId, ArtType, CanonicalUtc, ParseStatus, header/body split.

## ArticleId vs ArtHash

- `ArticleId` = BLAKE3(Message-ID value bytes). Carried on OPEN; recomputed from ArtData.
- `ArtHash` = XxHash3-64(ArtData). Carried on META; recomputed from ArtData.

MD5 / `cache://` identities are **not** part of VATP.

## Canonical transfer factory

```text
ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, meta, expectedArtId)
```

Validates size, ranges, ArtId binding, ArtHash, Locate equality, Date → CanonicalUtc,
and ArtLines sanity; classifies ArtType cheaply; assigns `ParseStatus = CanonicalV1`.
Does **not** destuff, parse, rematerialize Path, or recount body lines.

There is no trusted `ArticleRecord.FromCanonicalTransfer` constructor.

## Transfer state machine (receiver)

```text
OPEN accepted
  → AwaitingMeta
  → META → ReceivingData
  → DATA* (exact ArtSize) with FIN on the final DATA frame
  → AwaitingEnd
  → END
  → TryCreateFromCanonicalTransfer
  → Completed (consumable) | Failed
```

**Completion contract:**

- `FIN` marks the final DATA frame (exact `ArtSize` must already be assembled).
- `END` confirms transfer completion.
- An `ArticleRecord` becomes consumable only after ArtSize bytes + FIN + END and
  successful canonical validation.
- Neither FIN nor END alone exposes an article.

Invalid sequences (DATA before META, duplicate META, DATA after FIN, END before
ArtSize+FIN, duplicate END, etc.) are deterministic protocol errors.

## Flow control

`ArticleTransferWindow` tracks per-stream credit. Initial credit defaults to **256 KiB**
(`ArticleTransferLimits`) — policy, not a wire constant. WINDOW adds credit (saturating).
Insufficient credit is a flow-control violation. Zero credit pauses only that stream.

WINDOW on an unknown or terminal stream is `UnknownStream`.

## Fairness (scheduling model)

One write pump per connection (adapter-owned; not implemented in Phase 1).
`ArticleTransferReadyRing` documents and provides the ready-ring primitive:

- round-robin among write-ready streams
- at most one DATA frame per turn
- size = min(remaining, credit, maxFramePayload)
- zero-credit streams skipped

## Protocol invariants vs resource policy

**Invariants (Common):** header size, byte order, frame types, StreamId 0 rules,
HELLO magic, META layout, article ceiling `ArticleResourceLimits.MaxArticleBytes`,
malformed-frame rejection.

**Policy (`ArticleTransferLimits`):** max streams, initial window, max credit,
default maxFramePayload. Not frozen as immutable protocol constants.

## Multiplexing primitives

`ArticleTransferStreamTable` bounds active StreamIds, rejects duplicates and StreamId 0,
and requires removal after terminalization before reuse.

## Not implemented yet (Phase 3+)

- NNTPD outbound TLS client / connection pool
- RabbitMQ `articleId` response field
- ARTICLE/HEAD/BODY/STAT command wiring
- Ingest / History integration
- Storage consumers

## BackFiller Phase 2 server

`CacheListenerService` remains the single TLS listener on `BindPortTls`. After the
handshake it peeks the first 16-byte frame header and demultiplexes:

- Type `HELLO` (`0x00`) → `VatpListenerSession` (Common VATP)
- otherwise → legacy MD5 cache `CacheListenerSession`

Both paths share TLS, connection limits, IO timeouts, and shutdown. The legacy
cache protocol is intentionally retained until the VATP path fully replaces it.

Canonical articles enter retention via `IArticleRetentionAuthority.RetainCanonical`
(RequestId + `ArticleRecord`). VATP OPEN resolves RequestId, verifies ArticleId,
acquires a transfer lease, then streams META / DATA / END under per-stream WINDOW
credit and round-robin DATA scheduling.
