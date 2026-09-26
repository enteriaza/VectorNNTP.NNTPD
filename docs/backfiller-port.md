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
- Message-ID grammar already enforced by NNTPD/BackFiller validators (bracketed local@domain). Phase 3 uses the NNTPD `IsWellFormed` envelope capped at 250 (see Phase 3).
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
2. **AMQP `RequestId` header.** **Decided in Phase 3:** the header is not a JSON field and is not required. When present it must be a non-empty GUID that matches JSON `requestId`; mismatch is `InvalidRequest`. Absence is accepted (protocol MD + old parser). NNTPD always sends the header and matches it on the *response* router (ignore), not as a worker-side required request field. Same presence-only rule for AMQP `ContentType`: if present it must be exactly `application/json`.
3. **`grabbers.*` vs `backfiller.*`.** Interop decision is already made by locked NNTPD: consume `backfiller.*`. Document leftover `grabbers.*` broker entities as out of scope (NNTPD does not delete them).
4. **Lifecycle model.** Port old `ServiceLifecycle` vs adopt NNTPD `ApplicationLifecycle`/`ApplicationServiceManager` vs Generic Host only. **Phase 2:** Generic Host + `BackFillerRabbitMqService` for fail-closed RabbitMQ startup. **Phase 3:** a second `IHostedService` (`ArticleWorkConsumerService`) with a *local* consumer lifecycle (`Created → Starting → Running → Retiring → Stopped`). No placeholder `BackgroundService`. No NNTPD `ApplicationLifecycle`. No second connection-recovery loop.
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

Deferred in Phase 2. Phase 3 adds consume, validation, work-item admission, disposition, and settlement. Provider retrieval remains deferred.

## Phase 3 — Article Work consumption, validation, and settlement

Implemented in `src/VectorNNTP.BackFiller/ArticleWork`. This phase is the inbound protocol boundary only. It does **not** retrieve articles from upstream providers.

### Consumer ownership

| Resource | Owner |
|---|---|
| Process RabbitMQ connection | `BackFillerRabbitMqService` (Phase 2). Sole recovery owner. |
| Consume channel | One `ArticleWorkConsumerSession` per provider backbone. Caller-owned via `CreateChannelAsync`. Disposing the channel must not dispose the connection. |
| Hosted orchestration | `ArticleWorkConsumerService` (`IHostedService`, registered after the connection owner). Starts 12 backbone sessions. Rebuilds sessions on `ConnectionReplaced` (`IsReplacement=true`) without a second reconnect loop. |

Startup is fail-closed: if the current generation cannot create every required consumer, `StartAsync` fails and the host does not run. After start, a failed rebuild is logged; the host is not crashed. Prefetch is `ConsumerPrefetchCount` when configured, otherwise `1` (single-dispatch per session). Topology is not declared. Queues are `backfiller.<backbone>` from `BackFillerRabbitMqTopology`. `grabbers.*` is not used.

### Request validation boundary

`ArticleWorkRequestParser` is the only request deserializer. Property names are case-sensitive. Unknown JSON fields are ignored. Identities are never synthesized.

Required JSON fields: `version` (integer `1`), `requestId` (non-empty GUID), `messageId` (well-formed Message-ID), `backbone` (non-empty; ordinal-ignore-case match to the consuming session).

Required AMQP properties: `CorrelationId`, `ReplyTo`. Missing either is `InvalidRequest`.

Presence-only AMQP checks (not required by the protocol MD or old parser; NNTPD always sends both):

- `ContentType`, when present, must be exactly `application/json`.
- AMQP `RequestId` header, when present and non-whitespace, must be a non-empty GUID equal to JSON `requestId`.

`WorkRequestMaxPayloadBytes` rejects oversized bodies before JSON parse.

### Backbone-scoped consumption

Each session consumes one queue and rejects a JSON `backbone` that does not match that session, even if the message arrived on the queue. Comparison is ordinal-ignore-case. The JSON string is preserved as supplied (not case-folded).

### RequestId vs CorrelationId vs delivery tag

| Identity | Layer | Role |
|---|---|---|
| JSON `requestId` | Application | Logical lookup. Stable across redelivery. |
| AMQP `RequestId` header | Transport | Echo of JSON `requestId` when NNTPD published the request. Not a JSON field. |
| AMQP `CorrelationId` | Transport | Individual RPC publication. Distinct from `requestId`. |
| Delivery tag | Channel | ACK/NACK identity. Channel-scoped. Not globally unique. |
| Connection generation | Infrastructure | Fences settlement after replacement. |

These must not be merged or substituted for each other.

### Work-item model

- Wire: `BackFillerRabbitMqConsumedDelivery` (bytes + AMQP metadata; no RabbitMQ.Client types).
- Parse: `ArticleWorkParseResult` / `ArticleWorkRequest`.
- Admitted work: `ArticleWorkItem` (validated request + CorrelationId/ReplyTo + settlement lease).
- Domain code does not carry `JsonDocument` through the worker.

Message-ID validation is NNTPD `IsWellFormed` (`<local@domain>`, length ≥ 5) capped at 250 (NNTP command envelope). The exact accepted string is preserved. Old INN/dot-atom grammar is not imported.

### Settlement ownership

`ArticleWorkSettlementLease` binds one delivery tag to the original channel and generation. Settlement is exactly-once: ACK or NACK, never both, never twice. A replacement channel cannot settle another channel's tag. A lost generation sets `channelStillCurrent=false` and skips settlement; later consumer rebuild establishes a fresh consumer. Failed broker RPCs do not claim success.

