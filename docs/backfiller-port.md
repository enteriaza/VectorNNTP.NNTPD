# VectorNNTP.BackFiller port — Phase 0 behavioral inventory

This is a **migration inventory**, not a translation guide.

- **Old repository** (`C:\Users\chrisk\source\repos\VectorNNTP.BackFiller`): behavioral / functional reference. Do not copy it. Do not modify it.
- **New project** (`src/VectorNNTP.BackFiller`): clean reimplementation under this repository’s engineering standards.
- **VectorNNTP.NNTPD**: locked. This port must not change NNTPD production code, tests, topology, or Article Work RPC.

Protocol wire contracts already documented in this repository (NNTPD Article Work client + architecture) remain authoritative for interoperation. Old BackFiller protocol documents remain the worker-side source of truth for consume, ACK/NACK, and `cache://` URI semantics.

## 1. Existing BackFiller responsibilities

The old worker recovers Usenet articles that are not available from primary storage and returns them to the VectorNNTP pipeline.

Externally it:

1. Consumes RabbitMQ article-work RPC requests for configured provider backbones.
2. Retrieves `ARTICLE <message-id>` from an upstream Usenet provider for that backbone.
3. Validates acquired bytes (NNTP article parse, yEnc where applicable).
4. Retains successful article payloads in process memory and advertises them with a `cache://` URI.
5. Publishes a version-1 JSON RPC response to the request `ReplyTo`.
6. Settles the original delivery (ACK / NACK + requeue policy).
7. Optionally streams recovered articles downstream to TransitServer via NNTP `TAKETHIS`.
8. Serves retained payloads to cache clients over a TLS listener using a custom Listener protocol (not a full NNTP server).
9. Loads per-backbone NNTP account credentials from MySQL (`nntpbackfilleraccounts`).
10. Provisions and renews Let’s Encrypt certificates (Cloudflare DNS-01) for the listener FQDN.
11. Publishes backbone usable-capacity on a control plane so admission can track provider session budget.

It is **not** an NNTP reader/server equivalent to NNTPD. Listener traffic is cache-fetch, not NNTP command processing.

## 2. Existing application lifecycle

Old `Program.Main` is a phased pipeline:

| Phase | Behavior |
|---|---|
| Bootstrap | Serilog bootstrap logger, culture, build metadata, global exception handlers, thread-pool logging. |
| Commands | Early `--help` / `--version` / diagnostic commands before host composition. |
| Validate | Configuration + dependency validation (fail-fast, explicit exit codes). Ctrl+C during validation cancels startup only. |
| Snapshot | Immutable `BackFillerRuntimeOptions` produced once; hosted services consume the snapshot. |
| Compose | `HostComposer` registers DI + hosted services; `ValidateOnStart` runs at `Build()`. |
| Run | `HostLifetimeCoordinator` starts the Generic Host. |
| Shutdown | `ShutdownCoordinator` graceful → forced tokens; host `ShutdownTimeout` = `BackFiller:Shutdown:GracePeriodSeconds`. |

Lifecycle state machine is old `ServiceLifecycle` (Starting / Validating / Initializing / Running / Stopping / Faulted). This is **not** the same type as NNTPD `ApplicationLifecycle`. Do not copy either blindly.

Exit codes distinguish configuration failure, dependency failure, startup cancellation, unexpected failure, and normal shutdown. systemd unit uses `Type=notify`, `RestartPreventExitStatus=2 4 5`, and `TimeoutStopSec` greater than the host shutdown timeout.

## 3. Existing services / components

Hosted / singleton graph from old `HostComposer`:

