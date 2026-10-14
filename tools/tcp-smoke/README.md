# VectorNNTP.TcpSmoke

Isolated TCP client, server, and benchmark for article-sized payloads. It asks whether a very small persistent stream can move 768,000-byte articles, at concurrency 1 to 64, with less machinery than the TFTP prototype and with better end-to-end throughput.

The intended production paths are BackFiller to NNTPD, and NNTPD to StorageServer. This tool is not wired into either path. It does not speak VATP, and it does not open a production StorageServer.

A successful RESULT means the receiver finished the contract for that mode. In transport mode that contract is "the declared length was read and its SHA-256 matched." In file mode it is "the temp file was flushed, its length matched, the optional second hash matched, and the rename succeeded." A completed TCP write is not that contract. An atomic rename is not a promise that the bytes or the directory entry survive power loss. This tool does not call `Flush(true)` and does not fsync the directory.

Run it on a trusted lab network. There is no TLS and no authentication. The listener binds loopback unless `--allow-remote` is set.

## Layout

```
tools/tcp-smoke/                  console tool, net10.0, BCL only
  Protocol/                       56-byte header and exact stream reads
  Transfer/                       TCP client and server
  Payload/                        deterministic pseudorandom and article-shaped bytes
  Benchmark/                      warmup, timed interval, JSON summary
tests/VectorNNTP.TcpSmoke.Tests  header, framing, and loopback tests
```

## Commands

From the repository root:

```powershell
dotnet build -c Release tests/VectorNNTP.TcpSmoke.Tests/VectorNNTP.TcpSmoke.Tests.csproj
dotnet test -c Release tests/VectorNNTP.TcpSmoke.Tests/VectorNNTP.TcpSmoke.Tests.csproj
dotnet run -c Release --project tools/tcp-smoke -- help
```

Transport-only loopback, persistent connections, five seconds after warmup:

```powershell
dotnet run -c Release --project tools/tcp-smoke -- benchmark --mode transport --connections persistent --size 768000 --concurrency 16 --duration-seconds 5 --warmup-transfers 8 --results artifacts/tcp-smoke/transport-c16
```

File-backed loopback. The server writes each article, checks it, renames it, then sends RESULT. The files are deleted at the end unless `--keep-files` is set.

```powershell
dotnet run -c Release --project tools/tcp-smoke -- benchmark --mode file --connections persistent --size 768000 --concurrency 16 --duration-seconds 5 --warmup-transfers 8 --results artifacts/tcp-smoke/file-c16
```

`--reread` hashes the temp file a second time before the rename. `--connections fresh` opens a new TCP connection for every article.

Two processes. The server process chooses storage. The client's `--mode` only labels its report; it does not reconfigure a remote server. Use a new directory or a new `--seed` when the file server is still running, because an existing article id is rejected.

```powershell
dotnet run -c Release --project tools/tcp-smoke -- server --bind 127.0.0.1 --port 6972 --mode transport --max-clients 128
dotnet run -c Release --project tools/tcp-smoke -- benchmark --no-self-host --host 127.0.0.1 --port 6972 --mode transport --size 768000 --concurrency 16 --duration-seconds 5 --warmup-transfers 8

dotnet run -c Release --project tools/tcp-smoke -- server --bind 127.0.0.1 --port 6973 --mode file --root D:\tcp-lab --max-clients 128
dotnet run -c Release --project tools/tcp-smoke -- put --host 127.0.0.1 --port 6973 --file .\article.bin --article-id 1
dotnet run -c Release --project tools/tcp-smoke -- get --host 127.0.0.1 --port 6973 --article-id 1 --output .\article-out.bin
```

Hosts are IP addresses. Names are not resolved. Exit code 0 is a clean run. Exit code 1 is a failed transfer, an incomplete transfer, a hash mismatch, or a warmup failure. Exit code 2 is a bad argument.

## Wire format

Version 1. Every message starts with a 56-byte header. Multi-byte integers are big-endian. The payload, when present, follows immediately and is exactly `payloadLength` bytes. TCP is a byte stream: the receiver reads until it has 56 bytes, then until it has the declared payload. One write may arrive as several reads, and several writes may arrive as one read.

| Offset | Width | Field |
| --- | --- | --- |
| 0 | u8 | Version. This tool writes 1 and rejects anything else. |
| 1 | u8 | Operation. `1` PUT, `2` GET, `3` RESULT. |
| 2 | u16 | Status. Requests use `0`. |
| 4 | u32 | Payload length in bytes. |
| 8 | u64 | Article id. File mode publishes `{id:x16}.bin`. |
| 16 | u64 | Transfer id. The receiver echoes it. It is not a connection id. |
| 24 | 32 | SHA-256 of the payload. GET requests send 32 zero bytes. |