### Terminal vs retryable dispositions

| Outcome | Response seam | Settlement |
|---|---|---|
| Success | Publish intent | ACK |
| ArticleNotFound | Publish intent | NACK `requeue=false` |
| InvalidArticle | Publish intent | NACK `requeue=false` |
| InvalidRequest | Publish intent if `CorrelationId` and `ReplyTo` are present | NACK `requeue=false` |
| ProviderFailure | None | NACK `requeue=true` |
| Cancelled | None | NACK `requeue=true` |
| UnexpectedFailure | None | NACK `requeue=true` |

Phase 3 default handler was `DeferredArticleWorkHandler`. Phase 4 replaces it with `ProviderArticleWorkHandler`.

`IArticleWorkResponsePublisher` is a test seam (`RecordingArticleWorkResponsePublisher` in DI). It does not talk to the broker. Publisher confirms are not decided. Publish-seam failure is treated as retryable (NACK requeue, no ACK).

### Stale-generation behavior

A delivery captures the consume-channel generation. If that generation is no longer current, the session does not ACK/NACK through a replacement channel and does not open a replacement channel behind the in-flight delivery. Channel-close requeue remains the broker safety net.

### Cancellation behavior

`ArticleWorkOutcome.Cancelled` is distinct from host shutdown. Pipeline cancellation (token or `OperationCanceledException` while the token is cancelled) maps to NACK `requeue=true` and no terminal response. Session retirement is Model B: block admission, `BasicCancel`, drain already-admitted work on the original channel, dispose the channel. Drain does not convert admitted work into `Cancelled`.

### Explicit deferral

Phase 3 deferred upstream NNTP. Phase 4 implements provider ARTICLE retrieval. Still deferred: full article-header grammar / yEnc, retention, `cache://` serving, Transit `TAKETHIS`, full response publish + confirms, ACME, Cloudflare, MySQL accounts, control-plane capacity.

### Protocol discrepancies recorded (not silently chosen)

1. **AMQP `RequestId` / `ContentType`.** Protocol MD required transport fields are only `CorrelationId` and `ReplyTo`. Old parser follows that. NNTPD always publishes `ContentType=application/json` and header `RequestId`. Phase 3 validates those two only when present (decision 2).
2. **Unbracketed Message-ID example.** Protocol MD "Canonical Incoming Request Example" uses `"messageId":"12345@example.invalid"` (no brackets). The same document's canonical payload and valid example use brackets. Old parser and NNTPD `IsWellFormed` require brackets. Phase 3 requires brackets; the unbracketed example is treated as a documentation defect, not a second wire contract.
3. **Message-ID length / grammar.** NNTPD `IsWellFormed` is 5–998, brackets + `@`. Old BackFiller is INN/dot-atom 3–250. Port inventory previously said 3–250. Phase 3: 5–250, NNTPD envelope, no INN grammar. Tokens longer than 250 are `InvalidRequest` here even if NNTPD would accept them as well-formed.
4. **Consumer lifecycle names.** Protocol MD lists Running / Retiring / Stopped. Phase 3 adds Created / Starting as requested local states. This is not NNTPD `ApplicationLifecycle`.

## Phase 4 — upstream provider retrieval

Implemented in `src/VectorNNTP.BackFiller/Nntp`. The Phase 3 consume/validate/settle boundary is unchanged. This phase retrieves `ARTICLE <exact-message-id>` and classifies the result. It does **not** retain articles, publish RPC responses, or ACK Success.

### Provider / session ownership

| Resource | Owner |
|---|---|
| Provider identity | `IBackFillerProviderCatalog` / `BackFillerProviderDefinition`. Empty static catalog in production until MySQL accounts exist. |
| TCP/TLS stream | `INntpTransportFactory` (`TcpNntpTransportFactory` in production). |
| NNTP session | `NntpProviderSession`. One ARTICLE at a time. |
| Session pool | `NntpSessionPool` per backbone. |
| Pool set | `NntpProviderRegistry` (`IHostedService`, registered after RabbitMQ and before Article Work consumers). |
| Retrieval | `NntpArticleRetriever` acquires a lease, calls ARTICLE, releases or retires. Article Work never holds raw pool ownership. |

RabbitMQ `MinConnections` / `MaxConnections` remain **RabbitMQ connection-pool policy** from Phase 2. They are not NNTP session limits. NNTP capacity is `BackFillerProviderDefinition.MinSessions` / `MaxSessions`. Old worker used MySQL `maxconnections` as the exact eager slot count; Phase 4 uses `MaxSessions` as the hard bound and `MinSessions` as optional warmup (0 = fully lazy). That difference is intentional until account polling exists.

### Pool semantics

- Acquire waits for a lease slot (`SemaphoreSlim` = `MaxSessions`).
- Idle reusable sessions are reused. A session is never leased to two callers.
- Connect happens on first need (or warmup). One TCP session is not opened per ARTICLE when idle capacity exists.
- Return vs retire is exclusive and exactly-once (`NntpSessionLease`).
- Double-dispose of a lease is a no-op.
- Shutdown cancels waiters, waits for active leases up to `BackFillerRuntimeOptions.Shutdown.GracePeriod`, then force-retires remaining live sessions. The grace period is the existing BackFiller shutdown budget, not a second global timeout.

### Authentication and TLS