| Area | Components |
|---|---|
| Time | `TimeProvider.System` |
| Shutdown | `ShutdownCoordinator` |
| Accounts | `MySqlNntpAccountSnapshotProvider`, `NntpAccountSnapshotStartupInitializer` |
| RabbitMQ | `RabbitMqConnectionManager`, `RabbitMqTopologyInitializer`, consumer session factory, `RabbitMqConsumerService` |
| Transit | `TransitPublisher` (+ admission gateway), startup initializer |
| Certificates / listener | certificate state/store, ACME issuer, DNS TXT verifier, provisioning + renewal hosted services, `BackFillerListenerSocketService` |
| Control plane | `BackboneUsableCapacityState`, `ControlPlaneService` |
| Article work | retention authority + sweep, grabber workflow, request parser, backbone retriever, processor, disposition planner, response factory, response publisher, result sink, `RabbitMqArticleProcessingService` |

Supporting runtime: NNTP acquisition sessions, article parser, yEnc validator, independent RabbitMQ consumer-session and NNTP execution-session pools (Model B retirement/drain).

## 4. Existing configuration contract

Bound under `BackFiller` plus `ConnectionStrings:GrabberDB`.

Required sections observed in old options:

- Identity: `Name`, `Id` (0–99), `DnsSuffix` (default `usenet.ninja`) → canonical FQDN.
- Bind: `BindAddress` (optional; omitted = all interfaces), `BindPort` (required).
- Paths: `DirCerts`, log directory.
- `LetsEncrypt` (ACME email/key, Cloudflare token/zone, DNS poll/renewal).
- `RabbitMQ` (hosts, port, TLS, credentials, vhost, channel/connection pool, publish-confirm timeout, maximum shutdown drain timeout).
- `TransitServer` (host, port, TLS).
- `ArticleRetention` (in-memory capacity / TTL / memory policy).
- `Shutdown` (`GracePeriodSeconds`, `DrainQueuedWork`, `FinishActiveArticles`).
- `Listener` (parser accumulation, TLS handshake timeout, I/O progress timeout, receipt-ack timeout, queued Found-payload bytes, max connections).

Invariant: `RabbitMQ:MaximumShutdownDrainTimeoutSeconds` ≤ `Shutdown:GracePeriodSeconds`. Publish-confirm timeout must not exceed the RabbitMQ drain timeout.

Secrets belong in environment / secrets stores, not committed samples. The old tracked `appsettings.json` contains live credentials and **must not be copied**.

Environment / systemd: `DOTNET_ENVIRONMENT`, optional `EnvironmentFile` at `/etc/vectornntp-backfiller/vectornntp-backfiller.env`. Old worker: no custom prefix (`BackFiller__*`, `ConnectionStrings__GrabberDB`). New canonical prefix: `backfiller__` (see Phase 1).

## 5. Existing RabbitMQ topology

Old BackFiller builds **one fanout + durable quorum queue per backbone** discovered from the MySQL account snapshot:

- Exchange, queue, and routing key share the name `grabbers.{backbone.ToLowerInvariant()}`.
- Exchange: durable fanout, not auto-delete.
- Queue: durable, non-exclusive, quorum (`x-queue-type=quorum`).
- Server id is not part of the entity name.

**This repository’s locked NNTPD already declares a different namespace:**

- `backfiller.<backbone>` for the twelve provider backbones.
- `backfiller.storage` for NNTPD’s internal storage path (not a BackFiller provider).

Exchange / queue / routing key are the same name after trim + invariant lower-case. Declaration is fail-closed and idempotent; incompatible existing entities are not deleted.

The new BackFiller **must consume the NNTPD-established `backfiller.*` topology**. Reintroducing `grabbers.*` would break interoperation with locked NNTPD. Old `grabbers.*` is historical reference behavior only.

Old BackFiller does not consume `backfiller.storage`. That path stays NNTPD-internal.

## 6. Existing RabbitMQ request / response behavior

Canonical v1 request JSON (application body only):

```json
{"version":1,"requestId":"...","messageId":"<id@host>","backbone":"Giganews"}
```

AMQP transport (not JSON): `CorrelationId`, `ReplyTo`, `ContentType=application/json`. NNTPD additionally sets AMQP header `RequestId` (must match JSON `requestId`) and `Expiration=1000`. Old BackFiller validates JSON `requestId` and AMQP `CorrelationId`/`ReplyTo`; it does not treat AMQP `RequestId` as a JSON field.