Status values: `0` ok, `1` not found, `2` too large, `3` bad version, `4` bad operation, `5` hash mismatch, `6` exists, `7` busy, `8` incomplete, `9` io error. This server never sends `busy`. A connection past the limit is closed at accept so a client blocked in a large write is not left waiting for a status it cannot read. `busy` is reserved so a later reader can still name that outcome.

The default maximum payload is 8,388,608 bytes. A larger declaration gets RESULT `too large` and the connection is closed without reading the body, because the stream can no longer be framed. A small body can still be in flight; the client then sees either `too large` or a disconnect. Both are failures. Zero-length payloads are valid.

PUT is three frames: header, raw payload, RESULT with length 0. The RESULT hash is the hash the receiver calculated. The receiver sends it only after the mode's contract is done.

GET request length is 0 and the hash is zeros. The response uses operation GET, status ok, the article length, and the hash, then the raw bytes. A missing article is RESULT `not found` with length 0. A transport-only server retains nothing, so GET is `not found`. There is no per-chunk acknowledgement. One connection carries one transfer at a time, then another. Concurrent transfers use concurrent connections.

File mode reserves the article id, writes `.incoming/{guid}.partial`, flushes, checks `FileInfo.Length`, optionally rereads and hashes, then `File.Move` with overwrite disabled. A failed hash or a disconnect deletes the temp file and does not publish. A second PUT of the same id drains the payload, returns `exists`, and leaves the first file unchanged. The reservation is held only for the duration of that PUT.

## What the benchmark counts

The payload is built and hashed once, before the clock. The server hashes every received byte with `IncrementalHash` while it reads, inside the measured interval. File mode uses that same pass for the bytes it writes. `--reread` adds a second hash of the temp file before RESULT. The client compares the RESULT hash with the precomputed hash and counts a mismatch as a verification failure.

Persistent mode opens one connection per worker before warmup and keeps it. Those connect times are reported and are outside the measured wall clock, and they are not added to transfer latency. Fresh mode connects inside every measured transfer. That connect time is included in the transfer latency. Warmup ids and measured ids come from one sequence, so a file run does not collide with its own warmup.

Throughput is successful transfers times payload length, divided by measured wall time. The wall clock includes the tail of transfers that started before the duration expired. Failed and incomplete transfers add no payload bytes. MiB/s is 1024-based. Gbit/s is decimal (`bytes * 8 / 1e9`). Both are payload rates. Loopback payload rate is a memory and CPU measurement on this machine. It is not a NIC line rate, and it is not evidence that a 10 Gbit/s or 30 Gbit/s network can carry the same load.

Self-host CPU is one process and includes both peers. `--no-self-host` CPU in `summary.json` is the client process only. Latency percentiles are nearest-rank over successful transfers. The schema is `vectornntp.tcp-smoke.v1`.

## Defaults

| Option | Default | Bounds |
| --- | --- | --- |
| `--mode` | `transport` for benchmark, `file` for server | `transport` or `file` |
| `--connections` | `persistent` | `persistent` or `fresh` |
| `--size` | `768000` | 0–8388608 |
| `--payload` | `random` | `random` or `article` |
| `--concurrency` | `1` | 1–256. The self-host server accepts at least this many connections. |
| `--transfers` | `16` | used when `--duration-seconds` is 0. Duration mode also stops at 1,000,000. |
| `--duration-seconds` | `0` | 0–3600 |
| `--warmup-transfers` | `4` | failures abort before the clock starts |
| `--timeout-ms` | `30000` | one connect or one transfer |
| `--seed` | `1` | 1–2^64−1. Seed 1 and 768000 random bytes hash to `28fea82d564e69f7e9376919a8e9b718bf7c9c04c9dc7911d7f4164325ac7a01`, the same payload as the TFTP tool. |
| `--server-max-clients` | `128` | idle persistent connections hold a slot |

## Measured results

Release build, `dotnet` SDK 10.0.402, runtime .NET 10.0.13, Windows 10.0.26200, 24 logical processors. Loopback. Payload 768,000 bytes, seed 1, pseudorandom. Warmup 8, excluded from the rates. Every row below had zero failed transfers, zero incomplete transfers, and zero hash mismatches.

The four-transfer smoke completed in 0.004 s with the hash above. That interval is too short to use as a rate. The five-second rows are the measurements.

Transport-only, persistent, both peers in one process. Integrity `hashed_in_memory`.