- Implicit TLS from connect when `UseTls` is true (old `usessl`). Not STARTTLS (Transit-only in the old worker).
- `SslStream.AuthenticateAsClientAsync` with TLS 1.2/1.3 and **platform certificate validation**. No accept-all callback.
- AUTHINFO USER/PASS only when both username and password are configured (RFC 4643). Half-configured credentials fail locally without sending AUTHINFO.
- Credentials are never logged. AUTHINFO arguments are written as bytes; debug logs are not emitted for those commands.
- AUTHINFO 281 accepts; 381 prompts PASS; 480/481/482/5xx are authentication failures.

### ARTICLE retrieval

Command bytes: `ARTICLE ` + exact ASCII Message-ID + CRLF. No case-fold, no bracket changes. Non-ASCII Message-IDs are not sent.

Greeting must be 200 or 201 (RFC 3977 §5.1.1). Other greetings are provider failures; TCP connect alone is not health.

220 starts a multiline block (RFC 3977 §3.1.1 / §6.2.1). The destuffed payload excludes the terminating `.` line. A line-start `..` becomes `.`. Hard maximum destuffed size is 5 MiB (`ArticleResourceLimits.MaxArticleBytes`, old contract, not config).

### Response classification

| Retrieval | Article Work outcome | Session reusable? |
|---|---|---|
| 220 + destuffed payload with header/body separator | Success (internal `ArticleRetrieved`) | yes |
| 430 | ArticleNotFound | yes |
| 220 but empty / no header-body separator | InvalidArticle | yes |
| 480/481/482 or AUTHINFO failure | ProviderFailure (`AuthenticationFailure`) | no |
| Timeout, EOF, malformed status/greeting, 412/420/423/5xx, oversized, truncated | ProviderFailure | no |
| Caller/shutdown cancel | Cancelled | no |

yEnc and the old full `NntpArticleParser` header grammar are **not** in this phase. InvalidArticle here means framing completed but the destuffed bytes are not a minimal article.

### Session retirement

Reusable after: Success, ArticleNotFound, InvalidArticle (protocol completed).

Retired after: connection/TLS/EOF/timeout/malformed/truncated/oversized/auth failure/cancellation/unexpected protocol state. Cancelled is **not** reusable (old `NntpArticleSessionHealthClassifier`).

A failed session is never enqueued idle.

### Cancellation / shutdown

Timeout → ProviderFailure, session retired. Caller cancel → Cancelled, not ArticleNotFound. Host shutdown: consumers drain first, then the provider registry disposes pools. Shutdown cancel must not be classified as a miss.

### Payload ownership

`RetrievedArticle` owns destuffed bytes. The payload remains readable after the session is released. The handler copies bytes for tests (`LastPayload`) and hands a separate owner to the pipeline, which disposes it. Retention is the next phase.

### Success must not ACK yet

`IArticleWorkResponsePublisher.CompletesSuccessPublication` is false on the Phase 3/4 recording seam. A retrieved article is classified Success at the disposition boundary but is settled as NACK `requeue=true` with **no** Success RPC publish. That matches the old “cannot finish Success without retain + publish confirm” edge (retention admission failure requeues). Phase 5 retains and produces a URI but still does not publish or ACK.

### Explicit deferral

Phase 4 deferred retention. Phase 5 implements the in-memory retention authority and `cache://` identity. Still deferred: real response publish + confirms, Success ACK, Transit, Listener/TLS serving, ACME, Cloudflare, MySQL accounts, control-plane capacity, DATE keepalive, STARTTLS for providers, yEnc.

### Phase 4 discrepancies

1. **Pool sizing vs RabbitMQ options.** `BackFiller:RabbitMQ:MinConnections` / `MaxConnections` are not NNTP pool sizes.
2. **Eager vs lazy.** Old worker eagerly connected `maxconnections` slots at initialize. Phase 4 is lazy unless `MinSessions` > 0.
3. **Status-line length.** RFC 3977 §3.2: 512 octets including CRLF. Old worker / Phase 4: 16 KiB.
4. **Article validation depth.** Old grabber parsed headers/dates/newsgroups and ran yEnc before Success. Phase 4 only destuffs and requires a header/body separator. yEnc remains a later stage.
5. **AuthenticationFailure** is a retrieval kind but maps to Article Work `ProviderFailure` (old processor contract).
6. **Success ACK.** Old Success ACKs only after retain + confirmed publish. Phase 4 never ACKs Success.

## Phase 5 — article retention authority

Implemented in `src/VectorNNTP.BackFiller/Retention`. One process-wide owner of retained article bytes. Independent of RabbitMQ, NNTP sessions, and Transit.

### Ownership

| Resource | Owner |
|---|---|
| Destuffed payload after ARTICLE | `RetrievedArticle` until `TryDetach` |
| After successful retain | `ArticleRetentionAuthority` (`RetainedEntry`) |
| Lookup | `ArticleLookupLease` (refcount). Dispose releases the lease only. |

`RetrievedArticle.TryDetach` transfers the `byte[]`. After `Retained`, the provider session may already have been released (Phase 4). Callers cannot dispose authority storage by disposing a lease.

### Identity

`ArticleIdentity.FromExactMessageId`:

1. Exact Message-ID string (no trim, case-fold, bracket change, or Unicode normalize).
2. ASCII bytes (`Encoding.ASCII.GetBytes`).
3. MD5.
4. Lowercase hex (32 characters).

Known vectors: `<12345@example.invalid>` → `30edc94157aa16fe644a45a1f1ffe160`; `<abc@example.invalid>` → `de438dc83d64b1fa9206cf4da9eed5cc`.

The identity is stored on the entry. It is not recomputed on lookup.

### Cache URI

