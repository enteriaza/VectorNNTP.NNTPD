# NNTP command inventory

Tracker for VectorNNTP.NNTPD command implementations under `src/VectorNNTP.NNTPD/Session/Commands/`.

- `[x]` = real protocol behavior present (not merely a stub class).
- `[ ]` = registered placeholder; returns not-implemented (or equivalent) until the RFC-compliant handler is written.

Do **not** mark `[x]` solely because a `.cs` file exists.

## Implemented

```text
[x] CAPABILITIES
[x] AUTHINFO USER
[x] AUTHINFO PASS
[x] MODE READER
[x] HELP
[x] DATE
[x] MODE STREAM
[x] QUIT
[x] STARTTLS
[x] COMPRESS DEFLATE
[x] TAKETHIS
[x] CHECK
[x] IHAVE
```

CHECK (RFC 4644 §2.4) uses HistoryDB (`438` local/Redis hit, `238` double miss, `431` Redis unavailable). Consecutive transit-authorized CHECK commands may overlap Redis lookups in a per-session window of **16** (`CheckPipeline.Depth`; architectural constant, not configurable). Responses are emitted in send order. Every other command is a serial barrier: outstanding CHECK replies are drained first. See `docs/architecture.md` (Redis and HistoryDB).

## Placeholder (registered, not implemented)

```text
[ ] AUTHINFO SASL
[ ] LIST
[ ] GROUP
[ ] LISTGROUP
[ ] NEWGROUPS
[ ] NEWNEWS
[ ] ARTICLE
[ ] HEAD
[ ] BODY
[ ] STAT
[ ] LAST
[ ] NEXT
[ ] OVER
[ ] HDR
[ ] POST
```

## File map

| File | Commands |
|------|----------|
| `Capabilities.cs` | CAPABILITIES |
| `AuthInfo.cs` | AUTHINFO USER, PASS, SASL |
| `Mode.cs` | MODE READER, MODE STREAM |
| `Help.cs` | HELP |
| `Date.cs` | DATE |
| `Quit.cs` | QUIT |
| `StartTls.cs` | STARTTLS |
| `Compress.cs` | COMPRESS DEFLATE |
| `List.cs` | LIST |
| `Group.cs` | GROUP |
| `ListGroup.cs` | LISTGROUP |
| `NewGroups.cs` | NEWGROUPS |
| `NewNews.cs` | NEWNEWS |
| `Article.cs` | ARTICLE, HEAD, BODY, STAT |
| `Last.cs` | LAST |
| `Next.cs` | NEXT |
| `Over.cs` | OVER |
| `Hdr.cs` | HDR |
| `Post.cs` | POST |
| `IHave.cs` | IHAVE |
| `Check.cs` | CHECK |
| `Session/CheckPipeline.cs` | Per-session CHECK overlap window (not a command) |
| `TakeThis.cs` | TAKETHIS |

IHAVE (RFC 3977 §6.3.2) is a serial, non-pipelined transit ingest: HistoryDB peek → non-blocking Transit queue probe → `335`/`435`/`436` → raw article receive (frame `CRLF . CRLF`, own stuffed wire, no destuff) → non-blocking `TryAdmit` → `235`/`436`/`437`. IHAVE never waits for queue memory; temporary inability to accept is `436`. Downstream `IhaveArticleInterpreter` destuffs IHAVE payloads exactly once and builds `Article`. TAKETHIS production receive/framing is unchanged. See `docs/architecture.md` (IHAVE article ingestion).

Article ingestion (TAKETHIS and IHAVE → byte-budgeted queue → `spool/incoming`) lives under `ArticleIngestion/`.

Registration lives in `DefaultNntpCommandCatalog.cs` (descriptors + authorization metadata only).