| Concurrency | Seconds | Success | MiB/s | Gbit/s | Articles/s | Mean ms | p50 | p95 | p99 | CPU % of machine | Allocated MiB |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 5.000 | 6251 | 915.59 | 7.681 | 1250.09 | 0.80 | 0.53 | 1.03 | 8.71 | 3.6 | 16.7 |
| 4 | 5.000 | 34562 | 5062.42 | 42.467 | 6911.89 | 0.58 | 0.53 | 0.79 | 1.03 | 19.4 | 92.7 |
| 16 | 5.001 | 100567 | 14729.11 | 123.557 | 20110.15 | 0.79 | 0.75 | 1.07 | 1.47 | 79.0 | 268.4 |
| 64 | 5.003 | 99267 | 14531.60 | 121.900 | 19840.47 | 3.22 | 3.02 | 4.78 | 9.97 | 90.9 | 213.4 |

File-backed, persistent, same process, no second read. Integrity `published_and_hashed`. CPU stays near one core or below, which fits a disk limit rather than a protocol limit.

| Concurrency | Seconds | Success | MiB/s | Gbit/s | Articles/s | Mean ms | p50 | p95 | p99 | CPU % of machine | Allocated MiB |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 5.024 | 1969 | 287.05 | 2.408 | 391.92 | 2.55 | 1.63 | 9.36 | 15.87 | 3.3 | 131.7 |
| 4 | 5.022 | 1682 | 245.33 | 2.058 | 334.95 | 11.92 | 3.03 | 33.80 | 47.37 | 4.1 | 112.3 |
| 16 | 5.106 | 699 | 100.27 | 0.841 | 136.90 | 115.91 | 109.88 | 171.27 | 219.99 | 0.8 | 46.7 |
| 64 | 5.361 | 782 | 106.84 | 0.896 | 145.87 | 420.80 | 420.37 | 599.24 | 681.78 | 2.3 | 52.6 |

Connection reuse at transport concurrency 16. Fresh connections include connect time in the latency. Mean connect was 0.241 ms, and mean transfer latency was 8.79 ms, so most of the gap is the new connection's transfer, including TCP slow start, rather than the connect call itself.

| Connections | Gbit/s | Articles/s | Mean ms | CPU % of machine |
| --- | ---: | ---: | ---: | ---: |
| persistent | 123.557 | 20110.15 | 0.79 | 79.0 |
| fresh | 11.105 | 1807.39 | 8.79 | 8.7 |

One file run at concurrency 16 with `--reread` was 1.040 Gbit/s (169.29 articles/s, 2.6% CPU, 59.8 MiB allocated). The non-reread concurrency-16 file run was 0.841 Gbit/s. Each figure is a single five-second sample. The second hash did not open a multi-gigabit gap on this disk. Both samples sit near 1 Gbit/s.

A 20-second transport run at concurrency 16 finished at 126.884 Gbit/s (20,651.69 articles/s, 79.0% CPU), slightly above its five-second pair. Allocated bytes were 1105 MiB. Collections were gen0 74, gen1 6, gen2 3. Throughput did not fall across the longer interval.

Separate processes on `127.0.0.1`, concurrency 16, five seconds. JSON CPU is the client. Server processor time was sampled around the client invocation, so it includes warmup and a short shutdown wait.

| Server mode | Payload Gbit/s | Articles/s | Client CPU % | Server CPU % (approx.) | Sample seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| transport, port 6972 | 118.970 | 19363.56 | 24.3 | 51.7 | 5.172 |
| file, port 6973 | 0.908 | 147.87 | 0.5 | 2.6 | 5.283 |

Client integrity for both was `server_result_not_reread_by_client`. No second machine was available. A real network path was not measured.

## Comparison with the TFTP prototype

TFTP was not modified and was not re-run. The numbers below are the ones already recorded in `tools/tftp-smoke/README.md` on this class of machine: Windows 10.0.26200, .NET 10.0.13, 24 logical processors, loopback, 768,000 bytes, seed 1, warmup 8, about five seconds. TCP transport is compared with TFTP memory (`--store none`). TCP file is compared with TFTP file. The two modes are not crossed.

TFTP memory, block 1428, window 16, diagnostics on: concurrency 1 was 1.410 Gbit/s, concurrency 16 was 8.310 Gbit/s and 1,352.5 articles/s at about 68% of the machine. TCP transport on the same shape was 7.681 Gbit/s at concurrency 1 and 123.557 Gbit/s, 20,110 articles/s, at 79% of the machine. Concurrency 64 did not go higher (121.900 Gbit/s at 90.9% CPU). The TCP path has no userspace window, no per-block timer, and no retransmission loop. The kernel's TCP does that work. The 123 Gbit/s figure is still a loopback payload rate.