`CacheArticleUri.Create(fqdn, bindPort, identity)` → `cache://{Fqdn}:{BindPort}/{md5}`.

FQDN is `BackFillerRuntimeOptions.Fqdn`. Port is `BindPort`. MD5 is not URL-encoded. This is the only formatter; the future listener must use it.

### TTL / sweep

- Expiry = insertion UTC + `ArticleRetention.RetentionTtl` (default 60 s).
- **Lookup checks expiry.** An expired entry is not returned (`Expired`), then unindexed.
- `ArticleRetentionSweepService` (`BackgroundService`) sweeps at `SweepInterval` (default 1 s). First sweep runs immediately; one sweep at a time.
- Sweep walks insertion order and stops at the first not-yet-expired entry (uniform TTL ⇒ FIFO).
- Each entry has a generation. After expiry removal, a later insert of the same Message-ID is a new generation; a stale sweep cannot delete it because insert/sweep share the authority gate.

Hosted-service order: RabbitMQ → NNTP registry → sweep → Article Work consumers. Stop is reverse: consumers drain, then sweep `BeginShutdown`.

### Memory accounting

Only destuffed retained payload bytes are counted.

- Retain: `retainedBytes += size`, `physicalCount++`
- Physical dispose: subtract size (floor at 0), `physicalCount--`
- Rejected insert does not change totals
- Duplicate does not add
- Logically removed but still leased entries keep their bytes until the last lease releases

### Duplicate identity

Old first-wins: same exact Message-ID → `AlreadyPresent`. Incoming payload is not taken. Existing URI remains. Article Work treats this as Success (article is available).

Same MD5 / different Message-ID → `Md5Collision` (hard reject). Not a replacement.

There is no replace-in-place path, so an expired sweep cannot remove a newer generation of the same key.

### Capacity

Uses Phase 1 `BackFiller:ArticleRetention` only (default 4 GiB, 80% physical-memory ceiling already validated).

| Condition | Result |
|---|---|
| Single payload > total capacity | `PayloadExceedsCapacity`. No eviction. |
| Remaining capacity insufficient | Expire eligible first, then FIFO-evict oldest until the new article fits or the set is empty. |
| Still insufficient | `CapacityUnavailable`. |

This is insertion-order pressure eviction (old worker), not LRU. An enormous article that exceeds the **total** cap does not evict others.

### Lookup

`TryGetByMessageId` / `TryGetByMd5` → `Found` + lease, `Missing`, or `Expired`.

The lease exposes `ReadOnlyMemory<byte>` and the cache URI. One caller disposing a lease cannot invalidate another caller's lease or the authority payload.

### Shutdown

`BeginShutdown` closes admission (`ShuttingDown`). Existing entries remain until expiry, eviction, or `DisposeAsync`, which force-releases remaining physical storage.

### Article Work integration

`ProviderArticleWorkHandler` detaches `RetrievedArticle` and calls `Retain`.

| Retention | Article Work | Phase 5 settlement |
|---|---|---|
| `Retained` / `AlreadyPresent` | Success + `CacheUri` | NACK `requeue=true`, **no** publish, **no** ACK (`CompletesSuccessPublication` remains false) |
| Capacity / collision / invalid / shutdown | `RetentionRejected` | NACK `requeue=true`, no publish. Not `ArticleNotFound`, not `ProviderFailure` |

`RetentionRejected` is a new distinguishable outcome. Temporary Phase 5 settlement matches the old sink (retention failure requeues). Phase 6 implements Success publish/confirm/ACK; `RetentionRejected` remains retryable with no terminal response.

### Explicit deferral

Phase 6 implements real RPC publish, publisher confirms, and Success ACK. Still not implemented: Transit completion channels, Listener protocol, ACME, Cloudflare, MySQL accounts, control-plane capacity.

### Phase 5 discrepancies

1. **Lookup TTL.** Old read-lease acquisition did not check TTL; expired-but-unswept entries remained readable. Phase 5 lookup rejects expired entries. Documented tightening.
2. **Transit/Listener completion.** Old removal also happened when both consumers completed. Phase 5 does not implement those channels. TTL, FIFO pressure eviction, and dispose are the removal paths.
3. **FQDN property name.** Old URI used `CanonicalBackFillerFqdn`. Phase 1 runtime uses `Fqdn` (same validated identity).
4. **Success ACK.** Old ACK required retain + confirmed publish. Phase 5 retains and produces a URI but still does not publish or ACK. Phase 6 closes that path.
5. **Expire-before-evict.** Old `RecoverCapacity` only pressure-evicted insertion order. Phase 5 expires eligible entries first, then evicts. With uniform TTL this matches oldest-first.

## Phase 6 — Article Work response publication and confirmed settlement

Implemented in `src/VectorNNTP.BackFiller/ArticleWork` and `src/VectorNNTP.BackFiller/RabbitMq`. This phase closes the RabbitMQ Article Work success path and the terminal-failure response path.

### Ownership

| Resource | Owner |
|---|---|
| TCP connection | `BackFillerRabbitMqService` (Phase 2; sole recovery owner) |
| Consume / ACK / NACK channel | `ArticleWorkConsumerSession` |
| Confirm-enabled publish channel | `ArticleWorkResponsePublisher` |
| Retained article bytes | `ArticleRetentionAuthority` (unchanged) |
| Settlement lease | `ArticleWorkDeliveryPipeline` |

Shared connection → consumer-owned channel → publisher-owned channel. The publisher never ACK/NACKs a delivery. The consumer channel never publishes a response.

