# VectorNNTP.TftpSmoke

Isolated TFTP client, server, and benchmark for article-sized payloads. It answers whether RFC 1350 TFTP, plus the block-size and window-size options, is a simple enough and fast enough substitute for VATP on two internal paths: BackFiller to NNTPD, and NNTPD to StorageServer.

It does not integrate with those services. A completed transfer is a file rename after the benchmark checks length and SHA-256. It is not durable article storage, not an index insert, and not proof that an article was accepted by NNTP.

Run it only on a trusted lab network. The server is an unauthenticated UDP file receiver.

## Layout

```
tools/tftp-smoke/                 console tool, net10.0, no production references
  Protocol/                       RFC 1350 / 2347 / 2348 / 7440 codec and negotiation
  Transport/                      UDP sockets
  Transfer/                       client, server, windowed send and receive
  Payload/                        deterministic pseudorandom and article-shaped bytes
  Benchmark/                      warmup, timed interval, JSON summary
tests/VectorNNTP.TftpSmoke.Tests in-memory loss, reorder, and loopback tests
```

The in-memory datagram seam lives in the test project. The tool does not load a transport plugin.

## Commands

From the repository root:

```powershell
dotnet build -c Release tools/tftp-smoke/VectorNNTP.TftpSmoke.csproj
dotnet test -c Release tests/VectorNNTP.TftpSmoke.Tests/VectorNNTP.TftpSmoke.Tests.csproj
dotnet run -c Release --project tools/tftp-smoke -- help
```

Self-contained loopback benchmark. This is the default. It starts the server, uploads, checks length and SHA-256, publishes each file by rename, then deletes it unless `--keep-files` is set.

```powershell
dotnet run -c Release --project tools/tftp-smoke -- benchmark --size 768000 --concurrency 16 --transfers 1000 --block-size 1428 --window-size 16 --results artifacts/tftp-smoke/run1
```

Timed interval after warmup:

```powershell
dotnet run -c Release --project tools/tftp-smoke -- benchmark --size 768000 --concurrency 4 --duration-seconds 5 --block-size 1428 --window-size 16 --results artifacts/tftp-smoke/c4
```

Two terminals, if you want the server left running:

```powershell
dotnet run -c Release --project tools/tftp-smoke -- server --bind 127.0.0.1 --port 6969 --root D:\tftp-lab
dotnet run -c Release --project tools/tftp-smoke -- benchmark --no-self-host --host 127.0.0.1 --port 6969 --size 768000 --concurrency 4 --transfers 100
dotnet run -c Release --project tools/tftp-smoke -- upload --host 127.0.0.1 --port 6969 --file .\article.bin --block-size 1428 --window-size 16
dotnet run -c Release --project tools/tftp-smoke -- download --host 127.0.0.1 --port 6969 --file article.bin --output .\article-out.bin
```

`--no-self-host` cannot check the receiver's bytes. The JSON field `integrity` is `not_verified_external_server`. Hosts are IP addresses. Names are not resolved.

Exit code 0 is a clean run. Exit code 1 is a failed transfer, hash mismatch, or warmup failure. Exit code 2 is a bad argument.

## Defaults

| Option | Default | Bounds |
| --- | --- | --- |
| `--size` | `768000` | 0–8388608 bytes. 768000 is 750 KiB |
| `--payload` | `random` | `random` or `article` |
| `--concurrency` | `1` | 1–256 |
| `--transfers` | `16` | used when `--duration-seconds` is 0 |
| `--duration-seconds` | `0` | 1–3600 runs until that wall time after warmup |
| `--warmup-transfers` | `4` | failures abort before the measured interval |
| `--block-size` | `1428` | 8–65464. 1428 keeps a DATA datagram inside a 1500-byte IPv4 frame |
| `--window-size` | `16` | 1–1024 in the benchmark. The server's own default cap is 64 |
| `--timeout-ms` | `250` | retransmission wait. The receiver also lingers this long after the final ACK |
| `--retries` | `6` | consecutive timeouts before a transfer fails |
| `--results` | `artifacts/tftp-smoke/<utc>` | `summary.json` plus human text on stdout |
| `--store` | `file` | `file` is the end-to-end result. `none` hashes in memory and creates no article file |
| `--diagnose` | off | adds summed socket, file, hash, and ACK-wait times. The sums overlap |

