# VectorNNTP.NNTPD — Phase 0 / 0.1 / 0.2 / 0.3 Architecture

## Goals

Phase 0 establishes a production-shaped host for a long-running NNTP server without implementing the protocol or data plane. Phase 0.1 completes Linux systemd notify/watchdog integration. Phase 0.2 makes Serilog the exclusive logging pipeline. Phase 0.3 removes obsolete Microsoft console formatter configuration left by `AddSystemd()`. Lifecycle, systemd, and logging coordination stay outside future packet-processing paths.

## Component responsibilities

```text
┌─────────────────────────────────────────────────────────────┐
│ Program.cs (composition root)                               │
│  - Bootstrap Serilog, then build HostApplicationBuilder     │
│  - Configure Serilog-only logging                           │
│  - Configure platform hosting (Windows Service / systemd)   │
│  - Register DI + options                                    │
│  - Run host; CloseAndFlush on exit                          │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ SystemdLifecycleNotifier / SystemdWatchdogService           │
│  - STATUS / READY / STOPPING from lifecycle transitions     │
│  - Optional WATCHDOG keep-alives while healthy              │
└───────────────┬─────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────┐
│ NntpdHostedService (IHostedService / BackgroundService)     │
│  - StartAsync → ApplicationLifecycle.StartAsync             │
│  - ExecuteAsync → wait for shutdown / unexpected fault      │
│  - StopAsync → NntpdHostLifetime.RequestShutdownAsync (once) │
└───────────────┬─────────────────────────────┬───────────────┘
                │                             │
                ▼                             ▼
┌───────────────────────────┐   ┌─────────────────────────────┐
│ ApplicationLifecycle      │   │ NntpdHostLifetime             │
│  - Validated state machine│   │  - Single-flight shutdown   │
│  - StateChanged events    │   │  - Unexpected-term → host   │
│  - Start / stop semantics │   │    StopApplication()        │
└─────────────┬─────────────┘   └─────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────┐
│ ApplicationServiceManager                                   │
│  - Deterministic start order / reverse stop order           │
│  - Rollback on startup failure                              │
│  - Bounded shutdown timeout                                 │
│  - Optional Execution monitoring                            │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ IApplicationService implementations                         │
│  - CloudflareDnsReconciliationService (bind resolve + DNS)  │
│  - NntpPlainListenerService (cleartext TCP accept/transport)│
│  - AcmeCertificateService (TLS/ACME when BindPortTls > 0)   │
│  - NntpTlsListenerService (implicit TLS accept/transport)   │
│  - Optional: PlaceholderApplicationService (tests only)     │
│  - RedisService (shared ConnectionMultiplexer)              │
│  - HistoryWriteService (async HistoryDB Redis persistence)  │
│  - HistoryMaintenanceService (local HistoryDB expiry)       │
│  - NNTP session: greeting, command dispatch, authz gates    │
│  - Later: full command handlers / storage / feeds             │
└─────────────────────────────────────────────────────────────┘
```

Accepted connections establish an immutable `ConnectionClientIdentity` (TCP peer + effective client endpoint). When `ProxyHosts` is non-empty and the TCP peer is trusted, HAProxy PROXY v1/v2 is required on the cleartext socket before TLS/NNTP. Untrusted peers keep TCP identity; PROXY-looking bytes are left as application input and never rewrite client identity (intentional mixed-mode policy; exclusive PROXY ports remain a deployment/firewall choice). `NntpSession` exposes the effective client IP/port without re-parsing the transport.

### NNTP session foundation

`NntpSession` owns the NNTP greeting and command loop over `INntpConnection` pipes. Transport remains responsible for sockets, PROXY, TLS, DEFLATE, and connection lifecycle.

Session concepts (distinct):