Hosted-service order: RabbitMQ → NNTP registry → retention sweep → **response publisher** → Article Work consumers. Stop is reverse: consumers drain, then the publisher stops admitting and closes its channel.

Publisher states: `Created → Starting → Running → Retiring → Stopped`. Startup fails if a publish channel cannot be opened on the current generation.

### Publisher confirms

The production publish channel is created with RabbitMQ.Client 7.2 `CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)`. With tracking enabled, `BasicPublishAsync` waits for a broker confirmation and throws `PublishException` on nack or `basic.return`.

**Publish completion ≠ publisher confirmation.** Completing a `BasicPublishAsync` that was not opened with confirmation tracking is not a confirmation.

**Publisher confirmation ≠ permission to ACK.** ACK requires a confirmed response **and** a still-current original delivery settlement context (same consumer channel, same generation, channel open).

Confirm timeout is `BackFiller:RabbitMQ:PublishConfirmTimeoutSeconds` (linked with caller and publisher shutdown cancellation). A timeout, nack, return, channel close, or lost generation is an unconfirmed publication.

### Response construction

`ArticleWorkResponseWireProtocol.SerializeV1` writes compact UTF-8 JSON with exact property names: `version`, `requestId`, `messageId`, `backbone`, `outcome`, plus `uri` (Success only) or `error` (terminal failure only). Article bytes are never included.

Success `uri` is the Phase 5 retention result. The publisher does not reconstruct FQDN/port. Serialization rejects a Success URI that is not `cache://…/{md5}` bound to the exact Message-ID.

### RequestId / CorrelationId / ReplyTo

| Identity | Source | Use |
|---|---|---|
| JSON `requestId` | Validated `ArticleWorkItem.Request.RequestId` | Logical work identity. Never synthesized. Never a new UUID. |
| AMQP `RequestId` header | Same logical GUID when present | Required by locked NNTPD `ArticleWorkRpcResponseRouter`. Omitted when InvalidRequest could not parse a request id. |
| AMQP `CorrelationId` | Request `CorrelationId` | Echoed on the response. Not `RequestId`. Not the delivery tag. |
| AMQP `MessageId` | Fresh UUID per publication attempt | Transport publication identity only. |
| `ReplyTo` | Validated request `ReplyTo` | Default-exchange routing key. Never hard-coded. |
| Connection generation | Phase 2 handle | Fencing. Not a protocol field. |

NNTPD publishes requests with `ContentType=application/json` and `Expiration=1000`. Responses use the same content type and expiration so the locked NNTPD consume path can accept them.

### Success settlement ordering

1. Article retrieved
2. Article retained (`cache://` URI from the authority)
3. Response JSON constructed
4. Response published to `ReplyTo` on the publisher channel
5. Publisher confirmation succeeds
6. Re-check original consumer generation/channel currentness
7. ACK the original delivery on the **consumer** channel

Never reverse 5 and 6. Never ACK before confirmation. If currentness fails after confirmation, do not ACK on a replacement channel; leave the original delivery for broker recovery.

The retained article stays in the authority after confirm. Listener/Transit removal is deferred.

### Terminal failure settlement ordering

| Outcome | Response | After confirmed publish | If publish/confirm fails |
|---|---|---|---|
| `ArticleNotFound` | Yes (`error`, no `uri`) | NACK `requeue=false` | NACK `requeue=true` |
| `InvalidArticle` | Yes | NACK `requeue=false` | NACK `requeue=true` |
| `InvalidRequest` | Yes when `CorrelationId` and `ReplyTo` are present | NACK `requeue=false` | NACK `requeue=true` |
| `ProviderFailure` / `Cancelled` / `UnexpectedFailure` / `RetentionRejected` | No | NACK `requeue=true` | n/a |

InvalidRequest identity rules are unchanged from Phase 3: no synthesized identity. If reply coordinates are missing, no response is published and the delivery is still NACK `requeue=false`.

### Generation fencing

- Capture generation when opening the publisher channel.
- A stale publisher generation cannot replace or dispose a newer publisher channel.
- If the generation is not current before publish, publication fails.
- If the generation becomes invalid during publish/confirm, publication is not treated as confirmed.
- The original delivery is ACKed only when confirmation succeeded **and** the original consumer channel/generation is still the settlement context.

Never: old generation publishes → connection replaced → old publish reports success → ACK through the current generation.

### Publication failure and redelivery

There is no unbounded publish-retry loop inside one delivery. One `ProcessAsync` attempts one publish/confirm. Failure → retryable NACK `requeue=true`. A later redelivery may publish another response (at-least-once). This phase does not implement distributed deduplication.

### Shutdown

1. Consumers stop admitting new Article Work (existing Phase 3 drain).
2. Publisher stops admitting new publications (`Retiring`) and cancels in-flight confirm waits.
3. Unconfirmed publications are not ACKed.
4. Publisher channel is closed.
5. Existing host `ShutdownTimeout` / `GracePeriod` bounds the wait. Confirms are not awaited indefinitely. Shutdown never forces an ACK.

### Duplicate / redelivery

Exactly one ACK or NACK per settlement lease. The publisher cannot settle. A crash after confirm and before ACK may redeliver and publish a second response.

### Explicit deferral

Phase 7 implements Listener/TLS serving. Still not implemented: Transit `TAKETHIS`, ACME, Cloudflare, MySQL account polling (Phase 8), control-plane capacity.

### Phase 6 discrepancies