`--payload article` is a fixed ASCII header plus a pseudorandom body of the requested length. It is not produced by `ArticleRecordFactory` and is not evidence that NNTP would accept it. `--payload random` is an incompressible xorshift stream. The same `--seed` (default 1) always produces the same bytes.

Payload generation happens before the timer starts. The timed section is the transfer, the receiver hash, a second streaming hash of the temp file, and the rename. `--keep-files` skips the delete that follows a successful check.

## What was implemented

- RFC 1350 RRQ, WRQ, DATA, ACK, and ERROR. Octet mode only. Each transfer uses a new UDP port as its transfer identifier. The listen port is not reused for the data.
- Retransmission on the sender's acknowledgement timer, with a finite retry budget. A timeout is not an immediate permanent failure. Duplicate DATA is acknowledged again and not written twice. A burst of duplicate ACKs for one block retransmits the outstanding window once; further copies of that ACK wait for the timer.
- The last DATA block is shorter than the block size. When the length is an exact multiple of the block size, the sender adds a zero-length DATA block. Block numbers are 16-bit and wrap.
- RFC 2347 OACK. Unknown options, including RFC 2349 `tsize` and `timeout`, are omitted. An illegal `blksize` or `windowsize` is error 8. The server may lower a value. It does not raise one. If the client rejects the OACK, it sends error 8.
- RFC 2348 `blksize`, 8 through 65464.
- RFC 7440 `windowsize`. The receiver acknowledges the last in-order block when the window fills, when it sees a gap, or when the final block arrives. Out-of-order blocks inside the window are held until the gap is filled.

Not implemented: netascii and mail, multicast TFTP, authentication, encryption, congestion control, and RFC 2349 option negotiation. A filename must be one segment of letters, digits, `.`, `_`, and `-`, with no `..`. An existing file is error 6 and is not overwritten. The receiver writes under `.incoming` and renames into the root only after the benchmark's hash check, or, for a standalone server, after the local hash is computed. The standalone server also writes a sibling `.sha256` file. That file is the hash of the bytes it got. It does not prove the sender's intent.

## Measured results

Environment: Windows 10.0.26200, .NET 10.0.13, 24 logical processors, loopback, both peers in one process. Payload 768000 bytes, seed 1, pseudorandom unless noted. Warmup 8 transfers, excluded from the rates. Timeout 250 ms, 6 retries. Every run below had zero failed transfers and zero hash mismatches. `integrity` was `all_verified`.

Payload MiB/s is article bytes divided by measured wall time. Gbit/s is those same bytes times 8, decimal. Neither number is NIC line rate. Wire counters are TFTP datagram bytes and exclude IP and UDP headers. Retries and timeouts are the sum of the client and server counts. CPU is the process processor time for both peers divided by wall time and by 24 logical processors. Allocations are `GC.GetTotalAllocatedBytes` over the measured interval.