Identities are distinct: JSON `requestId` (stable across redelivery), AMQP `CorrelationId` (RPC transaction), `DeliveryTag` (settlement), connection generation (infrastructure).

InvalidRequest when payload/metadata is malformed, backbone mismatches the consuming queue, or `CorrelationId`/`ReplyTo` is missing. Malformed protocol is never `ProviderFailure`.

Response v1 JSON: `version`, `requestId`, `messageId`, `backbone`, `outcome`, plus `uri` (Success only) or `error` (terminal failures). Identity fields are never fabricated; `InvalidRequest` may null fields that were not parsed. Publish to request `ReplyTo` with the request `CorrelationId`. Response AMQP `MessageId` is a fresh UUID per publish attempt.

Disposition matrix (worker settlement — must stay identical):

| Outcome | RPC response | Disposition |
|---|---|---|
| Success | Yes (`uri` present) | ACK after publish confirm |
| ArticleNotFound | Yes | NACK `requeue=false` |
| InvalidArticle | Yes | NACK `requeue=false` |
| InvalidRequest | Yes if replyable; none if `CorrelationId`/`ReplyTo` missing | NACK `requeue=false` |
| ProviderFailure | None | NACK `requeue=true` |
| Cancelled | None | NACK `requeue=true` |
| UnexpectedFailure | None | NACK `requeue=true` |

Success order: process → build JSON → publish → wait confirm (`PublishConfirmTimeoutSeconds`) → ACK. Confirm failure/timeout → NACK `requeue=true` (not ACK). Crash after confirm and before ACK may duplicate responses (documented; not exactly-once).

Consumer Model B: Running → Retiring (`BasicCancel`) → drain admitted deliveries on the **same channel** → dispose channel → Stopped. Delivery tags are channel-scoped.

## 7. Existing NNTP upstream behavior

Per-backbone pooled NNTP execution sessions (independent of RabbitMQ consumer sessions):

1. Connect to account `hostname`/`port`, optional TLS from `usessl`.
2. Authenticate with snapshot username/password.
3. `ARTICLE <message-id>` using the exact accepted Message-ID string.
4. Acquire raw article bytes (including split-terminator handling).
5. Parse as NNTP article; classify missing vs invalid vs provider/transport failure.
6. yEnc validation when the article is yEnc.

Invalid Message-ID is a local rejection (not a provider miss). Acquisition buffer ownership transfers to the caller only on success.

## 8. Existing transit / downstream behavior

`TransitPublisher` maintains NNTP connections to `BackFiller:TransitServer`:

- Capability / optional STARTTLS / `MODE STREAM` negotiation.
- Admitted success articles are queued (global max item count, retry budget).
- Publish path is `TAKETHIS <message-id>` plus dot-stuffed payload.
- Responses correlate by Message-ID when present; shutdown/fault can mark work ambiguous and requeue or terminalize according to retry budget.
- Shutdown uses a drain grace period (default 5 min), inactivity watchdog (default 30 s), and absolute ceiling (default 30 min).

Transit admission failure on an already-retained success article is settled as NACK `requeue=false` (drop) in the result sink — a sharp edge that must be re-verified when that path is rewritten.

## 9. Existing article storage behavior

There is no durable article store. Successful payloads live in `ArticleRetentionAuthority`:

- Key: lowercase hex MD5 of the **exact** ASCII `messageId` bytes (no trim, no case fold, brackets kept).
- Success URI: `cache://{CanonicalBackFillerFqdn}:{BindPort}/{MessageIdMd5}`.
- Known vectors: `<12345@example.invalid>` → `30edc94157aa16fe644a45a1f1ffe160`; `<abc@example.invalid>` → `de438dc83d64b1fa9206cf4da9eed5cc`.
- Admission can fail on capacity, TTL/memory policy, or MD5 collision against a different Message-ID (collision is a hard reject).
- Listener takes read leases; sweep hosted service expires entries.
- Retention admission failure on the success path NACK-requeues (`requeue=true`).

