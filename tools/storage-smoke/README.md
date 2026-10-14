# VectorNNTP.StorageSmoke

Direct VATP client for repeatable smoke and soak runs against a Windows StorageServer. It offers articles through `ArticlePlacementClient` to one explicit host and port. It does not start the server, write the spool, or open the SQLite index.

VATP `Accepted` is admission to the server's in-memory queue. `client.csv` column `accepted` is that outcome. It is not durable spool publication and it is not an index insert. The client cannot see those stages. `monitor-windows.ps1` copies them from the server log when you point it at the current log file.

Articles are built with internal `ArticleRecordFactory.TryCreate(..., ArticlePathMode.Normalize)`. The public factory always traverses and would prepend an application hop, so `VectorNNTP.Common` grants `InternalsVisibleTo` to `VectorNNTP.StorageSmoke` only. Normalize stays internal. The parser identity is the organizational tracker `news.usenet.ninja`. Normalize does not write that identity as an application hop. A generated source `Path: peer.example` is offered as `Path: news.usenet.ninja!peer.example`. StorageServer's own traverse is what should store `Path: cache01.usenet.ninja!news.usenet.ninja!peer.example` when the server identity is `cache01.usenet.ninja`. The client does not insert `nntpd01.usenet.ninja`.

Build and run from the repository root:

```powershell
dotnet build -c Release tools/storage-smoke/VectorNNTP.StorageSmoke.csproj
dotnet run -c Release --project tools/storage-smoke -- help
```

CI includes this project in `VectorNNTP.NNTPD.sln` because the build checks every project under `tools/`.

## Commands

`help` explains commands, units, defaults, and examples.

`smoke` offers a bounded number of articles (default 4) and exits. Exit code 0 means every offered article was VATP-accepted and no duplicate, rejection, failure, ambiguous, or cancelled outcome was counted. Any other outcome, or a stop before the bound, exits 1.

`soak` offers until `--seconds` elapses (1 through 21600). There is no default duration. A soak that reaches its deadline exits 0 even if some attempts failed; the counts are the result. `stop.request` or Ctrl+C before the deadline exits 1. The process uses its own deadline and does not stop because a terminal or IDE command timed out. Start a long soak with `Start-Process` (see `help`) in a directory that is not the spool or the database directory.

Positional soak, matching the earlier harness, is `soak <seconds> <sessions> <bodyBytes> <resultsDirectory>`. Outstanding stores, host, and port then use the defaults. Do not mix that form with flags.

## Defaults

| Option | Default | Bounds |
| --- | --- | --- |
| `--host` | `cache01.usenet.ninja` | Non-empty DNS name. Omitted uses this host and prints it. A bad value fails. |
| `--port` | `1191` | 1–65535 |
| `--sessions` | `4` | 1–16 parallel connections |
| `--outstanding` | `2` | 1 or 2 STORE operations outstanding on each session |
| `--body-bytes` | `768000` | 64–2000000 bytes |
| `--articles` | `4` | Smoke only. 1–10000 offered articles |
| `--seconds` | none | Soak only. 1–21600 seconds |
| `--results` | none | Required. Refused when `client.csv`, `run.txt`, or `stop.request` already exists |

`--outstanding` is per session. The placement client rejects any other value.

## Results

All timestamps written by the client are UTC ISO-8601.

`run.txt` records the plan at start and a final `status` of `completed` or `incomplete`. A file with no final status means the process ended before it could append one (for example a reboot). `reason` is `completed`, `stop.request`, `cancelled`, `incomplete`, or an exception type.

`client.csv` columns, in order:

`utc,offered,accepted,duplicate,rejected,failed,ambiguous,cancelled,acceptedBytes`

Counters are cumulative totals. A row is written about every 10 seconds and once more at exit. An interval rate is the difference between two rows divided by the gap in their `utc` values. `acceptedBytes` sums `ArtSize` for VATP `Accepted` only.

| Column | Meaning |
| --- | --- |
| `offered` | Articles the client attempted to construct and place |
| `accepted` | VATP queue admission. Not spool publication |
| `duplicate` | Server reported the same article was already present |
| `rejected` | Capacity, pressure, invalid, or conflict |
| `failed` | Local construction failure, transport failure, or no dial |
| `ambiguous` | END was written and RESULT was not observed. The queue may already hold the article |
| `cancelled` | Cancelled before acceptance |
| `acceptedBytes` | Bytes counted for `accepted` only |

`failures.log` records non-accepted outcomes, capped at 1000 lines. The CSV counters continue after that cap. It is absent when every attempt is accepted.

`client.pid` is this process id.

Spool publication, index inserts, WAL size, and disk counters are not in `client.csv`. They are not read from SQLite. On Windows, `monitor-windows.ps1` appends `counters.csv` in the results directory. Its header comments state the units. `diskActivePct` is `\PhysicalDisk(n)\% Disk Time`, which can exceed 100 and is not a utilization percentage. Persistence and index cells are blank when the last 2 MiB of the log file you pass has no matching summary, which happens after the daily log roll. Pass the log file currently being written. The script writes `stop.request` if the server process exits or free space on the spool volume drops below `-StopBelowFreeBytes` (default 500GB).

```powershell
powershell -File tools/storage-smoke/monitor-windows.ps1 -ServerPid 16780 -Results D:\vatp-soak\run1 -SpoolPath D:\spool\cache -LogPath D:\logs\VectorNNTP.StorageServer-20261014.log
```