1. **AMQP `RequestId` header.** Locked NNTPD `ArticleWorkRpcResponseRouter` ignores responses whose AMQP `RequestId` is missing or does not match the pending logical request. Old BackFiller publisher omitted that header. Protocol MD is silent. Phase 6 includes the header when JSON `requestId` exists (NNTPD wire contract).
2. **AMQP `Expiration=1000`.** Locked NNTPD architecture / `ArticleWorkRpcAmqp` uses this on requests and accepted responses. Old publisher and protocol MD omit Expiration. Phase 6 sets `1000` for NNTPD interop.
3. **InvalidRequest without `requestId`.** Protocol still publishes when replyable. NNTPD will ignore a response that has no matching `RequestId` header. Settlement remains NACK `requeue=false` after confirm. Identities are not synthesized to make NNTPD accept the response.
4. **`RetentionRejected`.** Still no terminal response (retryable). Not in the protocol outcome enum.
5. **Confirm API.** Old publisher also used `BasicPublishAsync` on a confirm-tracking channel. Phase 6 keeps that RabbitMQ.Client 7.2 behavior behind `IBackFillerRabbitMqPublishChannel.PublishConfirmedAsync` so tests can fail confirm independently of enqueue.

## Phase 7 — cache Listener / TLS article serving

Implemented in `src/VectorNNTP.BackFiller/Listener`. This phase serves the Phase 5 retained payload advertised by the Phase 6 `cache://{Fqdn}:{BindPort}/{md5}` Success URI.

### Ownership

| Resource | Owner |
|---|---|
| TCP listen sockets and accepted connections | `CacheListenerService` |
| Per-connection parse/write/ReceiptAck | `CacheListenerSession` |
| TLS server certificate (already provisioned PFX) | `ICacheListenerCertificateSource` |
| Retained article bytes | `ArticleRetentionAuthority` (unchanged) |
| Read lease during Found + ReceiptAck | `CacheListenerRetentionHandler` |

The listener does not own RabbitMQ, NNTP providers, Article Work settlement, or response publishing.

Hosted-service order: RabbitMQ → NNTP registry → sweep → **cache Listener** → response publisher → Article Work consumers. Stop is reverse: the listener stops accept and drains connections while retention is still available.

Listener states: `Created → Starting → Running → Retiring → Stopped`. Startup fails if no TLS certificate is available or no endpoint can be bound.

### Exact protocol

This is a **binary framed** protocol (not CRLF / NNTP). Every frame is a 16-byte big-endian header plus payload:

`version(1) opcode(1) headerLength(2) requestId(4) payloadLength(4) reserved(4)`

| Opcode | Direction | Payload |
|---|---|---|
| `GetRequest` `0x01` | client | 32 lowercase ASCII hex MD5 bytes |
| `GetResponseFound` `0x11` | server | exact retained article bytes |
| `GetResponseNotFound` `0x12` | server | `0x00` |
| `GetResponseError` `0x13` | server | big-endian uint16 error code |
| `GetReceiptAck` `0x21` | client | empty |

`RequestId` must be non-zero. `headerLength` must be 16. `reserved` must be 0. Version must be `0x01`.

Fatal inbound frames (forced session close): unsupported version, invalid header length, invalid frame length. Other parse failures emit `GetResponseError` and continue.

Per-connection bounds (same as the old worker): 64 outstanding RequestIds, 8 concurrent handlers, 64 outbound responses. Phase 1 `MaxQueuedFoundPayloadBytes` and `ParserAccumulationMaxBytes` are enforced. Exceeding queued Found bytes returns `InternalError`.

### Receipt acknowledgement and leases

1. Valid GetRequest → `TryGetByMd5` (existing Phase 5 helper; no second MD5 implementation).
2. Found → hold `ArticleLookupLease` so payload memory stays valid.
3. Write Found header then payload with no mutation.
4. Wait for `GetReceiptAck` with the same RequestId, bounded by `AwaitingReceiptAckTimeout`.
5. Release the lease. **Do not evict the retained article.**

Missing and expired identities both return `GetResponseNotFound`. An early ReceiptAck is remembered until the Found write completes. ReceiptAck timeout or session shutdown releases the lease without removing the article.

### TLS

TLS is mandatory (old listener is TLS-only). `Tls12 | Tls13`, no client certificate, no revocation check. Handshake bounded by `TlsHandshakeTimeout`. I/O no-progress bounded by `IoProgressTimeout` (this is the stall timeout while assembling a frame; there is no separate Phase 1 parser-timeout setting).

Certificate boundary: `DirectoryCacheListenerCertificateSource` loads `{CertificateDirectory}/backfiller-listener.pfx` with `LetsEncrypt.PfxExportPassword`. It does not issue or renew certificates. ACME remains deferred. Startup fails if the PFX is missing or has no private key. Tests inject `ICacheListenerCertificateSource`.

Private keys are loaded with `UserKeySet | Exportable` on Windows (Schannel cannot serve `EphemeralKeySet`) and `EphemeralKeySet | Exportable` elsewhere. The listener prefers `SslStreamCertificateContext` and falls back to `ServerCertificate` when context creation fails, matching the old worker's self-signed path.

### Bind addresses

Empty tokens → IPv4 `Any` + IPv6 `Any`. Wildcard tokens (`*`, `+`, `0.0.0.0`, `::`) do the same. Explicit addresses bind only that family. IPv6 sockets are IPv6-only (`DualMode = false`). If an implicit wildcard family is unsupported by the OS, that family is skipped; an explicit address bind failure fails startup.

`MaxActiveConnections` is enforced after accept: excess sockets are closed immediately.

### Shutdown

