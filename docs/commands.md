# NNTP command inventory

Tracker for VectorNNTP.NNTPD command implementations under `src/VectorNNTP.NNTPD/Session/Commands/`.
Command-processing infrastructure (dispatch, catalog, execution, response writer, logging) lives under `src/VectorNNTP.NNTPD/Session/CommandProcessor/`.

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
[x] SPEEDTEST
[x] POST
```

CHECK (RFC 4644 §2.4) uses HistoryDB `PeekAsync` (`438` local/Redis hit, `238` double miss without recording the identifier, `431` Redis unavailable). Consecutive transit-authorized CHECK commands may overlap Redis lookups in a per-session window of **16** (`CheckPipeline.Depth`; architectural constant, not configurable). Responses are emitted in send order. Every other command is a serial barrier: outstanding CHECK replies are drained first. See `docs/architecture.md` (Redis and HistoryDB).

POST (RFC 3977 §6.3.1) is not pipelined. `440` is returned when the session is not permitted to post and no article is read. After `340`, the article is streamed under command-work accounting (`Nntpd:MaxArticleSize` destuffed). Client headers that are accepted are written through; server-owned headers (`Path: .POSTED`, `Injection-Date`, `Injection-Info` without a plaintext peer IP, and authenticated-encrypted `X-Trace`) are generated at the header/body boundary. `NNTP-Posting-Date` and `NNTP-Posting-Host` are discarded and are not generated. The body is copied with stuffing preserved into one owned stuffed buffer (IHAVE/TAKETHIS queue representation) and admitted with `TryAdmit`. History Peek stays after the terminator. `240` is sent only after queue admission; POST does not await spool or worker processing. Newsgroup existence is not looked up; see `docs/configuration.md`.

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
| `SpeedTest.cs` | SPEEDTEST (VectorNNTP diagnostic extension) |
| `Session/SpeedTest/` | SPEEDTEST coordinator, payload, concurrency |

IHAVE (RFC 3977 §6.3.2) is a serial, non-pipelined transit ingest: HistoryDB peek → non-blocking Transit queue probe → `335`/`435`/`436` → raw article receive (frame `CRLF . CRLF`, own stuffed wire, no destuff) → non-blocking `TryAdmit` → `235`/`436`/`437`. IHAVE never waits for queue memory; temporary inability to accept is `436`. Downstream `IhaveArticleInterpreter` destuffs IHAVE payloads exactly once and builds `Article`. TAKETHIS production receive/framing is unchanged. See `docs/architecture.md` (IHAVE article ingestion).

`SPEEDTEST <identifier>` is a VectorNNTP private diagnostic extension (not an RFC command). `<identifier>` is the configured Transit dictionary key (exact ordinal match, one NNTP token). `PeerName` is the human-readable administrative name and is not a command argument. The command is transit-authorized and measures TX on the **current** NNTP session with a synthetic payload. It does not open outbound Transit sockets, does not use article ingestion, HistoryDB, or Redis, and never reports measurements that were not performed. Outbound initiator support is not implemented (`OUTBOUND=NOT-AVAILABLE`). See `docs/architecture.md`.

Article ingestion (TAKETHIS and IHAVE → byte-budgeted queue → `spool/incoming`) lives under `ArticleIngestion/`.

Registration lives in `Session/CommandProcessor/DefaultNntpCommandCatalog.cs` (descriptors + authorization metadata only).