NNTPD treats a Success `uri` as “found elsewhere”; the current locked NNTPD ARTICLE path still maps RPC Success to 430 until a later integration. Do not change that from this port.

## 10. Existing account / configuration lookup

`ConnectionStrings:GrabberDB` + table `nntpbackfilleraccounts`, filtered by `serverid = BackFiller:Id`.

Columns: `entryid`, `backbone` (enum of the twelve providers), `hostname`, `keepalive`, `maxconnections`, `username`, `password`, `port`, `serverid`, `usessl`.

Startup can `CREATE TABLE IF NOT EXISTS`. Periodic refresh publishes an immutable snapshot. RabbitMQ topology and NNTP session pools are derived from the snapshot’s backbone set and per-account connection limits.

This is a different MySQL surface from NNTPD `nntpusers`. Do not merge them.

## 11. Existing retry / recovery semantics

- Provider / cancel / unexpected: NACK requeue; RabbitMQ redelivers; `requestId` stays stable.
- Terminal article outcomes and invalid requests: NACK no-requeue (or ACK after confirmed Success).
- Response confirm failure: NACK requeue.
- Retention admission failure: NACK requeue.
- RabbitMQ connection manager: multi-connection pool, blocked-connection timeout, consecutive-recovery failure budget, reconnect backoff.
- Old code uses RabbitMQ.Client 7.2.2 with application-owned recovery (not a reason to copy its connection abstractions).
- Transit: per-item retry budget (`TransitRetryMaxAttempts`, default 3); reconnect initialization timeout when work is outstanding.
- ACME/DNS-01 has its own transient retry and TXT-propagation poll settings.

## 12. Existing concurrency semantics

- RabbitMQ consumer sessions and NNTP grabber sessions are separate pools with coordinated retirement.
- Admission creates in-flight accounting; retirement blocks new admissions and drains admitted work.
- Control plane leases backbone session capacity.
- Listener: 64 outstanding requests, 8 concurrent handlers, 64 outbound responses; writes serialized; responses may complete out of arrival order.
- Transit: global work queue with max item count (default 2048) and write-batch coalescing experiment (`WriteBatchCoalesceMicroseconds`).
- Article processing drain barrier prevents result-sink handoff after shutdown admission close.
- `AllowUnsafeBlocks` exists in the old csproj for hot-path parsing; do not enable unsafe in the new project until a measured path requires it.

## 13. Existing shutdown semantics

Configured graceful shutdown:

- `DrainQueuedWork` (default true) / `FinishActiveArticles` (default true).
- Shared grace budget: `Shutdown:GracePeriodSeconds` (sample 120; validated range used by systemd comments 5–600).
- RabbitMQ drain timeout ≤ grace period.
- Consumers stop admitting, cancel the consumer tag, settle on the original channel, then close.
- Transit drain: grace → inactivity watchdog → absolute maximum.
- Listener stops accept; graceful vs forced session shutdown; forced token aborts remaining connections.
- Generic Host `ShutdownTimeout` equals the grace period; systemd `TimeoutStopSec` must be larger (unit uses 630s).

Invariant claimed by the old README: every admitted unit of work is settled exactly once on some termination path. Channel-close requeue is a broker safety net, not the primary mechanism.

## 14. Existing externally observable behavior

A peer (NNTPD today) observes:

- Consume from `backfiller.<backbone>` (NNTPD) / historically `grabbers.<backbone>` (old worker).
- JSON v1 request/response + AMQP RPC properties listed above.
- Outcome/settlement matrix.
- Success `cache://` URI grammar and MD5 vectors.
- Subsequent Listener fetch of that URI (custom protocol, TLS).
- Optional TransitServer `TAKETHIS` of the recovered article.
- Fail-fast process exit on invalid configuration or missing dependencies.
- systemd notify lifetime.