TFTP same-process file, block 1428, window 16, with a second SHA-256 before the final ACK: concurrency 1 was 0.540 Gbit/s, concurrency 4 was 1.059, concurrency 16 was 0.976, concurrency 64 was 0.992 and had handshake timeouts. The later profile at concurrency 16 was 1.082 Gbit/s. TFTP separate-process file, one hash and a rename, was 0.923 Gbit/s at concurrency 1 and 0.990 at concurrency 16.

TCP file, hash while writing, no second read: 2.408, 2.058, 0.841, and 0.896 Gbit/s at concurrency 1, 4, 16, and 64, with zero failures. The separate-process file run was 0.908 Gbit/s at concurrency 16. TCP is faster at one transfer. At 16 and 64 it is in the same about-1 Gbit/s band as TFTP's file results, and the concurrency-16 sample was a bit lower. Raising concurrency made the TCP file run slower while CPU fell, which is the opposite of the transport run. That is the disk, measured once per cell.

Payload-only arithmetic, with headers and disk ignored: 768,000 bytes is 6,144,000 bits, so 1 Gbit/s is 163 articles/s, 10 Gbit/s is 1,628 articles/s, and 30 Gbit/s is 4,883 articles/s. The TCP file rows are about 137 to 392 articles/s. The TCP transport row at concurrency 16 is far above the 30 Gbit/s article line and used 79% of this machine on loopback. That does not mean a 30 Gbit/s NIC would. No such path was tested.

Non-blank line counts, this tree, excluding `bin` and `obj`: this tool is 1,924 lines in 8 files. The TFTP tool is 3,991 lines in 13 files. The VATP and article-placement sources under `src/` are 7,572 lines in 36 files. The line counts are different kinds of work. TCP still has to grow TLS, identity, and a storage contract before it could stand where VATP stands. What it does not have is a userspace congestion and retransmission engine. That is the complexity this prototype avoids.

## Comparison with VATP

VATP was not modified and was not run. This client cannot talk to a StorageServer.

A same-sized body can be offered to a running StorageServer with the existing smoke tool. That client counts queue admission. Admission is not a verified file, and this TCP run did not perform that admission.

```powershell
dotnet run -c Release --project tools/storage-smoke -- smoke --body-bytes 768000 --sessions 4 --results <dir-outside-spool-and-database>
```

Keep that command pointed at a lab server. Do not point this TCP tool at a production port.

## Tests

`dotnet test -c Release` was run three times on the build used for the table. Each run was 34 passed, 0 failed. The concurrent-id fact repeats its eight-way upload eight times, and the one-client limit fact repeats five times. The suite covers header endianness, version and operation checks, one-byte reads, a truncated header, a truncated body, a header and body that share one buffer, a zero-length put and get, the maximum size, an oversized declaration, a disconnect in the middle of a body, an unknown id, distinct concurrent ids, two puts on one connection, no overwrite, SHA-256 and length, cancellation during publish, loopback put and get, and a one-connection limit. The tests bind `127.0.0.1` with port 0. They do not use an external network or a production service.

## Recommendation

The stream itself is small enough, and on loopback it is fast enough, to prefer over the TFTP prototype when the question is how to move article bytes. Persistent connections are required for that speed: fresh connections at concurrency 16 dropped from 124 Gbit/s to 11 Gbit/s. File publication is a separate result. At concurrency 16 and 64 it stayed near 0.8–0.9 Gbit/s, beside TFTP's file band, with the CPU almost idle. That is not a faster end-to-end storage path.

Keep this tool as a lab measurement. Do not integrate it into BackFiller, NNTPD, or StorageServer, and do not replace VATP. A second machine is the next measurement that would change this recommendation. Until that exists, the supported claim is: a minimal TCP framing moves 768,000-byte articles on loopback with less code than TFTP and with a much higher in-memory rate, and publishing the file still lands near 1 Gbit/s on this disk.

## Limitations

- Trusted network only. No TLS, authentication, compression, or retries.
- Loopback only. Congestion control was not observed on a lossy or high-delay path.
- Rename without a durability flush. A crash can lose a file that RESULT already called ok.
- GET reads the whole published file into memory before it replies. The benchmark path is PUT.
- One transfer at a time on a connection. Extra connections above the limit are dropped, with no RESULT.
- A too-large declaration closes the connection. The sender may observe a reset instead of the status.
- An idle persistent connection holds a server slot for its whole life.
- The benchmark reuses one payload buffer. It does not exercise distinct article bodies, and a live file server rejects a repeated article id.
- No per-interval trace inside the 20-second run. The evidence against degradation is the higher average rate, not a timeline.