- **Mode:** `Unspecified` | `Reader` | `Stream` (`MODE READER`; `MODE STREAM` is RFC 4644 legacy discovery and does **not** change mode)
- **Authentication:** `NntpAuthenticationState` (identity after successful AUTHINFO); pending `AUTHINFO USER` username is separate and does not authenticate
- **Authorization:** immutable `NntpAuthorization` (`IsAuthenticated`, `AuthorizedReader`, `AuthorizedTransit`, `PostingPermitted`, `StreamingPermitted`, plus optional `TransitPeerName` / `TransitPeerPolicy`). Defaults: unauthenticated; streaming and posting denied. Connection-time identification uses the top-level `Transit` dictionary (identifiers as keys; `PeerName` is display-only). The effective client IP is matched against that peer's `AllowFrom` ACL (literal IPs/CIDRs plus currently resolved DNS addresses). A unique match grants `AuthorizedTransit` + `StreamingPermitted` without authentication and retains the named peer policy (credentials, Patterns, limits, `DeferOnDuplicate`, TLS mode). This is peer privilege, not user identity. `MODE STREAM` requires `StreamingPermitted`; `IHAVE`/`CHECK`/`TAKETHIS` require `AuthorizedTransit` (not AUTHINFO). Authentication success applies **only** privileges returned by `INntpAuthenticationProvider` — it does not imply reader/transit/posting/streaming.
- **Dispatch:** `NntpCommandParser` classifies and validates syntax on **bytes** and produces a validated `NntpCommand`; invalid syntax is rejected immediately. `NntpCommandDispatcher` then applies authentication → authorization → mode → enum/switch handler. Protocol representation is defined in [Byte-Oriented Protocol Data Plane](#byte-oriented-protocol-data-plane).

Public/pre-auth commands: `CAPABILITIES`, `MODE READER`, `HELP`, `DATE`, `QUIT`, `STARTTLS`, `COMPRESS DEFLATE`. **AUTHINFO USER/PASS** are implemented (RFC 4643). **TAKETHIS** (RFC 4644) is implemented for transit-authorized sessions (peer ACL or authenticated transit): multiline article receive → byte-budgeted in-memory ingestion queue → background `IncomingSpoolWriterService` → `spool/incoming`. `239` means accepted into the ingestion pipeline (not disk persistence). CAPABILITIES advertises `STREAMING`. `MODE STREAM` requires `StreamingPermitted` and returns `203` without changing session state. Command implementations live in dedicated files under `Session/Commands/` (see `docs/commands.md`). Cleartext AUTHINFO is a **server policy** (`Nntpd:AllowCleartextAuth`, default `true`): TLS inactive + policy false → `483` and CAPABILITIES omits `AUTHINFO USER`. TLS connections always permit AUTHINFO USER/PASS. Default DI registration is `DenyAllNntpAuthenticationProvider` (rejects all credentials). `AUTHINFO SASL`, reader/article/posting remain registered placeholders (`500` / `501` after authz gates). `IHAVE` (RFC 3977 §6.3.2) is implemented as a serial transit ingest proof of concept. `CHECK` uses HistoryDB (local memory then Redis). `COMPRESS DEFLATE` is implemented (RFC 8054): advertised until active; after activation AUTHINFO/STARTTLS/MODE READER are rejected with `502` and `COMPRESS` is no longer advertised. **SPEEDTEST** is a VectorNNTP private diagnostic extension (advertised as `SPEEDTEST`); see [SPEEDTEST diagnostic](#speedtest-diagnostic).

### Article ingestion (TAKETHIS)

```text
ONE session RX owner (sole Connection.Input PipeReader consumer)
TAKETHIS message-id
   │
   ├── HistoryDB PeekAsync (starts at command line; no miss reservation)
   │
   └── IHaveArticleReader on the RX task
           frame CRLF.CRLF; one owned stuffed-wire buffer; terminator omitted
           OwnedWireBuffer.Take() — Pipe memory is not retained
           attach owned buffer to TakeThisPipeline slot
           RETURN TO RX LOOP (do not wait for Peek / enqueue / 239)
                   │
                   ↓
            next TAKETHIS command / article (bounded by Depth)

Detached slot (off the RX stack; at most Depth in flight)
    await HistoryDB Peek if still pending
           │
           ↓
    enqueue InboundArticle (Producer = TakeThis) or 439 / 400
           │
           ↓
    Remember on miss
           │
           ↓
    239/439 in command order (immediate TX flush, no TAKETHIS coalesce batch)
           │
           ↓
    IncomingSpoolWriterService (one production consumer)
```

STREAM TAKETHIS is pipelined per session with a bounded window (`TakeThisPipeline.Depth` = 16). One physical RX task owns `Connection.Input`: it parses commands in order, frames each article, and detaches one owned stuffed-wire buffer per TAKETHIS. Pipeline workers never call `ReadAsync` on that pipe. After detach, Peek / queue / Remember / ordered 239 run independently; a fast Peek must not emit 239 on the RX task. Depth bounds how many owned article buffers may be retained; when the window is full the RX loop stops admitting and the input Pipe (64 KiB pause) applies TCP backpressure. 239 is never emitted before the complete article and the HistoryDB result, and never reordered. TAKETHIS does not destuff; MODE READER fallback still destuffs. Disk I/O is never on the TAKETHIS receive critical path.

The ingestion queue is multi-reader (`DequeueAsync` is safe for concurrent callers). Production drains it with one `IncomingSpoolWriterService` consumer.

### IHAVE article ingestion

IHAVE is **not pipelined** (RFC 3977 §6.3.2). TAKETHIS receive/framing/performance is **not** changed by this path.

```text
Socket
  ↓
ConnectionByteTransport
  ↓
input Pipe
  ↓
IHAVE command (HistoryDB PeekAsync)
  ↓
non-blocking Transit queue probe (no size reservation)
  ↓
335 / 435 / 436
  ↓
raw article reader (CRLF . CRLF framing)
  ↓
one owned NNTP wire buffer (dot-stuffing preserved; terminator omitted)
  ↓
non-blocking TryAdmit (never waits for queue memory)
  ↓
235 / 436 / 437
  ↓
IncomingSpoolWriterService
  ↓
IhaveArticleInterpreter (destuff exactly once → Article)
```

- **Queue payload (IHAVE):** complete NNTP wire-format article bytes. Leading-dot stuffing is preserved. The terminating `CRLF . CRLF` is not stored. One owned buffer; no Pipe or pooled memory.
- **Worker:** `IhaveArticleInterpreter` destuffs IHAVE items exactly once and builds `Article` (`Headers`, `Body`, `Size`, `ArticleType`). TAKETHIS items are not destuffed here.
- **Article.Headers / Body:** destuffed owned copies produced **after** queue admission. Not Pipe spans.
- **Article.Size:** destuffed complete article (headers + blank line + body). Terminator excluded. Same meaning as `MaxArticleBytes` (“after dot-unstuffing”).
- **Body representation:** destuffed received bytes. yEnc/BASE64/uuencode are **not** decoded.
- **HistoryDB:** IHAVE uses `PeekAsync` (no miss reservation) then `Remember` after a successful enqueue. CHECK still uses `LookupAsync`.
- **Queue:** TAKETHIS still constructs `InboundArticle` with `Producer = TakeThis` and may wait for byte-budget capacity. IHAVE sets `Producer = IHave` and does not set `Structured` at enqueue. IHAVE uses `TransitQueueMemoryLimit` as **non-blocking** backpressure: it probes remaining budget before `335` (no MaxSize reservation) and `TryAdmit`s after receive. Temporary inability to accept is `436`. IHAVE never waits for queue memory.

AUTHINFO flow:

```text
AUTHINFO USER username
    → pending username (not authenticated)
AUTHINFO PASS password
    → INntpAuthenticationProvider.AuthenticateAsync
    → on success: NntpAuthenticationState + NntpAuthorization (provider-granted flags only)
```

After successful authentication: AUTHINFO commands return `502`; CAPABILITIES omits `AUTHINFO` and `MODE-READER`. Passwords are never logged (dispatcher logs registry keys only).

Invalid lines: empty/malformed → `501`; unknown verb → `500`; known verb/unknown variant → `501`.

### SPEEDTEST diagnostic

`SPEEDTEST <identifier>` is a VectorNNTP private NNTP extension for Transit-peer diagnostics. It is **not** an RFC command and is **not** part of the article data plane.

- **Argument:** one NNTP token. It is the configured Transit **identifier** (dictionary key, exact ordinal match). It is not `PeerName`, a hostname, IP, port, URL, or socket endpoint. Multi-token display names (`SPEEDTEST Usenet Ninja`) are syntax errors.
- **Authorization:** `RequiresTransit`. A session identified as a named Transit peer may only name that peer's identifier. Unknown identifiers return `502 UNKNOWN SPEEDTEST PEER`. Arbitrary hosts/IPs are looked up as identifiers and fail the same way.
- **Capability:** `SPEEDTEST` is advertised in `CAPABILITIES`. HELP lists `SPEEDTEST <peer>` (`<peer>` is the Transit identifier).
- **What V1 measures:** TX on the **current** NNTP session (this host writes a synthetic multiline payload to the connected client). Direction is reported as `TX`. RX, control RTT, and packet loss are `NOT-MEASURED`.
- **What V1 does not do:** outbound connect to `ConnectTo`, article ingestion, HistoryDB, Redis, UDP/ICMP/iperf3, or arbitrary-destination testing. Outbound Transit initiation is not implemented; results include `OUTBOUND=NOT-AVAILABLE` rather than a fabricated remote measurement.
- **Payload:** immortal reusable 64 KiB chunk of `#` + digits + CRLF lines. No line begins with `.` (no dot-stuffing). The complete payload is never allocated as one buffer.
- **Limits** (`Nntpd:SpeedTest`): duration, bytes, host concurrency, per-peer concurrency. Cancellation, client disconnect, and shutdown release the concurrency slot and do not emit `291` with fake totals.
- **Responses (private `x9x`, RFC 3977 §3.2):** `290 SPEEDTEST <identifier> TX` then payload then `.`; `291 SPEEDTEST COMPLETE` then `PEER=` (identifier) / `PEERNAME=` (display name) / `DIRECTION=TX` / `BYTES=` / `DURATION_MS=` / `THROUGHPUT_MBPS=` / `THROUGHPUT_GBIT=` / `RTT_US=NOT-MEASURED` / `RX=NOT-MEASURED` / `OUTBOUND=NOT-AVAILABLE` / `COMPLETE` then `.`. Throughput uses SI megabit/gigabit (`bytes * 8 / seconds`). `200`/`226` are not reused (incompatible RFC meanings). Temporary overload is `400 SPEEDTEST BUSY`. A non-VectorNNTP peer that lacks the command returns ordinary `500`.
- **Pipelining:** serial session command; not pipelined with CHECK/TAKETHIS.

### Transport I/O ownership

`NntpConnection` keeps stable application `Input`/`Output` pipes for the connection lifetime. Receive/send pumps talk only to an internal `ConnectionByteTransport`, which owns the current byte stream:

- **Plain:** `Socket` → `NetworkStream(ownsSocket: false)` → transport → pumps → pipes
- **TLS:** `Socket` → `NetworkStream` → `SslStream` → transport → pumps → pipes
- **DEFLATE:** `Socket` → (`SslStream`?) → `NntpDeflateStream` → transport → pumps → pipes

Pumps never call `Socket`/`SslStream` APIs directly. Exactly one stream implementation owns socket I/O at a time. When DEFLATE is active, that owner is a duplex raw-DEFLATE transform over the plain or TLS stream.

### Transport TLS modes

Connections become TLS-protected in one of two ways, sharing the same server authentication options (`ClientCertificateRequired = false`) and certificate-lease lifetime:

1. **Implicit TLS** (`NntpTlsListenerService`): accept → PROXY (when trusted) → `SslStream` authenticate-as-server → TLS transport → pumps.
2. **In-place upgrade** (`INntpConnection.UpgradeToTlsAsync`): accept → PROXY (when trusted) → plain transport + pumps → later upgrade on the **same** TCP socket.

Upgrade sequence (STARTTLS): **pause application reads** (writes still admitted) so ClientHello cannot enter application `Input` → write and flush the `382` response → wait until the send pump is idle (`382` on the wire) → **quiesce writes** → verify application `Input` is empty (true NNTP leftovers only) → acquire certificate lease → `AuthenticateAsServerAsync` on `SslStream` (optional `PrefixedStream` for octets retained if a socket read completed during pause) → publish TLS and resume pumps. `ClientIdentity` is unchanged. Concurrent upgrade / already-TLS / closed-connection calls fail deterministically.

Upgrade failure semantics are split:

- **Precondition refusal** (for example unconsumed plaintext still in application `Input`): upgrade is rejected, the connection remains valid plaintext, no TLS transport is published, and no socket bytes are consumed by TLS.
- **Post-quiescence / TLS failure** (authentication failure, cancellation after quiescence begins, or teardown during handshake): the connection becomes terminal with no plaintext fallback; certificate lease and `SslStream` are disposed.

NNTPD performs **server-side TLS authentication only**. Client certificates are not requested, required, or validated.

The transport exposes TCP→TLS upgrade capability. The NNTP `STARTTLS` command (session layer) writes `382`, discards pipelined plaintext per RFC 8143, then invokes `UpgradeToTlsAsync`.

### Transport DEFLATE compression

The transport supports bidirectional **raw DEFLATE** (RFC 8054 §4 / RFC 1951) as a connection-layer capability via `INntpConnection.UpgradeToDeflateAsync`. The NNTP `COMPRESS` command (`Session/Commands/Compress.cs`) negotiates activation: pause reads → write `206 Compression active` → wait for outbound delivery → `UpgradeToDeflateAsync`.

Stack when both TLS and DEFLATE are active (RFC 8054 layering; TLS negotiated first):

```text
Application (NNTP session)
    ↓
PipeReader / PipeWriter
    ↓
Transport pumps
    ↓
ConnectionByteTransport
    ↓
NntpDeflateStream (raw DEFLATE; when active)
    ↓
SslStream (when TLS active)
    ↓
NetworkStream(ownsSocket: false)
    ↓
Socket
```

PROXY processing remains outside this stack and runs once at accept time. `ConnectionClientIdentity` is unchanged by DEFLATE activation.

Wire format notes:

- Uses `System.IO.Compression.DeflateStream` (raw DEFLATE; negative zlib `windowBits` equivalent).
- Does **not** use zlib wrappers (RFC 1950 / `ZLibStream`) or gzip (`GZipStream`).
- Independent compressor and decompressor state per connection; state is not shared across connections.
- Send-pump `FlushAsync` after each outbound pipe batch forwards to `DeflateStream.FlushAsync`. On .NET 10 this implementation uses DEFLATE sync-flush semantics (compressed bytes become visible to the peer; sliding dictionary retained). Finalization occurs on stream dispose.

Activation reuses the same quiescence model as TLS upgrade (atomic admission + outstanding counts). Preconditions mirror TLS: drain application `Input`; flush any bytes that must remain uncompressed (the COMPRESS `206` response) before calling `UpgradeToDeflateAsync`. Post-quiescence failure is terminal with no uncompressed fallback. DEFLATE after TLS is supported; TLS after DEFLATE is rejected. TLS-level compression is not used.

## Byte-Oriented Protocol Data Plane

**Protocol data remains byte-oriented throughout the data plane.**

NNTP is a byte-oriented wire protocol. A `string` is an application representation. Protocol data must not become a `string` by default, and must not make an unnecessary round trip such as `bytes → string → bytes` or `bytes → string → formatted string → bytes`.

This is an **architectural representation rule** first. It is also a performance rule (fewer allocations, encodings, copies, and temporary objects). Measurements support decisions; they are not the reason protocol data is bytes.

This rule does **not** forbid strings. Strings are permitted at explicit, justified boundaries. Protocol data does not become strings for convenience.

### Both directions

```text
RX:  socket → bytes → parser → validated protocol representation → handler
TX:  application/protocol representation → wire bytes → transport
```

Incoming bytes are not converted to strings unless an explicit application, API, or logging boundary requires it. Static outgoing protocol text is pre-encoded once and reused. Dynamic outgoing protocol fields remain bytes where practical. Do not construct a string merely to encode it back to bytes.

### What stays bytes

Protocol data stays bytes while it is still protocol data. Typical examples include command verbs and parse-time arguments, Message-ID wire octets, status/reply fields, static and dynamic response text, capability tokens, keywords, protocol literals, and article wire framing.

### Legitimate string boundaries

A conversion is acceptable when a genuine boundary requires a string, for example:

- an application or framework API that actually requires `string`
- an authentication/provider API that requires `string`
- human-readable logging (see [Source-Generated Structured Logging](#source-generated-structured-logging))
- configuration that is inherently textual
- persistent domain data that is intentionally a `string`

Convert **at that boundary**. Do not propagate the string back through the protocol data plane. Current legitimate examples include AUTHINFO credentials at the authentication provider, `InboundArticle.MessageId` at the ingest API, Serilog / `ILogger` message text, and `NntpdOptions` text. Those examples illustrate the boundary idea; they are not a closed allow-list.

### RX

```text
intended:  Socket → bytes → parser → NntpCommand → dispatch → handler
forbidden: bytes → string → Split → uppercase string → dictionary lookup
```

The parser classifies and validates syntax using bytes. A string may be created **after** the protocol boundary if the application layer genuinely requires one.

### TX

```text
intended:  application semantics → protocol representation → wire bytes → transport
avoid:     string construction → ASCII encoding → byte[]
           when the source is already bytes
```

Static replies are immutable pre-encoded wire buffers. Dynamic fields are composed from pre-encoded prefixes/suffixes plus already-byte protocol data (for example Message-ID octets or a formatted DATE stamp written directly into a wire buffer). HELP, CAPABILITIES, CHECK, TAKETHIS, and DATE illustrate this direction; the rule is not tied to those handlers.

### Hot-path justification

**Any bytes↔string conversion in a hot path requires an explicit justification.**

The justification must answer:

- Why is a string required?
- What API or boundary requires it?
- Why can the operation not remain byte-oriented?
- Is the conversion once, or per command/message?
- Is it on a high-frequency path?
- Does the string immediately become bytes again?

“Because the API currently takes a string” is **not** automatically sufficient. Determine whether that API is itself an unnecessary protocol-layer boundary.

A future reviewer must be able to see why a conversion exists. Silent `byte[] → string → byte[]` is a defect in representation, not a style preference.

### Reviewer checklist

When reviewing protocol or data-plane code:

- [ ] Does protocol data remain bytes?
- [ ] Is there a bytes→string conversion?
- [ ] If yes, what explicit boundary requires it?
- [ ] Is the conversion on a hot path?
- [ ] Does the string immediately become bytes again?
- [ ] Is static protocol text pre-encoded?
- [ ] Are dynamic protocol fields kept byte-oriented where practical?
- [ ] Is there an unnecessary allocation caused by representation conversion?
- [ ] Is the conversion documented/obvious enough that a future reviewer understands why it exists?

## Cloudflare DNS reconciliation

Startup order places `CloudflareDnsReconciliationService` first among application services. It resolves eligible bind addresses (including intentional private IPs), reconciles A/AAAA for the generated FQDN via the Cloudflare DNS API, verifies the remote set, and fails startup on any hard error (empty address set, API failure, verification mismatch, cancellation). Configuration validation still never calls Cloudflare.

Next, `NntpPlainListenerService` binds cleartext NNTP listeners (`BindAddress`/`BindPort`) and accepts connections into duplex `PipeReader`/`PipeWriter` transports. It does **not** wait for ACME.

When `BindPortTls > 0`, `AcmeCertificateService` runs next: it ensures the ACME account and a usable TLS certificate (DNS-01 via Cloudflare), then publishes an immutable `SslStreamCertificateContext` for new handshakes. When `BindPortTls` is `0`, that service is idle and performs no ACME work. `NntpTlsListenerService` then binds the implicit-TLS port only if a certificate context is available. Certificate rotation atomically replaces the published context without rebinding listeners or dropping existing TLS sessions.

On shutdown (reverse service order), TLS then plain listeners stop accepting and complete active transports before ACME/DNS cleanup. After other application services stop, Cloudflare DNS cleanup removes **every** DNS record for the exact FQDN (all types), verifies none remain in the Cloudflare API view, and reports failure if cleanup cannot be verified before the graceful-shutdown budget expires. Parent/child/other hostnames are never deleted. The host is authoritative for that exact name only. ACME does not contact Let's Encrypt during shutdown.

Cloudflare multi-record updates are **not atomic**. Each reconcile attempt lists both families, creates all missing A/AAAA records before any deletes, updates kept desired records to managed attributes (`ttl=300`, `proxied=false`), deletes stale/duplicates, then verifies exact content sets plus managed TTL and DNS-only proxy state. Address changes keep create-before-delete staging. Cleanup lists all types for the exact name, deletes by id, then verifies emptiness. The application fails closed on partial/uncertain outcomes and recovers by re-reading remote state on the next attempt (up to 3 attempts with backoff) inside a shared `CloudFlareOperationTimeout` (default 2 minutes) linked with the caller token; HTTP attempts are additionally capped by `min(30s, remaining budget)`. Failed-start cleanup is best-effort and capped at 15 seconds. Same-instance concurrent reconcile/cleanup calls are serialized; cross-process and external DNS managers are not coordinated. Intermediate supersets or missing families may be briefly visible externally. Forced termination may prevent cleanup.

API client and reconciler types live under `Cloudflare/`; bind expansion under `Networking/`. Credentials and raw Cloudflare response bodies are never logged in exception messages.

**Limitation:** A/AAAA publication runs at startup; exact-FQDN removal runs at shutdown. NNTP listeners bind the configured `BindAddress`/`BindPort` (and TLS port when enabled). Recursive caches are not waited out. See [configuration.md](configuration.md).

## systemd integration

- `AddSystemd()` registers official `SystemdLifetime` / `ISystemdNotifier` only when systemd (or `NOTIFY_SOCKET`) is detected. Any Microsoft console formatter options it registers are stripped; Serilog owns console formatting.
- `ISystemdNotifyBridge` wraps the notifier for testability.
- `ISystemdRuntime` exposes Linux / service / notify / watchdog deadline detection.
- `IApplicationHealth` defines watchdog health as: `Running`, no unexpected termination, shutdown not requested.
- No custom SIGTERM/SIGINT handlers; the Generic Host / `SystemdLifetime` own signal handling.
- Watchdog and status notifications are not on the future NNTP data path.

## Dependencies

| Component | Depends on | Must not depend on |
|-----------|------------|--------------------|
| `Program` | Hosting extensions, DI | Lifecycle internals beyond registration |
| `NntpdHostedService` | `ApplicationLifecycle`, `NntpdHostLifetime`, options, logging | Concrete NNTP services |
| `NntpdHostLifetime` | `ApplicationLifecycle`, `IHostApplicationLifetime` | Service manager details |
| `ApplicationLifecycle` | `ApplicationServiceManager`, options, logging | Host types (keeps core testable) |
| `ApplicationServiceManager` | `IApplicationService`, options, logging | Host lifetime |
| `CloudflareDnsReconciliationService` | options, bind resolver, DNS reconciler | Host types |
| `ICloudflareDnsClient` / reconciler | options, `HttpClient` | Lifecycle coordinator |
| `SystemdLifecycleNotifier` | lifecycle, notify bridge, runtime, options | NNTP / sockets |
| `SystemdWatchdogService` | runtime, notify bridge, health, host lifetime | NNTP / sockets |
| `IApplicationService` | BCL + cancellation only | Host, lifecycle coordinator |

## Lifecycle semantics

### Happy path

`Created → Starting → Running → Stopping → Stopped`

### Startup failure / cancellation

1. `ApplicationServiceManager` stops already-started services in reverse order.
2. `ApplicationLifecycle` transitions `Starting → Stopping → Stopped`.
3. The original exception is rethrown so host startup fails closed.
4. No partially initialized services remain tracked as started.
5. systemd readiness is not reported.

### Shutdown

- Initiated exactly once through `NntpdHostLifetime`.
- Repeated stop callers on `ApplicationLifecycle` await the same shutdown task; concurrent stop/dispose is serialized there.
- `ApplicationServiceManager` rejects overlapping `StartAsync`/`StopAsync` calls (`InvalidOperationException`). Host paths rely on the lifecycle single-flight stop.
- `GracefulShutdownTimeout` is one overall wall-clock budget for the reverse-order stop sequence (also applied to Generic Host `ShutdownTimeout` when configured).
- The manager **awaits** each `IApplicationService.StopAsync` and does not abandon in-flight stops. Cooperative services observe the linked cancel/timeout token. A service that ignores cancellation keeps the manager awaiting until that call returns; process supervisors (host `ShutdownTimeout`, systemd `TimeoutStopSec`) are the kill backstop.
- Removal from `StartedServices` means the awaited stop attempt for that service finished (success, cancel/timeout, or exception)—not that the process has already been killed.
- Shutdown failures and timeouts are logged and rethrown; lifecycle state still settles on `Stopped` when possible. `Stopped` means the shutdown sequence finished (possibly with failures), not that every service cooperated within the budget.
- Watchdog keep-alives stop when shutdown begins.

### Unexpected termination

If `IApplicationService.Execution` faults or completes while `Running`, the manager raises `UnexpectedServiceTermination`. Health becomes unhealthy (watchdog stops). The hosted service requests host stop when `StopHostOnUnexpectedServiceTermination` is enabled, with `BackgroundServiceExceptionBehavior.StopHost`.

## Redis and HistoryDB

`RedisService` is generic infrastructure. It owns Redis configuration, one shared StackExchange.Redis `ConnectionMultiplexer`, startup connectivity (`Connect` + `PING`), and generic key operations. It does not contain HistoryDB or CHECK policy.

Startup order places `RedisService` after Cloudflare DNS reconciliation and before listeners. If the initial connection cannot be established, startup fails. The multiplexer is long-lived; consumers do not connect per request. Multiple `Redis:Host` entries are multiplexer endpoints/seeds for one topology, not independently round-robined HistoryDB servers.

`HistoryDB` is a Redis consumer:

```text
Message-ID bytes → BLAKE3 (32 bytes) → Redis key = "nntpd:hist:" || digest
```

The digest is not hex-encoded. Redis keys stay binary. Local memory and Redis both expire markers after `Nntpd:HistoryTime` (default two hours). Redis uses native key TTL.

Local HistoryDB is a hard-capped `ConcurrentDictionary` (1,048,576 entries). CHECK never walks the dictionary: hits are one-key lookups, inserts use an `Interlocked` counter, and expired entries are removed on that key's lookup or by `HistoryMaintenanceService` off the request path. When the cap is reached, new digests are not inserted (no false `438`); Redis remains authoritative until maintenance frees a slot.

CHECK lookup order (RFC 4644 §2.4):

1. Local HistoryDB hit → `438` immediately (no Redis).
2. Local miss, Redis hit → `438`; warm local memory. No Redis write.
3. Double miss → `238`; insert local immediately; enqueue a bounded background Redis `SET` with TTL. The CHECK response does not wait for Redis persistence.
4. Redis infrastructure failure (timeout, disconnect, error) → `431`. A Redis error is not a HistoryDB miss and must not become a false `238`. After a failure, `RedisService` enters a short cooldown: further CHECK misses return `431` without calling Redis until one recovery probe succeeds.

CHECK execution is a **per-session bounded pipeline** (`CheckPipeline.Depth`, architectural constant **16**, not configurable). Consecutive authorized CHECK commands may overlap Redis lookups so remote RTT is not paid serially. A slot is occupied from admission until the existing response writer accepts the line (`EnqueueLineAsync`); lookup completion alone does not free the slot. Every CHECK response passes through one emit gate and is enqueued **in send order**; the writer itself is FIFO-of-enqueue and does not reorder. When the window is full the session stops reading the next command so the input Pipe (64 KiB pause) applies TCP backpressure — including when Redis is fast and the client/TX is slow. CHECK commands are not dropped. Any non-CHECK command (including TAKETHIS, QUIT, STARTTLS, COMPRESS, AUTHINFO, MODE) drains outstanding CHECK responses first, then runs on the existing serial dispatcher. General NNTP command execution remains serial. HistoryDB and Redis remain process-wide and concurrency-safe; CHECK pipelining does not add a second multiplexer or per-request connection.

Background Redis writes use a bounded channel (`HistoryWriteQueue`, drop-on-full) drained by `HistoryWriteService`. A full queue logs a warning and drops the write; local history remains. Unrelated Message-IDs stay concurrent; there is no global CHECK lock.

## Logging

`ConfigureNntpdLogging()` clears MEL providers, removes the default `ILoggerFactory`, and registers Serilog via `AddSerilog` (`writeToProviders: false`). Framework and application logs share one Serilog pipeline. Details: [logging.md](logging.md). Application call sites follow [Source-Generated Structured Logging](#source-generated-structured-logging).

## Source-Generated Structured Logging

Application logging should use compile-time / source-generated logging where practical. Prefer `LoggerMessageAttribute` plus partial logging methods over repeated runtime template parsing such as `_logger.LogInformation("Connection accepted from {Remote}", remote)`.

Serilog remains the application's exclusive logging provider. Application and framework code continue to use `Microsoft.Extensions.Logging.ILogger` / `ILogger<T>` abstractions; those resolve to the Serilog pipeline (`ILogger` → `SerilogLoggerFactory` → configured sinks). Do not introduce Microsoft.Extensions.Logging as a second runtime provider, and do not replace Serilog.

Logging is an explicit application boundary, not a reason to change protocol representation. Protocol data stays bytes in the data plane ([Byte-Oriented Protocol Data Plane](#byte-oriented-protocol-data-plane)).

### Structured logging

Preserve structured properties. Do not turn structured logging into preformatted strings.

Avoid:

```csharp
_logger.LogInformation($"RX: {command}");
```

Prefer:

```csharp
CommandLogMessages.CommandRx(logger, client, command);
```

or, when a generated method is not appropriate, a structured template:

```csharp
_logger.LogInformation("RX: {Command}", command);
```

Do not preformat the message before passing it to structured logging.

### Logging representation

Protocol bytes should not be converted to strings solely because a log statement happens to need them if the logging mechanism can represent them without that conversion.

The current `LoggerMessageAttribute` / `ILogger` source-generation APIs do **not** accept `ReadOnlySpan<byte>` (ref struct) or provide a byte-native structured payload that Serilog would render as human-readable protocol text. `byte[]` / `ReadOnlyMemory<byte>` `ToString()` is not a useful operational representation.

When the logging API requires human-readable text, the conversion is a legitimate logging boundary and should happen **once, locally**, immediately before the generated log method. Do not convert protocol bytes earlier in the data plane merely to make logging convenient. Do not log raw byte arrays in a way that makes operational logs unreadable.

If a validated string already exists because an application API required it, reuse that string; do not create an additional conversion merely for logging.

### Hot path

Any bytes↔string conversion introduced solely for logging in a hot path requires an explicit justification that identifies:

- why logging needs the string
- why the logging API cannot consume the original representation
- whether the conversion occurs per command/message
- whether logging is enabled at that level in production
- whether the conversion can be moved outside the hot path or skipped when the level is disabled

Check `ILogger.IsEnabled` before constructing expensive logging arguments (for example redacted command text from protocol bytes).

### Event IDs

Generated methods use stable component-scoped EventId ranges. Do not mechanically invent new ranges for every call when an existing convention already applies. Unused reserved methods keep their original EventIds; do not rewrite operational message text to reuse a reserved unused template.

## Configuration

`NntpdOptions` binds from the `Nntpd` section, including nested `Systemd` options, listener bind settings, Cloudflare DNS settings, `HistoryTime`, and a generated FQDN (`nntpd{ServerId:00}.{DnsSuffix}`). Redis binds from the top-level `Redis` section.

Mandatory settings that fail startup when missing or invalid: `CloudFlareApiKey`, `CloudFlareZoneId`, `ServerId` (`1–99`, no default), and `Redis:Host`. Validation runs via `ValidateOnStart` / `IValidateOptions` before the host enters the running state. Validation does not bind sockets or call Cloudflare. After validation, `CloudflareDnsReconciliationService` reconciles and verifies A/AAAA for the generated FQDN against resolved bind addresses, then `RedisService` connects and PINGs; either failure prevents `Running`. Details: [configuration.md](configuration.md). Serilog is configured under the `Serilog` section.

## Testing strategy

- Offline tests live under `tests/VectorNNTP.NNTPD.Tests/`, grouped by production subsystem (`Cloudflare/`, `Core/`, `Hosting/Systemd/`, `Configuration/`, `Networking/`, `Logging/`), with shared `Fixtures/` and `TestDoubles/`.
- Unit tests drive lifecycle, service manager, systemd notifier, watchdog, and Serilog exclusivity with deterministic fakes and collecting sinks.
- Host integration tests use `Host.CreateApplicationBuilder` without network listeners.
- Platform-specific service managers are registered but do not require an actual Windows Service or systemd session for tests.
- Real systemd verification is documented separately and must not be claimed from unit tests alone.

## Non-goals (deferred)

NNTP article/group data plane, posting, AUTHINFO SASL, and account backends beyond `INntpAuthenticationProvider` remain deferred. AUTHINFO USER/PASS, COMPRESS DEFLATE (RFC 8054), TAKETHIS streaming ingestion (RFC 4644), CHECK HistoryDB (RFC 4644), IHAVE transit ingest (RFC 3977 §6.3.2; raw wire receive, destuff downstream), and the session authorization gates are in place.