NNTPD-observable client policy (already implemented, locked): first Success wins; `ArticleNotFound` / `InvalidArticle` / `InvalidRequest` are source-local; 500 ms storage grace is not a wait; 5 s aggregate deadline; 1 s request TTL is not end-to-end.

## 15. Existing tests and what they prove

Old `VectorNNTP.BackFiller.Tests` (net8.0, ~80 files) is **not** copied. It proves, incrementally useful later:

| Cluster | What it protects |
|---|---|
| Request/response wire + JSON schema | v1 property names, identity nullability, URI grammar, schema parity |
| Article processing Phase 3/4 | disposition planner, response factory, publisher confirms, result-sink ACK/NACK |
| InvalidRequest integration | non-replyable vs replyable invalid payloads |
| NNTP acquisition / parser / yEnc | ARTICLE framing, split terminator, canonical materialization |
| Grabber session manager | pool/lease/disposal races |
| RabbitMQ consumer Phase 2 | admission, retirement, channel-scoped settlement |
| Transit | MODE STREAM negotiation, TAKETHIS pipeline, publisher retry |
| Listener | session bounds, TLS listener, Found / ReceiptAck |
| Certificates / ACME | store, DNS-01 recovery, provisioning |
| Accounts | MySQL snapshot provisioning |
| Startup validation / hosting | fail-fast config, host composition, retention memory policy |
| Control plane | capacity leases |

New tests must prove those **contracts** against the rewrite, not old types.

## 16. Candidate new VectorNNTP.BackFiller bounded contexts

Create folders only when code exists. Expected later:

| Context | Responsibility |
|---|---|
| `Hosting` / `Logging` | Generic Host, Serilog-only, systemd/Windows Service (Phase 0) |
| `Configuration` | Options + `ValidateOnStart` + immutable snapshot |
| `RabbitMq` | Connection, `backfiller.*` consume, channel-scoped settlement |
| `ArticleWork` | Parse, process, disposition, response publish |
| `Nntp` | Upstream ARTICLE acquisition / parse / yEnc |
| `Transit` | Downstream TAKETHIS |
| `Storage` | In-memory retention + `cache://` identity |
| `Listener` | TLS cache-fetch protocol |
| `Accounts` | MySQL `nntpbackfilleraccounts` snapshot |
| `Certificates` | ACME + DNS-01 for listener |
| `ControlPlane` | Backbone usable capacity |

Phase 0 implemented host + logging. Phase 1 removed the no-op hosted service; the Generic Host stays alive via platform lifetime.

## 17. Old architecture → new architecture mapping

| Old | New (intent) |
|---|---|
| net8.0 Worker, R2R/single-file/self-contained, unsafe | net10.0 Worker per this repo; publish/GC/unsafe are later, evidence-driven decisions |
| `Serilog.AspNetCore` | `Serilog.Extensions.Hosting` (NNTPD standard) |
| `ServiceLifecycle` + `ShutdownCoordinator` | Decide later: Generic Host first; adopt NNTPD `ApplicationLifecycle` / `IApplicationService` only if BackFiller needs that orchestration |
| `CloudFlare.Client` package | Prefer this repo’s existing Cloudflare approach when certificates are ported; do not add the old package by default |
| `grabbers.*` topology | **`backfiller.*` as already declared by locked NNTPD** |
| Inline Article Work types | New BackFiller-owned consume-side types until a shared-contract project is explicitly approved |
| Large `HostComposer` | Incremental `IServiceCollection` extensions, NNTPD style, without cloning NNTPD’s full graph |
| Application-owned RabbitMQ recovery stack | Redesign against this repo’s connection-generation model when RabbitMQ is ported; do not copy old manager classes |

## 18. Items that must remain behaviorally identical