| Run | Concurrency | Block | Window | Seconds | Success | Payload MiB/s | Payload Gbit/s | Articles/s | Mean ms | p50 | p95 | p99 | Retries | Timeouts | CPU % of machine |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| c1-b1428-w16 | 1 | 1428 | 16 | 5.009 | 440 | 64.33 | 0.540 | 87.84 | 11.38 | 10.90 | 14.35 | 16.42 | 0 | 0 | 4.7 |
| c4-b1428-w16 | 4 | 1428 | 16 | 5.001 | 862 | 126.23 | 1.059 | 172.35 | 23.20 | 14.68 | 49.22 | 62.54 | 0 | 0 | 9.5 |
| c16-b1428-w16 | 16 | 1428 | 16 | 5.076 | 806 | 116.29 | 0.976 | 158.78 | 100.09 | 99.15 | 157.66 | 189.62 | 0 | 0 | 10.0 |
| c64-b1428-w16 | 64 | 1428 | 16 | 5.351 | 864 | 118.27 | 0.992 | 161.47 | 382.44 | 385.55 | 528.32 | 576.36 | 684 | 651 | 10.3 |
| c16-b512-w1 | 16 | 512 | 1 | 4.148 | 418 | 73.81 | 0.619 | 100.77 | 157.45 | 156.01 | 233.85 | 278.70 | 0 | 0 | 39.6 |
| c16-b512-w16 | 16 | 512 | 16 | 4.167 | 639 | 112.31 | 0.942 | 153.33 | 102.71 | 95.50 | 170.44 | 203.33 | 0 | 0 | 21.1 |
| c16-b1024-w8 | 16 | 1024 | 8 | 4.092 | 652 | 116.69 | 0.979 | 159.32 | 99.26 | 94.61 | 158.84 | 193.76 | 0 | 0 | 14.0 |
| c16-b1428-w8 | 16 | 1428 | 8 | 4.071 | 669 | 120.36 | 1.010 | 164.33 | 96.35 | 93.92 | 155.72 | 188.26 | 0 | 0 | 9.5 |
| c16-b1428-w32 | 16 | 1428 | 32 | 4.082 | 768 | 137.81 | 1.156 | 188.16 | 84.30 | 81.74 | 151.07 | 171.39 | 0 | 0 | 10.3 |
| c16-b2048-w16 | 16 | 2048 | 16 | 4.092 | 780 | 139.61 | 1.171 | 190.61 | 82.96 | 78.87 | 139.02 | 170.32 | 0 | 0 | 7.8 |
| c16-b8192-w16 | 16 | 8192 | 16 | 4.058 | 788 | 142.23 | 1.193 | 194.19 | 81.94 | 81.30 | 125.33 | 149.67 | 0 | 0 | 3.9 |
| c16 article, 1428/16 | 16 | 1428 | 16 | 4.056 | 682 | 123.14 | 1.033 | 168.13 | 94.46 | 91.68 | 156.02 | 189.87 | 0 | 0 | 9.9 |

A short keep-files smoke of 8 transfers at concurrency 1, block 1428, window 16, wrote `m-0000000000000001-00000001.bin`. Length 768000. SHA-256 `28fea82d564e69f7e9376919a8e9b718bf7c9c04c9dc7911d7f4164325ac7a01`, matching `summary.json`. That sample was only 0.136 s and is not the rate in the table. The 5.009 s concurrency-1 row is the one to keep.

Allocated bytes over the measured interval were 211 MiB at concurrency 1 (block 1428, window 16), 431 MiB at concurrency 4, 369 MiB for the 1428/32 row, 204 MiB for the 8192/16 row, and 1080 MiB for classic block 512 / window 1. Classic TFTP also used 39.6% of the machine. Larger blocks cut both CPU and allocations because they cut datagrams per article. Gen2 collections still occurred on every row.

The fastest row was concurrency 16, block 8192, window 16, at 142.23 MiB/s (1.193 Gbit/s), with no retries. That block does not fit in a 1500-byte Ethernet frame: a DATA datagram is 4 bytes of TFTP header plus 8192 bytes of payload, before IP and UDP. Loopback does not show the fragmentation that path would pay. Block 2048 has the same problem and was 139.61 MiB/s.