Stop accept, cancel in-flight handshake/session tokens, drain admitted connections, close listen sockets, dispose the loaded certificate. Repeated dispose is safe. Shutdown does not remove retained articles.

### Explicit deferral

Not implemented: Transit `TAKETHIS`, ACME issuance/renewal, Cloudflare DNS, MySQL accounts (Phase 8), control-plane capacity, Listener-completion eviction of retained articles.

### Phase 7 discrepancies

1. **No CRLF grammar.** The user prompt mentioned CRLF. The old implementation and tests define a binary 16-byte header protocol. Phase 7 follows that authoritative wire contract.
2. **No `MarkListenerCompleted` eviction.** Old handler marked listener completion after Found write + ReceiptAck so retention could drop the article when Transit had also completed. Phase 5 has no completion channels. Phase 7 releases the read lease only.
3. **Certificate source.** Old worker used ACME-backed `BackFillerCertificateState`. Phase 7 loads an already-provisioned PFX or a test-injected certificate. No accept-all TLS on the server; clients in tests disable validation.
4. **BackgroundService.** Old listener was a `BackgroundService`. Phase 7 is an `IHostedService` with the same local state machine used by RabbitMQ / consumers / publisher. Not NNTPD `ApplicationLifecycle`.

## Phase 8 — MySQL provider/account control plane

Implemented in `src/VectorNNTP.BackFiller/Accounts` plus registry snapshot apply in `Nntp/NntpProviderRegistry`. MySQL is control-plane state only. Article Work still resolves providers through `NntpProviderRegistry` / `IBackFillerProviderCatalog` and never queries GrabberDB.

Hosted-service order is now: RabbitMQ → **provider-account control plane** → NNTP registry → sweep → cache Listener → response publisher → Article Work consumers.

### Schema and query

The old worker table contract is used unchanged. Phase 8 does not create or migrate the table.

```sql
SELECT
  entryid,
  backbone,
  hostname,
  keepalive,
  maxconnections,
  password,
  port,
  serverid,
  username,
  usessl
FROM nntpbackfilleraccounts
WHERE serverid = @ServerId;
```

`serverid` is the validated `BackFiller:ServerId` (0–99), sent as an unsigned byte. Connections open only for the duration of a query. Command timeout is `BackFiller:Accounts:CommandTimeoutSeconds` (default 15, range 1–120). `MySqlException` is wrapped as `InvalidOperationException("Provider account query failed against GrabberDB.")` so the connection string is not copied into the exception message.

`keepalive` is parsed and stored on the row and is otherwise unused (DATE remains deferred).

### Mapping onto Phase 4 providers

Each accepted row becomes one `BackFillerProviderDefinition`:

| Column / rule | Provider field |
|---|---|
| `backbone` matched ignore-case to the Phase 2/3 canonical list | `Backbone` (canonical spelling) |
| `hostname` (required, trimmed) | `Host` |
| `port` 1–65535 | `Port` |
| `usessl` exactly `y` / `n` | `UseTls` |
| `username` (required, trimmed) | `Username` |
| `password` (required, may be empty) | `Password` |
| no min-session column | `MinSessions = 0` (lazy) |
| `maxconnections` ≥ 1 | `MaxSessions` |

Unknown backbones, duplicates (first valid row wins), and invalid rows are rejected and logged. They do not fail the snapshot and do not create runtime providers. An empty accepted set is a valid snapshot.

### Snapshot and polling

`ProviderConfigurationCatalog` publishes a complete list atomically (`volatile` replace). Readers see the previous complete set or the new complete set.

`ProviderAccountConfigurationService` owns one polling loop:

1. `StartAsync` performs a **required** initial refresh. Query failure fails startup. Empty providers succeed.
2. After startup, the loop `Delay`s `BackFiller:Accounts:RefreshIntervalSeconds` (default **60**, the old `ControlPlaneService` cadence; range 5–3600), then refreshes. The delay happens first, so the initial load is not immediately repeated.
3. `Interlocked` prevents overlapping refreshes. A refresh that overruns the interval is not started again until it finishes.
4. Change detection uses record equality on the published `BackFillerProviderDefinition` set (host, port, TLS, credentials, min/max, presence). Unchanged polls keep the existing snapshot and pools.
5. A later query/refresh failure logs a warning and **retains the last known-good snapshot**. Database unavailable is not treated as “all providers removed”. The next successful refresh publishes atomically and logs recovery.

### Ownership

| Resource | Owner |
|---|---|
| MySQL poll, current snapshot, change publication | `ProviderAccountConfigurationService` |
| Row query | `IProviderAccountSource` / `MySqlProviderAccountSource` |
| Published provider set | `ProviderConfigurationCatalog` (`IBackFillerProviderCatalog`) |
| Session pools, lease, reuse, retirement, warmup | existing `NntpSessionPool` / `NntpProviderRegistry` |

The control plane does not own RabbitMQ, Article Work settlement, retention, the Cache Listener, or NNTP protocol state. It does not implement a second session pool.

`NntpProviderRegistry.ApplySnapshotAsync` publishes the catalog and reconciles pools under the same lock: unchanged providers keep their pool; added/changed providers get a new pool; removed/replaced pools are drained via `DrainAndDisposeAsync`. New leases use the new pool. An already-leased session stays with the retired pool until the lease returns. Drain waits for outstanding leases unless the refresh token is cancelled, in which case remaining live sessions are retired and the refresh throws.

Removed providers disappear from the catalog, so `TryGetPool` rejects new leases. Reappearance creates a new pool without a process restart.

### Observability