- JSON v1 request/response property names, types, and identity rules.
- AMQP `CorrelationId`, `ReplyTo`, `ContentType=application/json`.
- Outcome set and ACK/NACK + requeue matrix.
- Success publish-confirm-before-ACK ordering.
- `cache://` URI grammar and MD5-of-exact-`messageId` rule.
- Backbone case-insensitive ordinal match between JSON and queue context.
- Channel-scoped settlement; no cross-channel ACK.
- InvalidRequest vs ProviderFailure classification.
- Message-ID grammar already enforced by NNTPD/BackFiller validators (3..250, bracketed local@domain).
- Twelve backbone names used by NNTPD topology: Abavia, Altopia, BaseIP, Eweka, Elbracht, Giganews, GTT, Highwinds, ItsHosted, Novia, UExpress, UsenetNode1.

## 19. Items that can be re-engineered internally

- Project file, TFM, analyzers, publish profile, GC/thread-pool knobs.
- Logging package graph and `LoggerMessage` usage.
- DI composition and hosted-service types.
- RabbitMQ connection/channel abstractions (keep generation + ownership explicit).
- NNTP client implementation (keep ARTICLE/yEnc/parse semantics).
- Listener/transit internals (keep protocol and settlement edges).
- Lifecycle state machine implementation.
- Folder layout and naming (`VectorNNTP.BackFiller`, not `VectorNNTP.Backfiller`).
- Tests: rewrite against new types.

## 20. Items requiring explicit design decisions before implementation

1. **Shared Article Work contract ownership.** NNTPD types live in `VectorNNTP.NNTPD.RabbitMq.ArticleWork` and are internal to NNTPD. Extracting a shared project requires a separate approved task. Until then, BackFiller owns consume-side types; do not add a ProjectReference to NNTPD; do not modify NNTPD to expose internals.
2. **AMQP `RequestId` header.** NNTPD writes it and requires it to match JSON `requestId`. Old worker parsed JSON only. New worker should accept the header without putting it in JSON; mismatch policy (ignore vs InvalidRequest) must be decided when the parser is written.
3. **`grabbers.*` vs `backfiller.*`.** Interop decision is already made by locked NNTPD: consume `backfiller.*`. Document leftover `grabbers.*` broker entities as out of scope (NNTPD does not delete them).
4. **Lifecycle model.** Port old `ServiceLifecycle` vs adopt NNTPD `ApplicationLifecycle`/`ApplicationServiceManager` vs Generic Host only. **Phase 2:** Generic Host + one real `IHostedService` (`BackFillerRabbitMqService`) for fail-closed RabbitMQ startup. No placeholder `BackgroundService`. No NNTPD `ApplicationLifecycle`.
5. **Listener vs NNTPD fetch.** Whether NNTPD will later pull `cache://` URIs, and whether Listener remains a separate protocol, is integration work — not this phase.
6. **Transit coupling.** Whether recovered articles must still TAKETHIS to TransitServer in every Success path, and the drop-on-transit-reject settlement, needs confirmation before that path is rewritten.
7. **MySQL account schema.** Keep `nntpbackfilleraccounts` as-is vs any later shared auth store. Default: keep the table contract.
8. **Certificate stack.** Reuse NNTPD ACME/Cloudflare code via a future shared library vs a BackFiller-local rewrite. Do not reference NNTPD to borrow it.
9. **Environment-variable prefix.** **Decided in Phase 1 review:** one canonical contract. Prefix `backfiller__` is stripped, then `__` maps to `:`. RabbitMQ and Let's Encrypt are nested under `BackFiller`, so secrets use `backfiller__BackFiller__RabbitMQ__*` and `backfiller__BackFiller__LetsEncrypt__*`. GrabberDB stays on the framework `ConnectionStrings` section: `backfiller__ConnectionStrings__GrabberDB`. Short-form `backfiller__RabbitMQ__Username` is **not** supported. The old worker had no custom prefix and does not require that alias.
10. **Unsafe / R2R / single-file / 4 MB socket buffers.** Old defaults are deployment optimizations, not protocol. Revisit with evidence.

