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
[x] QUIT
[x] STARTTLS
```

## Placeholder (registered, not implemented)

```text
[ ] MODE STREAM
[ ] AUTHINFO SASL
[ ] COMPRESS DEFLATE
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
[ ] IHAVE
[ ] CHECK
[ ] TAKETHIS
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
| `TakeThis.cs` | TAKETHIS |

Registration lives in `DefaultNntpCommandCatalog.cs` (descriptors + authorization metadata only).