The fastest row in that first table that stays inside a 1500-byte IPv4 datagram was concurrency 16, block 1428, window 32, at 137.81 MiB/s (1.156 Gbit/s). A later reproduction on the same machine, still without `--diagnose`, moved that shape: concurrency 4 and 16 at block 1428 window 16 both landed near 1.08 Gbit/s, and block 8192 window 16 landed at 1.391 Gbit/s. Treat the file-benchmark band as about 0.5 Gbit/s at one transfer and about 1.0 to 1.4 Gbit/s once several transfers overlap. The profiling section is what those timeouts mean. They are not evidence that UDP dropped the article bytes.

## Where the time goes

`--diagnose` sums `Stopwatch` durations across overlapping transfers. Divide a sum by the measured seconds to see how many transfers were in that section at once. `--store none` keeps the TFTP exchange and one SHA-256, and skips the file. It is not a substitute for the file result. Packet counts on a healthy run match the RFC minimum: a 768000-byte article at block 1428 is 538 DATA datagrams and 34 ACKs per window of 16. The sender's average burst was 15.82 blocks, which is 538/34. The window is filled. The transfer does not wait for an ACK after every block.

Same process, loopback, 5 seconds, block 1428, window 16, diagnostics on:

| Mode | Concurrency | Payload Gbit/s | Articles/s | DATA sent | DATA retransmitted | Server DATA received | Duplicates | Handshake timeouts |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| file | 1 | 0.487 | 79.2 | 213586 | 0 | 213586 | 0 | 0 |
| memory | 1 | 1.410 | 229.5 | 617624 | 0 | 617624 | 0 | 0 |
| file | 16 | 1.082 | 176.1 | 482586 | 0 | 482586 | 0 | 0 |
| memory | 16 | 8.310 | 1352.5 | 3643874 | 0 | 3643874 | 0 | 0 |
| file, block 8192 | 16 | 1.274 | 207.4 | 99452 | 0 | 99452 | 0 | 0 |
| memory, block 8192 | 16 | 19.726 | 3210.7 | 1517442 | 0 | 1517442 | 0 | 0 |
| file | 64 | 1.038 | 169.0 | 496036 | 0 | 496036 | 0 | not split in that run |

On the concurrency-1 file run the summed sections over 5.011 seconds were: socket receive 4779 ms, ACK wait 3671 ms, verify 2912 ms, socket send 1407 ms, file write 244 ms, hash 175 ms, timer setup 63 ms, flush 57 ms. Verify is the second SHA-256 of the temp file, the rename, and the delete, and it happens before the final ACK. Commit time was 2972 ms, almost the same number. At concurrency 16 the file run kept about 7.7 transfers inside verify (`39285 / 5095`) and the send fraction of a window cycle fell to 0.09. The memory run at the same concurrency used 67.7% of the machine and put the time in socket send (56933 ms) and socket receive (76380 ms). Timer setup was 4734 ms, a small slice of the socket time. Hash was 3704 ms. File write disappeared.

So the 1.0–1.2 Gbit/s file number is the per-article verification sitting on the final ACK. The same engine, still hashing once, with the body discarded, reaches 8.3 Gbit/s at block 1428 and 19.7 Gbit/s at block 8192. The 8192 result is loopback only. That datagram does not fit in a 1500-byte Ethernet frame.

An asynchronous reread was tried so the verify would release its thread during the file read. Concurrency 1 stayed at 0.484 Gbit/s and concurrency 16 stayed at 1.082 Gbit/s. The bytes are still in the cache, so the read does not yield, and the final ACK still waits for the hash. That change is kept because a cold file can yield. It is not a throughput win.

Concurrency 64 on the file path, after that change: 886 successes, 0 failures, 649 timeouts of which 647 were handshake timeouts, 1076 retransmitted DATA datagrams, and 1056 duplicate DATA datagrams at the server. Every first-transmission DATA datagram was accepted (`476668` sent and received). The retransmitted copies arrived. They were not dropped. A separate-process run of the same shape had 760 handshake timeouts and zero DATA retransmissions, with DATA sent equal to the minimum. The 250 ms timer is firing while the peer is busy starting the next transfer. It is not a broken window and it is not silent UDP loss. More concurrency does not raise throughput. This prototype does not add congestion control.

