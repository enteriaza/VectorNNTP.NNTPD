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
│  - NntpDbService (lifecycle + SELECT 1; MySqlConnector pool)│
│  - NewsgroupCatalogueService (immutable snapshot + 5 min)   │
│  - ModeratorCatalogueService (nntpmoderators snapshot)      │
│  - HistoryWriteService (async HistoryDB Redis persistence)  │
│  - HistoryMaintenanceService (local HistoryDB expiry)       │
│  - IncomingSpoolWriterService (TAKETHIS/IHAVE/POST spool)   │
│  - EmailDeliveryService (generic outbound SMTP worker)      │
│  - SessionStateService (leases + IPs + bytes + rate shares) │
│  - TransitPeerStateService (Transit inbound ownership)      │
│  - NNTP session: greeting, command dispatch, authz gates    │
│  - Later: full command handlers / storage / feeds             │
└─────────────────────────────────────────────────────────────┘
```

NNTPD owns the database service and lifecycle (`NntpDbService` + mandatory startup `SELECT 1`). MySqlConnector owns physical connection pooling. There is no application-owned MySQL pool.

`ModeratorCatalogueService` starts immediately after `NewsgroupCatalogueService` and uses the same load/refresh contract against `nntpmoderators`. Initial load is required. Refresh builds a new snapshot off-side and publishes it with `Interlocked.Exchange`. POST captures `Current` once.

`NewsgroupCatalogueService` starts after `NntpDbService` and before NNTP listeners. Initial population of `nntpgroups` must succeed before the application reaches RUNNING. The published `NewsgroupSnapshot` is immutable and swapped atomically every five minutes. Command handlers capture `Current` once; LIST/GROUP/POST newsgroup validation never query MySQL. A failed refresh retains the last known-good snapshot. LIST ACTIVE posting status is the stored `y`/`n`/`m`/`x`/`j` octet; the RFC 6048 `=<newsgroup>` form is not supported. LIST COUNTS reuses the same snapshot and GROUP watermark estimate (`high − low + 1`, or `0` when `high < low`) as a precomputed five-field line. LISTGROUP is registered but unimplemented (`500`) and does not consult the catalogue. POST uses `CatalogueNewsgroupPostingPolicy` on the same snapshot: every `Newsgroups:` target must exist. Status `y` is ordinary local posting. Status `n`/`x`/`j` reject local POST. Status `m` is moderated: an unapproved proto-article is offered to `IModerationSubmissionService` (RFC 5537 §3.5.1, leftmost moderated group) and is not injected. The submission address is the first-match `nntpmoderators` route (`moderator_id ASC`; static mailbox or INN `%s` template: dots to dashes). An `Approved:` header is an assertion and is trusted only when the authenticated AUTHINFO principal matches `account_name` for every moderated target. Routing-only rows with an empty `account_name` do not authorize injection. `Control:PgpAuthorities` is not used for this decision. Unapproved forwarding calls `IModerationSubmissionService`, which composes an RFC 5537 `application/news-transmission; usage=moderate` email and submits it through the generic `IEmailService`. `240` means the complete message was durably written to the local filesystem spool (`spool/smtp`) for asynchronous SMTP delivery — not that a remote SMTP server or moderator mailbox received it. Undelivered spool files survive process restart. When `Email:Enabled` is `false` (the production default) or the spool write fails, POST returns `441`. POST never performs DNS, TCP, TLS, AUTH, or SMTP DATA.

Accepted connections establish an immutable `ConnectionClientIdentity` (TCP peer + effective client endpoint). When `ProxyHosts` is non-empty and the TCP peer is trusted, HAProxy PROXY v1/v2 is required on the cleartext socket before TLS/NNTP. Untrusted peers keep TCP identity; PROXY-looking bytes are left as application input and never rewrite client identity (intentional mixed-mode policy; exclusive PROXY ports remain a deployment/firewall choice). `NntpSession` exposes the effective client IP/port without re-parsing the transport.

### NNTP session foundation

`NntpSession` owns the NNTP greeting and command loop over `INntpConnection` pipes. Transport remains responsible for sockets, PROXY, TLS, DEFLATE, and connection lifecycle.

Session concepts (distinct):

- **Mode:** `Unspecified` | `Reader` | `Stream` (`MODE READER`; `MODE STREAM` is RFC 4644 legacy discovery and does **not** change mode)
- **Authentication:** `NntpAuthenticationState` (identity after successful AUTHINFO); pending `AUTHINFO USER` username is separate and does not authenticate
- **Authorization:** immutable `NntpAuthorization` (`IsAuthenticated`, `AuthorizedReader`, `AuthorizedTransit`, `PostingPermitted`, `StreamingPermitted`, plus optional `TransitPeerName` / `TransitPeerPolicy`). Defaults: unauthenticated; streaming and posting denied. Connection-time identification uses the top-level `Transit` dictionary (identifiers as keys; `PeerName` is display-only). The effective client IP is matched against that peer's `AllowFrom` ACL (literal IPs/CIDRs plus currently resolved DNS addresses). A unique match grants `AuthorizedTransit` + `StreamingPermitted` without authentication and retains the named peer policy (credentials, Patterns, limits, `DeferOnDuplicate`, TLS mode). This is peer privilege, not user identity. `MODE STREAM` requires `StreamingPermitted`; `IHAVE`/`CHECK`/`TAKETHIS` require `AuthorizedTransit` (not AUTHINFO). Authentication success applies **only** privileges returned by `INntpAuthenticationProvider` — it does not imply reader/transit/posting/streaming.
- **Dispatch:** `NntpCommandParser` classifies and validates syntax on **bytes** and produces a validated `NntpCommand`; invalid syntax is rejected immediately. `NntpCommandDispatcher` then applies authentication → authorization → mode → enum/switch handler. Protocol representation is defined in [Byte-Oriented Protocol Data Plane](#byte-oriented-protocol-data-plane).

Public/pre-auth commands: `CAPABILITIES`, `MODE READER`, `HELP`, `DATE`, `QUIT`, `STARTTLS`, `COMPRESS DEFLATE`. **AUTHINFO USER/PASS** and **AUTHINFO SASL** (PLAIN, LOGIN, CRAM-MD5, SCRAM-SHA-256) are implemented (RFC 4643 / RFC 5802). Reader identity comes only from a successful AUTHINFO/SASL exchange. Default DI uses `CompositeNntpAuthenticationProvider` (configured newsmaster, then MySQL `nntpusers` through existing NntpDB) plus `NntpSaslService` and `DistributedSessionStateTracker` in `VectorNNTP.NNTPD.SessionState` (cluster-wide authenticated-session ownership: `account_session_limit` and `account_srcip_limit`, evaluated atomically via Redis ownership leases). **TAKETHIS** (RFC 4644) is implemented for transit-authorized sessions (peer ACL or authenticated transit): multiline article receive → byte-budgeted in-memory ingestion queue → background `IncomingSpoolWriterService` → `spool/incoming`. `239` means accepted into the ingestion pipeline (not disk persistence). CAPABILITIES advertises `STREAMING`. `MODE STREAM` requires `StreamingPermitted` and returns `203` without changing session state. Command implementations live in dedicated files under `Session/Commands/`; command-processing infrastructure lives under `Session/CommandProcessor/` (see `docs/commands.md`). Cleartext AUTHINFO is a **server policy** (`Nntpd:AllowCleartextAuth`, default `true`): TLS inactive + policy false → `483` and CAPABILITIES omits `AUTHINFO USER` and SASL. TLS connections always permit AUTHINFO USER/PASS and SASL. `IHAVE` (RFC 3977 §6.3.2) is implemented as a serial transit ingest proof of concept. `CHECK` uses HistoryDB (local memory then Redis). `COMPRESS DEFLATE` is implemented (RFC 8054): advertised until active; after activation AUTHINFO/STARTTLS/MODE READER are rejected with `502` and `COMPRESS` is no longer advertised. **SPEEDTEST** is a VectorNNTP private diagnostic extension (advertised as `SPEEDTEST`); see [SPEEDTEST diagnostic](#speedtest-diagnostic). ARTICLE/HEAD/BODY/STAT remain registered placeholders.

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
- **HistoryDB:** CHECK, IHAVE, and TAKETHIS use `PeekAsync` (no miss reservation). IHAVE and TAKETHIS call `Remember` after a successful enqueue.
- **Queue:** TAKETHIS still constructs `InboundArticle` with `Producer = TakeThis` and may wait for byte-budget capacity. IHAVE sets `Producer = IHave` and does not set `Structured` at enqueue. IHAVE uses `TransitQueueMemoryLimit` as **non-blocking** backpressure: it probes remaining budget before `335` (no MaxSize reservation) and `TryAdmit`s after receive. Temporary inability to accept is `436`. IHAVE never waits for queue memory.

### Outbound Email service

`EmailService` is a generic outbound email subsystem. It has no dependency on newsgroups, NNTP commands, moderation, `Approved`, articles, or HistoryDB. Moderation is the first producer; later producers (alerts, account mail) call the same `IEmailService.SendAsync`.

```text
producer (e.g. EmailModerationSubmissionService)
  ↓