## Phase 1 — configuration and hosting foundation

Implemented in `src/VectorNNTP.BackFiller/Configuration` and `Hosting`. Bindable `BackFillerOptions` + `BackFillerConnectionStringsOptions` → `ValidateOnStart` → immutable `BackFillerRuntimeOptions` produced once from those option objects. Application services consume the snapshot. The factory does not re-read `IConfiguration`. `HostOptions.ShutdownTimeout` is post-configured from the snapshot grace period.

### Canonical configuration contract

| Item | Canonical value |
|---|---|
| BackFiller section | `BackFiller` |
| Connection-strings section | `ConnectionStrings` (framework section; only `GrabberDB` is consumed) |
| Environment-variable prefix | `backfiller__` (stripped, then `__` → `:`) |
| Identity | `backfiller__BackFiller__Name`, `backfiller__BackFiller__ServerId` |
| RabbitMQ secrets | `backfiller__BackFiller__RabbitMQ__Username`, `backfiller__BackFiller__RabbitMQ__Password` |
| ACME secrets | `backfiller__BackFiller__LetsEncrypt__CloudFlareApiToken`, `backfiller__BackFiller__LetsEncrypt__PfxExportPassword` |
| GrabberDB | `backfiller__ConnectionStrings__GrabberDB` |

Other `BackFiller:*` keys follow the same rule: `backfiller__` + section path with `__` separators (for example `backfiller__BackFiller__BindPort`).

Non-canonical names that are **not** a supported contract:

| Name | Why it is rejected |
|---|---|
| `backfiller__RabbitMQ__Username` / `Password` | NNTPD-style short form. NNTPD’s RabbitMQ section is top-level; BackFiller’s is nested. Not an alias. |
| `backfiller__LetsEncrypt__*` | Same: Let's Encrypt is nested under `BackFiller`. |
| `backfiller__BackFiller__Id` / `BackFiller:Id` | Intentional rename to `ServerId`. Not an alias. |
| Unprefixed `BackFiller__*` / `ConnectionStrings__GrabberDB` | Old worker used Generic Host’s default environment source (no custom prefix). The default host still loads unprefixed environment variables, but that is framework behavior, not a second first-class BackFiller contract. Operators should set the prefixed names. |

### Why the short-form alias was removed

A Phase 1 draft copied short-form `RabbitMQ` / `LetsEncrypt` keys onto the nested `BackFiller` options so `backfiller__RabbitMQ__Username` would look like NNTPD’s `nntpd__RabbitMQ__Username`.

The old worker does **not** require that:

- It called `Host.CreateApplicationBuilder` and used the default (unprefixed) environment source.
- RabbitMQ was already nested: the observable env path was `BackFiller__RabbitMQ__Username`, not a root `RabbitMQ__Username` alias.
- GrabberDB was `ConnectionStrings__GrabberDB` (framework section), never a BackFiller-nested alias.

The alias was convenience, not compatibility. It is not retained.

### Intentional differences from the old worker

| Topic | Old | New |
|---|---|---|
| Server identifier key | `BackFiller:Id` | `BackFiller:ServerId` (range still **0–99**, not NNTPD’s 1–99) |
| Log / cert directories | `DirLogs`, `DirCerts` | `LogDirectory`, `CertificateDirectory` |
| Directory startup probe | Create directory and write/read/delete probe files | Path required and resolved to absolute; no filesystem mutation during validation |
| Bind port-in-use check | Attempted live bind | Syntax + local-NIC check only (NNTPD convention; no sockets) |
| GrabberDB | Shape + live connectivity probe | Shape only (`MySqlConnectionStringBuilder`); no connection |
| Secret defaults | Tracked placeholders / live values | No committed secrets; required via env / secrets |
| Cloudflare zone default | Hard-coded zone id | Required, no default |
| Env prefix | None (default host env: `BackFiller__*`, `ConnectionStrings__GrabberDB`) | Canonical `backfiller__` + nested path. No short-form alias. |
| Lifecycle | `ServiceLifecycle` + pre-host validation pipeline + many hosted services | Generic Host + `ValidateOnStart` + platform lifetime. No placeholder `BackgroundService`. `ApplicationLifecycle` deferred. |