Structured events 5600–5609: snapshot loaded, provider added/removed/changed, refresh failed/recovered, rejected row, unchanged snapshot, start/stop. Logs include backbone, host, port, TLS, and session bounds. They never include passwords, connection strings, tokens, article payloads, or full account rows.

### Explicit deferral

Not implemented: table/database provisioning, DATE keepalive from `keepalive`, Transit `TAKETHIS`, ACME, Cloudflare, control-plane capacity.

### Phase 8 discrepancies

1. **No `BackgroundService`.** Same local `IHostedService` pattern as RabbitMQ / registry / Listener. Not NNTPD `ApplicationLifecycle`. Not a copy of the old `ControlPlaneService` / `NntpAccountSnapshotStartupInitializer` types.
2. **MinSessions.** The table has no min-session column. Phase 8 always publishes `MinSessions = 0`. A later definition that differs only in `MinSessions` (tests / future source) still replaces the pool.
3. **Rejected rows do not fail the snapshot.** The old worker also skipped unusable account rows rather than refusing the whole process after a successful query. Startup still fails when the **query itself** fails.
4. **Last-known-good after startup.** A temporary GrabberDB outage keeps the last successful providers. That is the Phase 8 policy; it is not “fail-open to an invented catalog”.
5. **No live MySQL in the test suite.** Tests use `IProviderAccountSource` fakes. There is no Testcontainers convention in this repository for GrabberDB.

## Phase 9 — end-to-end integration and concurrency hardening

Phase 9 does not add a feature. It exercises the seams between Phases 2–8 with deterministic fakes at RabbitMQ, MySQL, and upstream NNTP, using the real production pipeline (`ProviderAccountConfigurationService` → catalog → `NntpProviderRegistry` → `NntpArticleRetriever` → `ProviderArticleWorkHandler` → `ArticleRetentionAuthority` → `ArticleWorkDeliveryPipeline` → `ArticleWorkResponsePublisher` → `CacheListenerRetentionHandler` / `CacheListenerSession`).

### Architecture verified

```
NNTPD Article Work
  → RabbitMQ consume (generation-fenced)
  → provider snapshot (MySQL control plane, not request path)
  → NntpSessionPool lease
  → upstream ARTICLE
  → retain exact destuffed bytes
  → cache:// URI
  → publish + publisher confirm
  → re-check original consumer generation/channel
  → ACK
NNTPD cache:// fetch
  → Listener TLS / binary v1
  → MD5 lookup lease
  → Found exact bytes
  → ReceiptAck releases the lookup lease only
```

Transit `TAKETHIS` remains out of scope. NNTPD is the transit path.

### Settlement ordering

Required success order is unchanged: retrieve → retain → serialize → publish → confirm → re-check settlement context → ACK.

- Confirm success + current original channel → ACK.
- Publish or confirm failure → NACK `requeue=true`, never ACK.
- Consumer generation stale or original channel closed after confirm → no settlement.
- Shutdown during confirm → no ACK; retryable NACK if the original context is still current.
- Confirm success then ACK RPC failure: `ArticleWorkSettlementLease` marks the lease settled, swallows the ACK exception, and records no ACK. The pipeline still returns the work outcome (`Success`). That is not permission to treat the delivery as settled. Redelivery is a new lease (at-least-once).

The publisher never ACK/NACKs the original delivery.

### Retention lifecycle

Retention is independent of publication and Listener receipt. ReceiptAck / connection close release the lookup lease and do not evict. TTL/sweep unindexes an expired entry; a held lease keeps the payload bytes until release, then physical dispose reclaims accounting. Subsequent lookups follow Phase 5 (`Found` / `Expired` / `Missing`). Same exact Message-ID is first-wins (`AlreadyPresent`). Distinct Message-IDs are distinct identities. A true MD5 collision of two different Message-IDs is rejected (`Md5Collision`); this suite cannot construct such inputs.

### Provider replacement

A refresh publishes the new snapshot and new pool under the registry lock. An in-flight Article Work lease stays on the retired pool until ARTICLE completes. New work uses the new pool. Removal stops `TryGetPool`. Reappearance creates a new pool. A MySQL refresh failure keeps last-known-good. Unrelated backbones are not replaced.

### Shutdown

Hosted stop order is reverse of start (consumers first). `ArticleWorkConsumerSession` drains admitted work with `CancellationToken.None`; `FinishActiveArticles` / `DrainQueuedWork` are validated configuration and are not yet wired as a second cancellation policy. Pipeline-level cancellation (tests and explicit tokens) yields `Cancelled`, no publish, NACK requeue. No ACK is issued because shutdown was requested.

### Ownership (unchanged)

| Resource | Owner |
|---|---|
| RabbitMQ connection | `BackFillerRabbitMqService` |
| Consume channels | `ArticleWorkConsumerSession` |
| Publish channel | `ArticleWorkResponsePublisher` |
| Provider snapshot | `ProviderAccountConfigurationService` / catalog |
| Session pools | `NntpProviderRegistry` |
| NNTP session | `NntpSessionLease` while leased, then pool |
| Retained bytes | `ArticleRetentionAuthority` |
| Lookup lease | `ArticleLookupLease` / Listener `RequestContext` |
| Listener sockets | `CacheListenerService` |
| Settlement | `ArticleWorkSettlementLease` (exactly once per delivery) |

### Explicit deferral

Transit `TAKETHIS`, ACME, Cloudflare, distributed cache/dedup, wiring `FinishActiveArticles` into consumer cancellation, and live MySQL/RabbitMQ/NNTP infrastructure tests.