IEmailService.SendAsync          ← validate + MIME encode + atomic spool write
  ↓
filesystem spool (spool/smtp/*.eml)  ← the durable queue
  ↓
EmailDeliveryService worker      ← IApplicationService; scans disk
  ↓
ISmtpTransport (TcpClient / SslStream)
  ↓
remote SMTP (relay or submission)
  ↓
successful 2xx acceptance
  ↓
delete spool file
```

The filesystem spool is the durable outbound email queue. `SendAsync` means **the complete RFC 5322/MIME message (plus SMTP envelope) has been durably written to the local spool**. It does not mean the remote SMTP server accepted the message. SMTP delivery is asynchronous. Successful SMTP delivery deletes the spool file. Undelivered `.eml` files survive process restart, machine restart, and SMTP outage. SMTP acceptance followed by filesystem deletion is inherently at-least-once and cannot provide exactly-once delivery.

Writers publish atomically: write a complete `<md5(uuid)>.tmp`, flush, then rename to `<md5(uuid)>.eml`. The delivery scanner is a non-recursive top-level `spool/smtp/*.eml` listing. It does not recurse into `failed/` and does not treat `.tmp`, `.wrk`, or `.delivered` as pending work.

Spool states:

| File | Meaning | Automatic delivery |
|------|---------|--------------------|
| `.tmp` | Incomplete atomic write | Never |
| `.eml` | Pending outbound message | Yes (only this state) |
| `.wrk` | In-flight claim | No; crash recovery may rename back to `.eml` because SMTP may not have completed |
| `failed/*.eml` | Permanent SMTP/parse failure; retained for operators | Never; never moved back to the active spool |
| `.delivered` | SMTP already accepted the message, but local delete failed | **Never.** Retrying would create a possible duplicate. Crash/startup/shutdown recovery must not rename this to `.eml`. |

In-process claims are serialized so two delivery workers cannot process the same file. Startup recovers leftover `.wrk` files back to `.eml` unless this process still holds the claim. Permanent SMTP failures move the file to `spool/smtp/failed/`. If SMTP accepted the message but deleting the spool file fails, the file is renamed to `.delivered` and a critical log is written. `.delivered` is an operational cleanup-failure artifact, not pending mail, and is never automatically retransmitted.

In-process retry state (attempt count / backoff) is not stored on disk. After restart, pending `.eml` files are eligible for delivery again. The bounded SMTP retry policy still prevents hammering one server during a process lifetime.

Producers never see `TcpClient`, `SslStream`, SMTP commands, STARTTLS, AUTH, retries, spool paths, or connection reuse. POST never waits for DNS, TCP, TLS, AUTH, or DATA.

SMTP transport (`SmtpTransport` / `SmtpConnection`) uses native .NET networking only. Security mode is explicit (`None` / `StartTls` / `ImplicitTls`) and is never inferred from port. Required STARTTLS does not silently downgrade. Implicit TLS handshakes before the SMTP greeting. After STARTTLS the client issues a second EHLO. AUTH PLAIN (preferred) and AUTH LOGIN run only after TLS when `RequireTlsForAuthentication` is true (the default). Certificate validation is mandatory; there is no production option to accept invalid SMTP server certificates. EHLO/HELO uses the application FQDN (`nntpd{ServerId:00}.{DnsSuffix}`). Credentials are never logged.

Retries: 4xx / network failures retry with bounded exponential backoff and jitter. 5xx, AUTH, protocol, malformed-message, and partial-recipient failures do not retry; those files are retained under `failed/` for diagnosis. There is no dead-letter database.

Startup validates configuration, creates `spool/smtp` when email is enabled, and does not open SMTP. Shutdown stops new submissions, allows the current SMTP operation to finish within `Email:Spool:ShutdownTimeout`, then leaves remaining `.eml` files on disk.

AUTHINFO flow:

```text
AUTHINFO USER username
    → pending username (not authenticated)
AUTHINFO PASS password
    → authority from MODE, never from source IP (no Transit ↔ MySQL fallback)
    → MODE STREAM / Stream: Transit peer credentials only
    → MODE READER / unspecified: INntpAuthenticationProvider (newsmaster, then nntpusers)
    → SessionState admission (`account_session_limit` is the cluster-wide
      authenticated-session cap; `account_srcip_limit` is the cluster-wide
      distinct-source-address cap. Both are evaluated in one Redis EVAL. Two HASH keys
      `nntpd:srcip:{sha256hex(account)}` and `nntpd:sess:{sha256hex(account)}`.
      Source fields `{ip}<US>{ownerId}` and session fields `{ownerId}` store
      `{expiryMs}|{generation}|{count}`. `ownerId` is `{nodeId}:{incarnation}`
      so a process restart cannot mutate a previous incarnation's unexpired
      ownership. Session count is the sum of unexpired owner counts; source
      count is distinct unexpired IPs. Session-limit rejection takes precedence
      when both limits would fail. Limit 0 is unlimited. New admission that
      requires Redis fails closed (`503`). The source-IP local hot path is used
      only while `account_session_limit` is 0, `account_rate_limit` is 0, and
      this node holds a live source lease; a finite session limit or a positive
      rate requires every new session to take the distributed path so
      the global session count stays exact. Leases last 30s
      and renew every 10s; failed renewal does not extend local validity.
      Incarnation is a process-lifetime identifier created with the tracker
      singleton, not a configuration setting. Admit and release each use one
      EVAL. The 30s TTL is crash recovery, not a client-idle timeout: a healthy
      node renews every 10s for as long as local authenticated sessions exist,
      including completely idle clients. Normal teardown decrements immediately.
      Node failure is detected when renewals stop and the lease expires.
      Renewal is one EVAL per account and is all-or-nothing; it never recreates
      a lost lease.)
    → on success: NntpAuthenticationState + NntpAuthorization + NntpAccountPolicy
AUTHINFO SASL mechanism [initial-response]
    → NntpSaslService (PLAIN / LOGIN / CRAM-MD5 / SCRAM-SHA-256)
    → challenges: 383 + Base64 (RFC 4643; empty PLAIN is 383 =)
    → CRAM-MD5 native challenge is RFC 2195 <random.timestamp@Fqdn>; HMAC-MD5 is over those octets
    → success: 281, or 283 + Base64 when extra SASL data is required (SCRAM)
    → same identity, authorization, and admission as PASS
```

`VectorNNTP.NNTPD.SessionState` owns distributed authenticated-session state after AUTHINFO succeeds, remaining-byte quota in `SessionState.BytesAccounting`, and outbound rate shares in `SessionState.RateLimiting`. Every authenticated account has both remaining-byte and rate policies. `account_session_limit` is the cluster-wide cap on total authenticated sessions. `account_srcip_limit` is the cluster-wide cap on distinct active source addresses. `ISessionStateStore` persists that ownership (`RedisSessionStateStore` in production; in-memory for tests). `SourceAddressIdentity` is the normalized source-address identity used by SessionState (IPv4-mapped IPv6 is treated as IPv4; IPv4 and IPv6 remain distinct; there is no `/64` grouping). `SessionStateService` is the sole periodic scheduler: lease renewal, source-address ownership, and byte-quota reconciliation share one ~10-second cycle. There is no `AccountByteService`, `RateLimitService`, or second timer. Rate allocation rides the existing admit/release/renew session totals; it does not add a periodic EVAL. Authentication and other core components consume `ISessionStateLeaseManager`; SessionState does not depend on Authentication. It does not accept connections or keep its own session registry. Every 10 seconds it commits account-wide MySQL remaining-quota consumes, then asks the session-state tracker to renew currently active local ownership. Session and source-address ownership are maintained together. When an owned account also has a MySQL-committed byte batch, renewal and Redis APPLY share one EVAL. Accounts with ownership and no pending bytes use RENEW-only. Accounts with pending bytes and no SessionState ownership use APPLY-only. One Redis operation per account that needs work, not per session and not a second 10-second timer. The tracker is the source of truth for what this node owns. `NntpSession` owns connection/protocol lifecycle and calls admit/release; it does not schedule renewal.

Cluster admission leases prove **node liveness**, not client activity. The Redis TTL is only the crash/failure window. A healthy node renews ownership every 10 seconds for every account that still has local authenticated sessions, so those sessions keep consuming `account_session_limit` / `account_srcip_limit` even if the clients sit idle for hours. The TTL is not an idle-session timeout. Normal disconnect decrements that node's count immediately; process/node failure is detected when renewals stop and the lease expires. Renewal is one EVAL per account (session ownership plus every locally active source IP) and is all-or-nothing: it never recreates a lost lease.

Authenticated-session teardown is independent of `QUIT`. Every command-loop exit (QUIT, remote EOF, TCP reset, socket/IO/TLS failure, idle timeout, cancellation, listener or application stop) enters one idempotent finalization (`Running` → `Finalizing` → `Finalized`). Only the first transition may call the existing combined Redis RELEASE. Unauthenticated connections do not release. If Redis is down at teardown, the session is still finalized locally and dropped from renewal; the lease expires for cluster recovery. Renewal never snapshots a session that has already been released locally. Listeners stop before `SessionStateService`, so graceful shutdown finalizes sessions first; `ReleaseOwner` then clears leftovers. Abrupt crash still relies on TTL.

`VectorNNTP.NNTPD.SessionState.BytesAccounting` owns cluster-wide remaining-byte quota for every authenticated reader account. It is a bounded context inside SessionState, not a second application service. TransitPeerState remains a separate peer-connection context. `account_byte_limit` is **remaining** bytes: `> 0` means bytes remain, `0` means exhausted, and a negative value is never allowed. NULL remaining maps to `0`. There is no `0 = unlimited` sentinel; an effectively unlimited account is provisioned with a large positive remaining quota (for example 10 TB). Unauthenticated sessions, transit peers, and newsmaster/admin identities whose `AccountPolicy` is null do not participate. `account_type` is not used.

The accounting boundary is `NntpResponseWriter` after a successful `PipeWriter.Advance` of bytes actually copied into the output pipe. Counted bytes are logical uncompressed NNTP application bytes: status lines, CRLF, multiline framing, dot-stuffing, article payload, the terminating `.\r\n`, LIST/HELP, SPEEDTEST payloads, and any future ARTICLE/HEAD/BODY/OVER/XOVER/LISTGROUP/CHECK/TAKETHIS responses written through that writer. TLS, TCP/IP, Ethernet, DEFLATE wire bytes, `GetMemory` capacity that was never copied, and Channel items that never reached `Advance` are not counted. The hot path is `Interlocked.Add` only. Redis and MySQL are never touched per write, article, command, or response.

`VectorNNTP.NNTPD.SessionState.RateLimiting` owns cluster-wide outbound download-rate allocation for every authenticated reader account. It is a bounded context inside SessionState, not a second application service. `account_rate_limit` is the account-wide aggregate cap in **bits per second (bps)**. Examples: `240` = 240 bps = 30 B/s; `2,400` = 2,400 bps = 300 B/s; `1,000,000` = 1 Mbps = 125,000 B/s; `10,000,000` = 10 Mbps = 1,250,000 B/s. `0` and NULL are unlimited and do not participate in allocation. This is not the remaining-byte `0 = exhausted` rule. The denominator is the live cluster-wide authenticated session count from the SessionState session HASH — never `account_session_limit`. Conversion is integer `floor(bps / 8)` to account bytes/sec, then `floor(accountBytes / activeSessions)` so `sum(caps) <= account bytes/sec`. A positive configured rate below 8 bps floors to 0 bytes/sec and is blocked (`cap = -1`), never treated as unlimited. Byte quota and rate apply together. Example: `account_byte_limit = 10,000,000,000` and `account_rate_limit = 2,400` is 10 GB remaining at 300 B/s aggregate.

Admit, release, and successful renew return the cluster session total. The process-local `AccountRateAllocator` applies that total to every registered local session cap. Existing **local** sessions that already hold a positive equal-share cap are updated in place via `IOutboundRateCap.UpdateMaxSendBytesPerSecond` on admit/release. Sessions on other nodes still hold their last cap until that node's next SessionState renew (~10 s; no extra Redis traffic). A cross-node join would overshoot if the new session took an equal share while remotes still held `floor(rate / previousCount)`. The unused remainder `accountBytes % remotes` is safe only for a single joining node; a second joining node would take another remainder while the established node still holds nearly the whole aggregate. Joiners are therefore blocked (`cap < 0`) until this node owns every remaining session (remote leases expire and are pruned from the SessionState HASH). Established local sessions may only drop: the allocator assumes remotes that were already in the last applied split still hold this node's last per-session cap, and remotes observed since that split hold 0. Remote release is safe under-use: remotes stay at the old (lower) share until they renew. The write path reads only the local cap and throttles; it never GETs Redis, EVALs, or queries session counts.

The rate-limit boundary is the wire-ward stream under TLS/DEFLATE: `OutboundRateLimiter` wraps the `NetworkStream` (or prefixed stream) and stays under `SslStream` / `NntpDeflateStream`. Throttled octets are those written toward the socket (TLS records when TLS is active; compressed octets when DEFLATE is used without TLS). That is intentionally different from byte accounting, which counts uncompressed application bytes at `PipeWriter.Advance`. For rate: `cap > 0` throttles, `cap == 0` is passthrough (unlimited), and `cap < 0` blocks writes until a later update. This is not the remaining-byte `0 = exhausted` rule. A positive `account_rate_limit` that floors to `0` bytes/sec (more sessions than bytes/sec, or below 8 bps) is blocked, not unlimited. The limiter is created for every connection at cap `0` and is only assigned a non-zero cap after admission when `account_rate_limit > 0`.

Local pending totals are aggregated by authenticated account name across sessions on the same node (one ledger per account; the write path is still `Interlocked.Add` only). `SessionStateService` reconciles bytes on the same ~10-second cadence as lease renewal (and once more on a bounded shutdown, before `ReleaseOwner`). One account-level MySQL consume, then one Redis operation per account that needs work: combined RENEW+APPLY when the account has SessionState ownership and a committed batch, APPLY-only otherwise. Empty batches do no I/O. Batching is intentionally not byte-exact: overshoot is bounded by roughly `(sum of local authenticated session throughputs) × 10s` plus one in-flight NNTP response. Example: one session at 1 Gbit/s can overshoot by about 1.25 GiB between cycles. Process crash loses unreconciled in-memory pending (at-most-once durable charge). Durable quota must never be double-charged. Combined EVAL still APPLYs when renewal is lost so a stale lease cannot skip quota.

MySQL is the durable remaining store. Consume uses a transaction (`SELECT … FOR UPDATE`, then `UPDATE` with `CASE` that floors at zero, then `SELECT` remaining). The post-update remaining is committed with the subtract. Redis `nntpd:bytes:{sha256hex(accountName)}` is a HASH with **no key TTL**: field `remaining` is cluster-wide live remaining; field `b:{batchId}` marks a MySQL-committed batch as applied. APPLY(`batchId`, `consumed`, `mysqlRemainingAfter`) is idempotent: the same batch id is a no-op. Remaining is initialized from MySQL remaining after that batch when the key is missing, otherwise floored to `min(current, mysqlRemainingAfter)`. It never increases and never goes negative. `consumed` is not subtracted in Redis — MySQL already subtracted the batch, and another node's APPLY may already have floored to a mysql_after that includes it. A lost Redis reply is retried with the same in-process batch id. A process crash after MySQL drops the batch id and does not replay; Redis may stay stale-high until a later APPLY floors it. `Redis < MySQL` is a valid conservative state and is never repaired upward (APPLY is not a sync-from-MySQL). After APPLY is idempotent, a duplicate retry cannot create an artificial Redis-low. Redis-low can still occur when a later node's lower `mysqlRemainingAfter` arrived first, or when an operator raises MySQL remaining while the Redis key still exists; a top-up becomes visible on Redis only after that key is deleted. Effective remaining is `min(Redis, MySQL)` when Redis exists. Batch marks are bounded (256 retained); evicted marks remain retry-safe because remaining is never increased. Observe never creates or raises a key. If an existing Redis remaining is stale-high versus MySQL, observe floors that key down (never up) so a later MySQL top-up cannot become visible through leftover Redis. A missing Redis key is reconstructed from current MySQL remaining, never from an original configured quota.

Quota operations stay separate. **Consumption** subtracts MySQL remaining (floor 0) and floors Redis toward that MySQL value. **Observation** is `min(Redis, MySQL)` when the Redis key exists, otherwise MySQL; it may floor stale-high Redis and must never increase Redis. **Explicit top-up** is an operator MySQL change plus `IAccountByteAccountant.DeleteAccountByteStateAsync` (Redis `DEL` of `nntpd:bytes:{sha256hex(accountName)}`). This repository has no account-management path that raises `account_byte_limit`; the only SQL write is consume. After invalidation the next observe/APPLY may initialize Redis from the new durable remaining. A MySQL top-up without that delete leaves effective remaining at `min(existing Redis, new MySQL)`, including `0` when Redis is still `0`. APPLY never autonomously increases an existing remaining value. AUTHINFO still returns `281` when remaining is already `0`; the next command is `400 Service temporarily unavailable` (RFC 3977 §3.2) and the session closes. An in-flight response is finished; the gate runs at the next command. Exhaustion is a process-local `Interlocked` flag visible to every local authenticated session of that account without a per-command Redis GET.

`VectorNNTP.NNTPD.Transit` owns cluster-wide inbound Transit connection limits in `Transit/TransitPeerState/`. This is a separate bounded context from SessionState. The Redis identity is `TransitPeerPolicy.Identifier` (the Transit dictionary key). Source IP is the AllowFrom ACL admission predicate only; it is never the Redis connection-limit key. `MaxIncomingConnections` is cluster-wide. `0` means closed (reject all new inbound Transit connections), not unlimited. Redis is authoritative for every named-peer admit: there is no local-only fallback. The HASH is `nntpd:tconn:{sha256hex(identifier)}` with field `{ownerId}` (`{nodeId}:{process-incarnation-guid}`) and value `{expiryUnixMs}|{generation}|{count}`. Ownership uses value expiry (30s lease, 10s renewal), not a Redis key TTL. Leases are crash recovery, not a Transit idle timeout. `TransitPeerStateService` renews independently of NNTP traffic, article transfers, CHECK, TAKETHIS, commands, bytes transferred, and idle state. Listeners stop before `TransitPeerStateService` so admitted connections finalize/RELEASE first; `ReleaseOwner` then clears leftovers and new admits on that tracker fail closed. New-connection Redis failure keeps the existing Transit wire behavior: `400 Service temporarily unavailable`, close, no greeting. Diagnostics report the local owned count and configured `MaxIncomingConnections`; they do not read Redis on accept or telemetry paths. `MaxOutgoingConnections` is not part of this HASH.

After successful authentication: AUTHINFO commands return `502`; CAPABILITIES omits `AUTHINFO`, `SASL`, and `MODE-READER`. Passwords, SCRAM keys, and SASL proofs are never logged.

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
3. Double miss → `238`. CHECK does not insert locally or enqueue a Redis write. Presence is recorded only by `Remember` after a successful IHAVE or TAKETHIS accept.
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

`NntpdOptions` binds from the `Nntpd` section, including nested `Systemd` options, listener bind settings, Cloudflare DNS settings, `HistoryTime`, `IdleTime` (NNTP command idle seconds), `MaxArticleSize` (destuffed POST article limit), and a generated FQDN (`nntpd{ServerId:00}.{DnsSuffix}`). Redis binds from the top-level `Redis` section. Outbound email binds from the top-level `Email` section (disabled by default). Email EventIds are 2600–2615.

Mandatory settings that fail startup when missing or invalid: `CloudFlareApiKey`, `CloudFlareZoneId`, `ServerId` (`1–99`, no default), and `Redis:Host`. Validation runs via `ValidateOnStart` / `IValidateOptions` before the host enters the running state. Validation does not bind sockets or call Cloudflare. After validation, `CloudflareDnsReconciliationService` reconciles and verifies A/AAAA for the generated FQDN against resolved bind addresses, then `RedisService` connects and PINGs; either failure prevents `Running`. Details: [configuration.md](configuration.md). Serilog is configured under the `Serilog` section.

## Testing strategy

- Offline tests live under `tests/VectorNNTP.NNTPD.Tests/`, grouped by production subsystem (`Cloudflare/`, `Core/`, `Hosting/Systemd/`, `Configuration/`, `Networking/`, `Logging/`), with shared `Fixtures/` and `TestDoubles/`.
- Unit tests drive lifecycle, service manager, systemd notifier, watchdog, and Serilog exclusivity with deterministic fakes and collecting sinks.
- Host integration tests use `Host.CreateApplicationBuilder` without network listeners.
- Platform-specific service managers are registered but do not require an actual Windows Service or systemd session for tests.
- Real systemd verification is documented separately and must not be claimed from unit tests alone.

## Non-goals (deferred)

NNTP article retrieval remains deferred. AUTHINFO USER/PASS and AUTHINFO SASL (PLAIN, LOGIN, CRAM-MD5, SCRAM-SHA-256) authenticate against the configured newsmaster and MySQL `nntpusers` through the existing NntpDB pool. Every authenticated account receives both `account_rate_limit` (enforced by `VectorNNTP.NNTPD.SessionState.RateLimiting` at the outbound transport) and `account_byte_limit` (enforced by `VectorNNTP.NNTPD.SessionState.BytesAccounting` on the `SessionStateService` cycle; batched ~10s MySQL durable + Redis cluster remaining; see above). COMPRESS DEFLATE (RFC 8054), TAKETHIS streaming ingestion (RFC 4644), CHECK HistoryDB (RFC 4644), IHAVE transit ingest (RFC 3977 §6.3.2; raw wire receive, destuff downstream), POST (RFC 3977 §6.3.1; streaming receive into one stuffed IHAVE/TAKETHIS queue buffer, strict article validation, catalogue snapshot newsgroup/posting-status checks, moderator authorization for status `m`, proto-article moderation submission via `IEmailService` durable spool acceptance, server-owned injection metadata on the ordinary injection path, HistoryDB duplicate detection after the terminator, TryAdmit only), LIST/GROUP against the in-memory newsgroup catalogue, and the session authorization gates are in place. LISTGROUP remains a registered placeholder. The outbound Email subsystem is implemented as generic application infrastructure; moderation is one producer.