Separate processes on `127.0.0.1:6971`. The server command writes the file, hashes once, renames, and keeps a `.sha256` sidecar. It does not reread the file, and the client cannot see that hash, so `integrity` is `not_verified_external_server`. One kept file from seed 1 was checked by hand: length 768000, SHA-256 `28fea82d564e69f7e9376919a8e9b718bf7c9c04c9dc7911d7f4164325ac7a01`, matching the sidecar and the client payload. CPU below is the client process only.

| Concurrency | Block | Window | Payload Gbit/s | Articles/s | DATA retransmitted | Handshake timeouts | Client CPU % of machine |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1428 | 16 | 0.923 | 150.3 | 0 | 0 | 3.4 |
| 16 | 1428 | 16 | 0.990 | 161.1 | 0 | 0 | 4.9 |
| 16 | 8192 | 16 | 3.265 | 531.5 | 0 | 0 | 3.6 |
| 64 | 1428 | 16 | 0.864 | 140.7 | 0 | 760 | 4.7 |

No second machine was available, so there is no real-network row. Same-process, separate-process, and a future LAN run are different measurements.

Payload-only article rates, assuming every payload bit is useful and headers, hash, and disk take nothing: 768000 bytes is 6,144,000 bits, so 1 Gbit/s is 163 articles/s, 10 Gbit/s is 1,628 articles/s, and 30 Gbit/s is 4,883 articles/s. The verified same-process file runs are about 80 to 210 articles/s. The memory run at block 1428 and concurrency 16 is 1,352 articles/s, under the 10 Gbit/s line, at 68% of this machine. The memory run at block 8192 is 3,211 articles/s and is not an Ethernet-safe configuration.

## Comparison with VATP

VATP was not modified and was not run. Pointing this tool at a production VATP port would not speak VATP.

A same-sized article can be offered to a running StorageServer with the existing smoke client. That client counts VATP queue admission. It does not hash a stored file, and this TFTP run did not do that admission.

```powershell
dotnet run -c Release --project tools/storage-smoke -- smoke --body-bytes 768000 --sessions 4 --results <dir-outside-spool-and-database>
```

The differences that block treating the two numbers as one comparison:

- TFTP here is loopback UDP with both peers in one process. VATP smoke is TLS to a live StorageServer.
- TFTP success is a verified temp file. VATP `accepted` is an in-memory queue admission, not spool publication.
- TFTP has no article metadata, Message-ID, or spool index. VATP carries them.
- TFTP has no congestion control. The concurrency-64 row is the evidence. VATP runs over TCP.

The production files whose names or placement path are VATP are about 7600 lines across Common, NNTPD, StorageServer, and BackFiller. This prototype, including the benchmark and CLI, is about 3500 lines and depends only on the BCL. The line counts are not the same kind of work. VATP's extra surface is TLS, metadata, flow control, and the storage admission path. Those are the pieces a TFTP deployment would still have to grow.

## Assessment

The 1.0–1.2 Gbit/s ceiling of the file benchmark is the benchmark's own per-article file check, done before the final ACK. It is not a full window, not a failure to retransmit, and not UDP dropping the payload. With that check removed, the same RFC exchange reaches 8.3 Gbit/s on loopback at block 1428, and the separate-process server that writes each file once reaches 3.3 Gbit/s only when the block is 8192. Neither file result is a 10 Gbit/s article path, and the 8192-byte block is not safe to treat as an Ethernet result.

TFTP remains the smaller protocol. It is not the thing to grow until it behaves like a storage service. A minimal TCP transfer would be the next prototype if the question is whether a simple stream can sit near line rate without a userspace window and a per-datagram timer. Do not wire this tool into BackFiller, NNTPD, or StorageServer.