### Lifecycle decision (Phase 1)

`IHost.RunAsync()` waits on platform lifetime (`ConsoleLifetime` / systemd / Windows Service). A placeholder `BackgroundService` is not required to keep the process alive and would be an architectural dependency to undo when real workers arrive. Do not port old `ServiceLifecycle`/`ShutdownCoordinator` and do not adopt NNTPD `ApplicationLifecycle` until later services need ordered start/stop.

## Phase 0 / Phase 1 implementation status

Created in this repository:

- `src/VectorNNTP.BackFiller` — net10.0 worker host, Serilog-only logging, systemd/Windows Service registration, options + `ValidateOnStart` + immutable runtime snapshot. No placeholder hosted service.
- `tests/VectorNNTP.BackFiller.Tests` — configuration binding, validation, snapshot, and host-composition tests (no old tests copied).
- This document.

Not created: Article Work processing, NNTP, transit, retention, listener, accounts, certificates, or any NNTPD integration.

## Phase 2 — RabbitMQ connection foundation

Implemented in `src/VectorNNTP.BackFiller/RabbitMq`. One process-wide connection owner (`BackFillerRabbitMqService`) registered as an `IHostedService`. Callers obtain a non-owning generation handle and may open caller-owned channels. Article Work processing is deferred.

### Ownership

| Resource | Owner | Notes |
|---|---|---|
| TCP/AMQP connection | `BackFillerRabbitMqService` | Sole long-lived connection. Consumers must not open competing connections. |
| Connection handle | Caller (non-owning) | Snapshot of generation + `IsCurrent`. Not a pin. Not disposable. |
| Channel | Caller that called `CreateChannelAsync` | Dedicated channel per future consumer. Disposing a channel must not dispose the connection. |

RabbitMQ.Client automatic recovery and topology recovery are disabled. Application-owned recovery is the only recovery path.

### Startup and recovery

- **Startup is fail-closed.** `StartAsync` connects once. If the broker is unreachable or the connection is not usable, startup fails, any partial connection is disposed, and the host does not run.
- **Post-startup recovery is indefinite.** After a successful start, connection loss is a degraded state. One watch task reconnects with exponential backoff (`PoolReconnectBaseDelayMs` / `PoolReconnectMaxDelayMs`) until a new generation is installed or shutdown cancels the loop. There is no consecutive-failure budget that permanently abandons recovery (intentional difference from old `MaxConsecutiveRecoveryFailures` escalation).
- **Single connection.** Phase 2 does not implement the old min/max connection pool. `MinConnections` / `MaxConnections` remain on the snapshot for a later evidence-driven pool if Article Work needs it. Channels are created per caller on the current connection.

### Generation fencing

- Each successful install increments a monotonic generation.
- `ConnectionLost` from a sender that is not the published current connection is ignored and cannot request recovery or retire a newer generation.
- Replacing a generation disposes only the captured previous instance, not whatever later became current.
- Direct dispose of a retired connection object cannot dispose the current generation.
- `TryGetCurrent` / `IsCurrent` fail as soon as the published connection is closed, unpublished, or the service is stopping.

### Topology

**Not declared in this phase.** Locked NNTPD already declares durable fanout exchanges and quorum queues named `backfiller.<backbone>` (and `backfiller.storage`). Old BackFiller declared `grabbers.*` from the MySQL account snapshot at startup; that path is deferred with accounts. `BackFillerRabbitMqTopology` records the canonical `backfiller.*` provider names only. `grabbers.*` is out of scope. `backfiller.storage` is NNTPD-internal and is not a BackFiller consume target.

### Article Work

Deferred. This phase does not deserialize requests, publish responses, ACK/NACK, or consume queues.
