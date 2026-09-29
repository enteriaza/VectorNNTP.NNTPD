# VectorNNTP.NNTPD — Configuration

Configuration binds from the application-neutral root (shared Cloudflare secrets and the shared ACME account email), the `Nntpd` section (NNTPD-specific settings including bind addresses and ports, Cloudflare zone id, DNS suffix, ACME directory, renewal threshold, and state directory), the top-level `Redis` section, the top-level `RabbitMQ` section, `ConnectionStrings:NntpDB`, the top-level `NntpDb` application options, the top-level `Transit` peer dictionary, the top-level `Control` PGP-authority catalogue, the top-level `Moderation` moderator catalogue, and the top-level `Email` outbound-mail options. Sources include `appsettings.json`, environment variables, and command-line arguments via the Generic Host.

Shared ACME, Cloudflare, and bind-address **implementations** are owned by `VectorNNTP.Common`. Shared implementation does not imply shared/global configuration. VectorNNTP.NNTPD binds bind addresses and ports, Cloudflare zone id, DNS suffix, ACME directory URL, renewal threshold, and state directory from the `Nntpd` section; root-level copies of those keys are not applied. The ACME account email is shared Common configuration (`VECTOR__ACMEACCOUNT` / `ACMEACCOUNT`) and is not application-section configuration. `VECTOR__` secrets (`VECTOR__CLOUDFLAREAPIKEY`, `VECTOR__ACMECERTIFICATEPASSWORD`, `VECTOR__ACMEACCOUNT`) overlay from the configuration root. VectorNNTP.BackFiller owns bind addresses, TLS listen port, Cloudflare zone id, ACME directory, DNS-suffix, and ServerId values under the `BackFiller` section (`BackFiller:BindAddress`, `BackFiller:BindPortTls`, `BackFiller:CloudFlareZoneId`, `BackFiller:DnsSuffix`, `BackFiller:ServerId`) and adapts them into Common; it does not read those keys from the root. There is no `BackFiller:BindPort`. BackFiller still uses root `VECTOR__*` secrets for the Cloudflare API key, the ACME PKCS#12 password, and the ACME account email, plus `VECTOR__RABBITMQ__USERNAME` and the shared `ConnectionStrings__NntpDB` Generic Host mapping. Values are case-sensitive and are not transformed. There is no `nntpd__`, `backfiller__`, or unprefixed `__` alias for those secrets. There is no NNTPD-specific or BackFiller-specific ACME email or password variable.

NNTPD-specific settings stay under section `Nntpd` and use `NNTPD__*` (`NNTPD__SERVERID`, `NNTPD__XTRACEKEY`, `NNTPD__XTRACEPREVIOUSKEY`, `NNTPD__NEWSMASTERUSER`, `NNTPD__NEWSMASTERPASSWORD`). BackFiller application settings stay under section `BackFiller`. StorageServer application settings stay under section `StorageServer` (`StorageServer:ServerId`, `StorageServer:BindAddress`, `StorageServer:BindPort`, `StorageServer:BindPortTls`, `StorageServer:DnsSuffix`, `StorageServer:CloudFlareZoneId`, `StorageServer:LogDir`, `StorageServer:CacheDir`, `StorageServer:Storage:ControlDir`). StorageServer is TLS-only VATP (`BindPortTls` required `1–65535`); `BindPort` defaults to `0` and is not listened on. `ServerId` binds from each application's section only (required integer `1–255` via shared `ServerIdRules`; no default). There is no `BackFiller:Name` / `StorageServer:Name`. The canonical BackFiller FQDN is `backfiller{ServerId:00}.{DnsSuffix}`; StorageServer is `cache{ServerId:00}.{DnsSuffix}`; prefixes are fixed application identity, not configuration. `BackFiller:BackFillerAccountRefreshIntervalSeconds` (default 60, range 5–3600) is how often BackFiller polls MySQL for provider/account rows. It is not the per-account NNTP DATE keepalive; that value is the MySQL `nntpbackfilleraccounts.keepalive` column. There is no `BackFiller:Accounts` section and no dedicated environment-variable mapping for the refresh interval.

Validation runs at startup through `IValidateOptions<NntpdOptions>` and data annotations (`ValidateOnStart`). **Validation does not bind sockets and does not call Cloudflare APIs.** The `Control` catalogue is optional: an omitted or empty section does not prevent startup and is not a runtime dependency. `Moderation` is also optional when empty; malformed mappings fail startup. Missing moderator routes reject moderated POST — they do not bypass moderation.

## Settings

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `ApplicationName` | string | `VectorNNTP.NNTPD` | no | Display name for logs and Windows Service metadata |
| `GracefulShutdownTimeout` | `TimeSpan` | `00:00:30` | no | Overall wall-clock bound for the reverse-order application-service stop sequence (also drives host `ShutdownTimeout`). Manager awaits each stop; ignore-cancel services can block until host/supervisor kill. |
| `StartupTimeout` | `TimeSpan?` | `null` | no | Optional startup bound; `null` means host cancellation only |
| `StopHostOnUnexpectedServiceTermination` | bool | `true` | no | Request host stop when a service fails while `Running` |
| `Systemd:*` | object | see existing docs | no | systemd notify / watchdog options |
| `Nntpd:BindAddress` | string array | omitted → `["*"]` after normalize | no | Local listen addresses (see below). Root-level `BindAddress` is not consumed. |
| `Nntpd:BindPort` | int | `119` | no | Cleartext NNTP TCP port (`1–65535`). Root-level `BindPort` is not consumed. |
| `Nntpd:BindPortTls` | int | `0` | no | TLS NNTP TCP port; `0` / unset disables TLS (`1–65535` enables). When enabled, ACME is required. Root-level `BindPortTls` is not consumed. |
| `AllowCleartextAuth` | bool | `true` | no | Permit `AUTHINFO USER/PASS` and AUTHINFO SASL when the connection is not TLS-protected (see below) |
| `AcmeDirectoryUrl` | string | Let's Encrypt **staging** directory | no | Absolute HTTPS ACME directory URL (authoritative; never silently switched to production) |
| `ACMEACCOUNT` / `VECTOR__ACMEACCOUNT` | string | _(none)_ | **yes when TLS enabled** | Shared ACME account contact email used by every Common ACME client. Not `Nntpd:AcmeEmail` or `BackFiller:AcmeEmail`. Ignored when `BindPortTls` is `0`. |
| `AcmeStateDir` | string | `certs/` | no | Filesystem directory for ACME account + certificate DER state. Relative paths resolve with Common `ResolveAcmeStateDir` (delegates to `ApplicationLocalPath.ResolveApplicationLocalPath`) against `AppContext.BaseDirectory` (the binary directory), not the process working directory, source tree, or IDE content root. |
| `LogDir` | string | `logs/` | no | Filesystem directory for Serilog daily rolling application logs, the dedicated Serilog `news` file, and the dedicated Serilog Path-survey (`inpaths`) file. Relative paths resolve with Common `ApplicationLocalPath.ResolveApplicationLocalPath` against `AppContext.BaseDirectory`. Application File `path`, `Serilog:News:path`, and `Serilog:Inpaths:path` in `appsettings.json` are placeholders; startup overwrites them from this setting. The INN news line format and the Path-survey `Path: ` line format are not configurable. |
| `AcmeRenewalThresholdDays` | int | `30` | no | Renew when `NotAfter - threshold` is reached (`1–90`) |
| `AcmeCertificatePassword` | string | _(none)_ | **yes when TLS enabled** (secret) | Password protecting the TLS server PKCS#12/PFX |
| `CloudFlareApiKey` | string | _(none)_ | **yes** (secret) | Cloudflare API key for DNS integration |
| `Nntpd:CloudFlareZoneId` | string | _(none)_ | **yes** | Cloudflare zone identifier. Root-level `CloudFlareZoneId` is not consumed. |
| `CloudFlareOperationTimeout` | duration | `00:02:00` | no | Wall-clock budget for one reconcile or cleanup operation (shared by HTTP 429 retries and reconciler attempt backoffs) |
| `Nntpd:DnsSuffix` | string | `usenet.ninja` | no | DNS suffix used to generate the FQDN. Root-level `DnsSuffix` is not consumed. |
| `ServerId` | int | _(none)_ | **yes** | Server identity `1–255`; no silent default |
| `ProxyHosts` | string array | `[]` (empty) | no | Trusted HAProxy PROXY-protocol peer IPs (see below) |
| `Fqdn` | _(generated)_ | `nntpd{ServerId:00}.{DnsSuffix}` | n/a | **Not configurable** |
| `HistoryTime` | `TimeSpan` | `02:00:00` | no | HistoryDB retention for local memory and Redis key TTL (`1s`–`7d`) |
| `IdleTime` | int (seconds) | `300` | no | Disconnect an established NNTP session after this many seconds with no executed NNTP command (`1`–`86400`). `0` is invalid (not disabled). Resets when a command is accepted; in-flight CHECK/TAKETHIS/POST (including article receive and validation) keep the session non-idle. Not TCP/TLS/socket receive idle. |
| `MaxArticleSize` | int | `5242880` (5 MiB) | no | Maximum destuffed POST article size in bytes (`1`–`104857600`). Enforced during streaming receive (headers + blank separator + body; terminator excluded; stuffing dots are not counted). Exceeding the limit ends reception and returns `441 Posting failed`. Distinct from `ArticleIngestion:MaxArticleBytes` (IHAVE/TAKETHIS). |
| `MailComplaintsTo` | string | `abuse@usenet.ninja` | no | Mailbox emitted as `mail-complaints-to` on server-generated POST `Injection-Info`. Must be a plausible mailbox. Client `Injection-Info` is discarded. |
| `Top1000` | string array | `[]` (empty) | no | Path-survey (ninpaths) report recipients. Missing, null, or empty disables internal ninpaths. Whitespace-only entries are ignored. Non-whitespace entries must be mailboxes. No separate enable flag and no environment-variable overlay. See below. |
| `XTraceKey` | string | _(none)_ | **yes** (secret) | 32-byte AES-256 key that protects POST `X-Trace` (64 hex characters or Base64). Supply via `NNTPD__XTRACEKEY` or secrets. Never commit. |
| `XTracePreviousKey` | string | _(none)_ | no (secret) | Optional previous AES-256 key retained for one-generation decrypt after rotation. Supply via `NNTPD__XTRACEPREVIOUSKEY`. |
| `NewsmasterUser` | string | _(none)_ | no | AUTHINFO username that may POST a well-formed `Control: cancel <message-id>` article. When set, `NewsmasterPassword` is required. |
| `NewsmasterPassword` | string | _(none)_ | no (secret) | AUTHINFO password for `NewsmasterUser`. Supply via `NNTPD__NEWSMASTERPASSWORD` or secrets. Never commit. |
| `TransitQueueMemoryLimit` | long | `1073741824` (1 GiB) | no | Transit article-queue payload memory budget in bytes (`1`–`9223372036854775807`) |
| `ArticleIngestion:IncomingDirectory` | string | `spool/incoming` | no | Directory for accepted TAKETHIS articles |
| `ArticleIngestion:QueueCapacity` | int | `256` | no | Unused leftover article-count setting (`1–100000`). Not an admission bound. |
| `ArticleIngestion:MaxArticleBytes` | int | `4194304` (4 MiB) | no | Max destuffed IHAVE/TAKETHIS article size (`1–104857600`). Not the POST limit. |
| `ArticleIngestion:MinWorkers` | int | `2` | no | Minimum concurrent article ingestion workers (`1–512`) |
| `ArticleIngestion:MaxWorkers` | int | `32` | no | Maximum concurrent article ingestion workers (`1–512`; ≥ `MinWorkers`) |
| `ArticleIngestion:MaxPublishConcurrency` | int | `32` | no | Max outstanding OverviewDB publisher confirms / leased publish channels (`1–512`); independent of worker counts |
| `ArticleIngestion:OverviewDbWorkQueueMemoryLimit` | long | `1073741824` (1 GiB) | no | In-process OverviewDB work-queue byte budget (`≥1`). Not durable; not a RabbitMQ queue |
| `ArticleIngestion:OverviewDbMinPublisherWorkers` | int | `2` | no | Minimum OverviewDB RabbitMQ publisher workers (`1–512`); scales independently of article workers |
| `ArticleIngestion:OverviewDbMaxPublisherWorkers` | int | `32` | no | Maximum OverviewDB RabbitMQ publisher workers (`1–512`; ≥ `OverviewDbMinPublisherWorkers`) |
| `ArticleIngestion:OverviewDbPublisherBatchSize` | int | `100` | no | Outstanding OverviewDB RabbitMQ confirmation window (`1–10000`). Publishers stop issuing new publishes when full |
| `ArticleIngestion:OverviewDbPublisherShutdownSeconds` | int | `2` | no | Bounded OverviewDB publisher drain on shutdown (`1–60`). Outstanding confirms may be abandoned afterward |
| `ArticleIngestion:ScaleIntervalSeconds` | int | `2` | no | Shared pressure sample interval for article and OverviewDB publisher pools (`1–3600`) |
| `ArticleIngestion:ScaleUpPressureThreshold` | double | `0.4` | no | Sustained pressure that triggers scale-up (`0–1`; must be greater than scale-down threshold) |
| `ArticleIngestion:ScaleDownPressureThreshold` | double | `0.1` | no | Sustained pressure that triggers scale-down (`0–1`; must be less than scale-up threshold) |
| `ArticleIngestion:ScaleUpConsecutiveIntervals` | int | `2` | no | Consecutive high-pressure samples before +1 worker (`1–100`) |
| `ArticleIngestion:ScaleDownConsecutiveIntervals` | int | `3` | no | Consecutive low-pressure samples before −1 worker (`1–100`) |
| `Nntpd:Transit:StreamOutstandingArticleDepth` | int | `8` | no | Max concurrent outstanding STREAM article TX operations (`4–16`, rejected outside range). Depth gate above shared `WriteArticleAsync`; independent of TX Channel / Pipe / ingestion queue. Not peer authorization. |
| `Nntpd:Transit:WantTrash` | bool | `true` | no | INN `wanttrash`. TAKETHIS/IHAVE only. When `true`, articles whose `Newsgroups:` names are all unknown or RFC 6048 `j` are accepted and treated as junk internally after dequeue. When `false`, unknown/non-carried groups are rejected before enqueue (`437`/`439`) and write a `-` news line. The original `Newsgroups:` header is not rewritten. POST is not subject to this policy. |
| `Nntpd:Transit:LogTrash` | bool | `true` | no | INN `logtrash`. When `true`, accepted-junk events write a `j` line to `{LogDir}/news`. When `false`, junk articles are still accepted; only the junk news line is omitted. Accepted (`+`), rejected (`-`), and moderated (`m`) events are still written. Path-survey (`inpaths`) observations are independent of this setting and are still written for CanonicalV1 queued articles. |
| `Nntpd:PostFilter` | _(removed)_ | n/a | **must be absent** | Leftover section fails startup. Cluster PostFilter policy is MySQL `nntppostfiltercurrent`. See [postfilter.md](postfilter.md). |
| `SpeedTest:MaxBytes` | long | `67108864` (64 MiB) | no | Maximum SPEEDTEST synthetic payload bytes (`1024–1073741824`) |
| `SpeedTest:MaxConcurrent` | int | `2` | no | Maximum concurrent SPEEDTEST operations on this host (`1–8`) |
| `SpeedTest:MaxConcurrentPerPeer` | int | `1` | no | Maximum concurrent SPEEDTEST operations per Transit identifier (`1–4`) |
| `FeedDiagnostics:Enabled` | bool | `false` | no | Temporary real-feed snapshot reporter. Also enabled by `VECTORNNTP_FEED_DIAGNOSTICS=1`. Off by default; not per-article logging. |
| `FeedDiagnostics:IntervalSeconds` | int | `5` | no | Snapshot interval (`1–60`) |
| `FeedDiagnostics:IncludeSessions` | bool | `true` | no | Include compact per-session lines (remote IP/port only; no Message-IDs) |
| `Transit:{identifier}` | object | _(none)_ | no | Named Transit peer (top-level `Transit` dictionary; key is the protocol identifier). |
| `Control:PgpAuthorities` | object | empty catalogue | no | Authoritative Usenet PGP control-authority catalogue (data only; see below). Not a moderator list. |
| `Moderation:Source` | object | _(none)_ | no | Provenance for the imported INN `samples/moderators` URL. Not a runtime authorization source. |
| `Moderation:Moderators` | array | `[]` | no | Leftover static list. Must be empty. Runtime authorization is `nntpmoderators`. |
| `Email:*` | object | disabled | no | Generic outbound email subsystem (SMTP + durable filesystem spool). See below. |
| `ConnectionStrings:NntpDB` | string | _(none)_ | **yes** | Shared NNTPD/BackFiller MySQL connection string (secret; env `ConnectionStrings__NntpDB`; never log) |
| `NntpDb:*` | object | see below | no | Application-level NntpDB options (startup verification only) |

Setting names are PascalCase and match the `NntpdOptions` property names. Obsolete snake_case keys (`bind_address`, `server_id`, …) are not aliased.

CHECK in-flight depth is **not configurable**. Per-session overlap is the architectural constant `CheckPipeline.Depth` = 16 (see `docs/architecture.md`). A leftover `Nntpd:CheckPipelineDepth` key is ignored.

## POST article size (`MaxArticleSize`)

`Nntpd:MaxArticleSize` is the maximum destuffed POST article size in bytes. Default is `5242880` (exactly 5 MiB). Zero and negative values fail startup validation. The accepted range is `1`–`104857600` (100 MiB).

The count is the destuffed client article: header block, the blank header/body separator, and body. The NNTP multiline terminator (`CRLF . CRLF`) is not included. Stuffing dots (`..` on the wire for a destuffed `.` line) are not counted. The limit is enforced while the article is streamed; exceeding it ends reception and returns `441 Posting failed`. The server does not buffer an oversized article merely because the terminator has not arrived yet.

This setting is distinct from `ArticleIngestion:MaxArticleBytes`, which bounds IHAVE/TAKETHIS receive destuff before `ArticleRecord` creation (default 4 MiB). A valid 5 MiB POST is not re-checked against `MaxArticleBytes`. The post-queue worker does not destuff or re-parse queued records.

Operator runbook (NntpDB schema, 60s snapshot refresh, Redis reservation lifecycle, SPAMD, failure matrix): [postfilter.md](postfilter.md). Do not put PostFilter policy in `appsettings.json`.

## PostFilter (NntpDB)

Cluster posting policy is **not** configured in appsettings. It is loaded from MySQL `nntppostfiltercurrent` (published revision) plus the matching `nntppostfilterpolicy` row and revision-keyed collection tables. Account lists store lowercase MD5 hex of the AUTH username (`CHAR(32)`), not the plaintext name. Policy `art_type` is a MySQL `ENUM` of the public `ArticleType` names. Rejection evidence is appended asynchronously to `nntppostfilterrejections` and does not change `441`/`240`. DDL: [postfilter.sql](schema/postfilter.sql). `PostFilterPolicyService` requires a successful initial load (after `NntpDbService`) and refreshes every 60 seconds. Refresh failure retains last-known-good. POST never queries MySQL for policy.

A leftover `Nntpd:PostFilter` section fails startup (`PostFilterLeftoverConfigurationValidator`). There is no node-local Gate, quota, or SPAMD-policy override. Node-local settings that remain are only how to reach NntpDB (`ConnectionStrings:NntpDB`) and Redis (`Redis:*`).

SpamAssassin eligibility (`sa_max_article_size` exclusive, `sa_exclude` ArtTypes) is compiled into that snapshot. A skip does not contact SPAMD and is not an `OnFailure` event. Seed `sa_max_article_size = 131072` and `sa_exclude = YEncoded` match the historical small-article boundary.

Example:

```json
"Nntpd": {
  "MaxArticleSize": 5242880,
  "MailComplaintsTo": "abuse@usenet.ninja"
}
```

## POST complaint mailbox (`MailComplaintsTo`)

`Nntpd:MailComplaintsTo` is the mailbox written as `mail-complaints-to` on the server-generated POST `Injection-Info` header. Default is `abuse@usenet.ninja`. Empty, whitespace-only, and implausible mailbox values fail startup validation. Client-supplied `Injection-Info` is discarded.

Example:

```json
"Nntpd": {
  "MailComplaintsTo": "abuse@usenet.ninja"
}
```

## Path-survey ninpaths (`Top1000`)

`Nntpd:Top1000` is the enablement switch and recipient list for the internal ninpaths processor. There is no `EnableNinpaths` flag and no `Top1000` environment variable.

- Missing, null, or empty array: ninpaths is disabled. Completed `inpaths-yyyyMMdd.log` files are still gzip-archived; they are not parsed for a report.
- Whitespace-only entries are ignored.
- One or more remaining mailboxes: after daily rotation, NNTPD streams the completed uncompressed file, aggregates INN ninpaths statistics, formats the compact `!!NINP` dump, and sends that dump once through `IEmailService` to every remaining address.
- Non-whitespace entries that are not mailboxes fail startup validation.

Processing is a background `IApplicationService`. The rotation hook opens the completed file (share-read/write/delete) and returns without parsing. Gzip proceeds independently. Raw Path records are not retained; memory is O(unique sites + relations). Failures are logged and do not affect NNTP, ingestion, news, or gzip.

The email subject follows INN sendinpaths: `inpaths {Fqdn}`. The body is the compact dump, not the human-readable `-r` ZCZC report. From is `Email:DefaultFrom`. When `Email:Enabled` is `false`, the report is generated but not spooled.

Example:

```json
"Nntpd": {
  "Top1000": [
    "top1000@anthologeek.net",
    "cknipe@opticnetworks.net"
  ]
}
```

## POST injection metadata

VectorNNTP treats the following headers as server-owned for locally POSTed articles. Client-supplied values are discarded and replaced; they are never accepted as this server’s posting path or injection identity.

| Header | Server value |
|--------|----------------|
| `Path` | exactly `.POSTED` |
| `Injection-Date` | UTC RFC date-time captured once at the injection boundary |
| `Injection-Info` | `{Fqdn}; logging-data="{Message-ID}"; mail-complaints-to="{MailComplaintsTo}"` |
| `X-Trace` | opaque AES-256-GCM token (`v1.` + Base64url of nonce, ciphertext, and tag) |
| `Xref` | discarded (not emitted on POST) |
| `NNTP-Posting-Date` | discarded (not generated) |
| `NNTP-Posting-Host` | discarded (not generated) |

The client transport IP address is never written in plaintext article headers. `Injection-Info` does not include `posting-host`. The authenticated-encrypted `X-Trace` payload contains:

| Field | Meaning |
|-------|---------|
| Peer IP | Effective transport client address |
| Peer Port | Effective transport client port |
| Injection Unix timestamp | Injection-boundary UTC time |
| Trace GUID | Unique id for this POST attempt |
| Authenticated username | AUTHINFO identity captured at POST admission, or empty when the session is unauthenticated |

The username is taken only from trusted session authentication state. It is never read from `From:`, a client `X-Trace`, or other article headers. It is not written as plaintext article metadata.

The token envelope remains `v1.` + Base64url(nonce \|\| ciphertext \|\| tag) with AAD `VectorNNTP.XTrace.v1`. Inner payload version 2 adds a length-prefixed UTF-8 username. Existing inner version 1 tokens (no username) remain decryptable. Key rotation (`XTraceKey` / `XTracePreviousKey`) is independent of payload version. Base64 encoding of the IP is not used as protection; AES-GCM provides confidentiality and tamper detection.

`Fqdn` is the generated `nntpd{ServerId:00}.{DnsSuffix}` identity (for example `nntpd01.usenet.ninja`). The client `Date:` is preserved. A missing `Message-ID:` is synthesized as `<MD5(UUID())@usenet.ninja>` for that posting attempt.

## POST X-Trace protection (`XTraceKey`)

`Nntpd:XTraceKey` is the persisted AES-256 key used by `AesGcmPostingTraceProtector` to generate POST `X-Trace` values. The process does not generate a random key at startup. Restarting with the same key keeps previously issued tokens decryptable.

Rotation:

1. Generate a new 32-byte key and store it as `XTraceKey`.
2. Move the previous current key to `XTracePreviousKey`.
3. New articles use the current key. Trusted decrypt tries the current key, then the previous key.
4. After the previous key is removed, tokens produced only with that retired key cannot be decrypted.

Never commit `XTraceKey` or `XTracePreviousKey`. Do not put them in `appsettings.json`, samples, logs, exception messages, or options dumps. Validation failure messages name the setting; they never include the secret value. Decrypted `X-Trace` payloads are not written to application logs. The newsmaster utility `NNTPCancelMessage` decrypts `X-Trace` with the same keys and prints the payload to the terminal only.

## Newsmaster AUTHINFO (`NewsmasterUser`)

When both `Nntpd:NewsmasterUser` and `Nntpd:NewsmasterPassword` are set, AUTHINFO USER/PASS for that account grants posting plus `ControlCancelPermitted`. That flag allows only a well-formed `Control: cancel <message-id>` header on POST (RFC 5536 Control syntax; RFC 5537 §5.3 CANCEL). It does not permit `newgroup`, `rmgroup`, `checkgroups`, or any other Control verb. Ordinary authenticated users still receive `441` for every `Control` header. RFC 1036 is historical only.

When either value is omitted, both must be omitted and the newsmaster path is inactive. Reader AUTHINFO still uses MySQL `nntpusers` through the existing NntpDB pool (`CompositeNntpAuthenticationProvider`). Transit peer AUTHINFO remains a separate peer-credential check and never falls through to MySQL.

### `nntpusers` remaining-byte quota

Every authenticated reader account has `account_byte_limit`. It is **remaining** download quota, not an original configured cap, and it applies independently of `account_rate_limit`. `account_type` is not selected for NNTP policy. `account_art_type` is selected: an `INT UNSIGNED` of `ArticleType` flags (`65535` = `ArticleTypeCapabilities.All` = unrestricted for existing users). Apply [`nntpusers-account-art-type.sql`](schema/nntpusers-account-art-type.sql) to the account database before deploying that `SELECT`. A missing column fails AUTHINFO with `503 Temporary authentication failure`; it is not treated as a bad password and does not grant unrestricted access. It is enforced on POST/TAKETHIS when an `ArticleRecord` exists and is not re-queried per article. IHAVE still classifies after `235`; ARTICLE/HEAD/BODY/STAT have no stored ArtType yet.

| Remaining | Meaning |
|-----------|---------|
| `> 0` | Bytes remain |
| `0` | Exhausted |
| `< 0` | Invalid / never allowed |

NULL remaining maps to `0` (exhausted). There is no `0 = unlimited` meaning. An effectively unlimited account is provisioned with a sufficiently large positive remaining value (for example 10 TB). Newsmaster/admin identities (`AccountPolicy` null) and unauthenticated or Transit-only sessions do not participate.

**Quota top-up is not automatic.** `account_byte_limit` is remaining quota. Redis is conservative and will never autonomously increase an existing remaining-byte value. Changing MySQL remaining while `nntpd:bytes:{sha256hex(accountName)}` exists leaves effective remaining at `min(Redis, MySQL)`. An exhausted account (`MySQL=0`, `Redis=0`) that is topped up in MySQL stays exhausted until AccountBytes Redis state is deleted. This repository has no account-management writer for `account_byte_limit` (the only SQL write is consume). After an external MySQL top-up the operator must invalidate cluster state:

1. Update `nntpusers.account_byte_limit` to the new remaining value.
2. Call `IAccountByteAccountant.DeleteAccountByteStateAsync(accountName)`, or `DEL nntpd:bytes:{sha256hex(UTF-8 accountName)}` on the cluster Redis.

The next observe/APPLY then sees a missing Redis key and may initialize from current MySQL remaining. Do not expect AUTHINFO or APPLY to publish the top-up while the Redis key still exists.

Live remaining is **not** the AUTHINFO account-record cache. That cache is Redis only (`nntpd:account:{sha256hex(UTF-8 accountName)}`, `SET EX 10`) with MySQL on miss/expiry and process-local single-flight for concurrent cold lookups. It stores the MySQL `nntpusers` row at lookup time only. There is no process-local full-record cache in front of Redis. GET does not extend the TTL. The cache does not subtract downloads, update remaining, or replace `nntpd:bytes:{sha256hex(accountName)}`. Redis GET/SET failure on the account-record cache falls back to MySQL and is not an authentication `503`. `AccountBytes` observes Redis cluster state when present and otherwise the durable MySQL remaining. Effective remaining is `min(Redis, MySQL)` when Redis exists. Redis (`nntpd:bytes:{sha256(accountName)}`, HASH `remaining` plus per-batch `b:{batchId}` marks, at most 256 marks) never has a key TTL and is never reconstructed from an original provisioned quota when the key is missing. Redis APPLY is idempotent per batch id: remaining is floored to `min(current, mysqlRemainingAfter)` and `consumed` is not subtracted. The same batch id is a no-op. `Redis < MySQL` is a valid conservative state and is **never repaired upward**. After APPLY is idempotent, a duplicate retry cannot create an artificial Redis-low. Redis-low can still occur when a later node's lower `mysqlRemainingAfter` arrived first, or when an operator raises MySQL remaining while the Redis key still exists. A MySQL top-up is visible on Redis only after the key is deleted so the next APPLY can initialize from current durable remaining. APPLY is not a sync-from-MySQL.

Accounting is batched on the `SessionStateService` ~10-second cycle (lease renewal and byte APPLY share that scheduler; there is no second AccountBytes timer). It is not byte-exact at the instant of exhaustion. Expected overshoot is roughly the bytes those authenticated sessions can send in one interval plus one in-flight NNTP response (for example about 1.25 GiB for one 1 Gbit/s session). MySQL stores durable remaining (`CASE`/`GREATEST`-style clamp at zero). Redis stores cluster-wide live remaining and may only initialize from durable remaining, decrease, or reconcile downward. When an account has both SessionState ownership and a committed byte batch, one Redis EVAL performs renewal and APPLY.

### `nntpusers` rate limit

Every authenticated reader account also has `account_rate_limit`. It is the **account-wide aggregate outbound download rate in bits per second (bps)** and applies independently of remaining-byte quota. It is not megabits and not a per-session cap. Example: `account_byte_limit = 10,000,000,000` and `account_rate_limit = 2,400` means 10 GB remaining **and** 2,400 bps = 300 B/s aggregate. Both apply at once.

| `account_rate_limit` | Meaning |
|----------------------|---------|
| `240` | 240 bps = 30 bytes/sec |
| `1,000` | 1 kbps = 125 bytes/sec |
| `1,000,000` | 1 Mbps = 125,000 bytes/sec |
| `10,000,000` | 10 Mbps = 1,250,000 bytes/sec |
| `> 0` | Aggregate cap; `floor(bps / 8)` bytes/sec, then `floor(accountBytes / live_sessions)` per session |
| `0` | Unlimited; no rate allocation and no extra session tracking for rate |
| `< 0` | Treated as unlimited (same sentinel as `0`) |
| `1`–`7` | Positive but `floor(bps / 8) = 0`; blocked (`cap = -1`), never unlimited |

This is not the remaining-byte rule. `account_rate_limit = 0` does **not** mean exhausted; it means unlimited rate while byte quota still applies. NULL rate maps to `0` (unlimited). The divisor is the live cluster-wide authenticated session count, never `account_session_limit`. Example: `10,000,000` bps (10 Mbps) and session limit 10 with only 2 sessions active is 625,000 bytes/sec each, not 125,000. When the third of 10 disconnects, remaining sessions move from 125,000 to `floor(1,250,000 / 7)` = 178,571 bytes/sec without reconnecting.

Allocation is cluster-wide: five sessions on two nodes still split `10,000,000` bps five ways after every node has observed the new total. SessionState's existing session HASH is the only counter. Local sessions are updated on admit/release immediately when this node owns every session. A session that joins a node while other nodes still hold the previous share is blocked. Established local sessions may only drop, using the last applied split to bound what remotes may still be sending. Equal shares resume when this node owns every remaining session. Remote disconnect is safe under-use until the next renew. There is no `RateLimitService` and no Redis operation on the write path.

The limiter sits under TLS/DEFLATE, so it throttles octets written toward the socket. Byte accounting remains at uncompressed `PipeWriter.Advance`. The two policies apply together and do not disable each other.

Never commit `NewsmasterPassword`. Do not put it in `appsettings.json`, samples, logs, or exception messages.

```text
NNTPD__NEWSMASTERUSER=newsmaster
NNTPD__NEWSMASTERPASSWORD=<secret>
```

## Newsmaster utility (`NNTPCancelMessage`)

`src/VectorNNTP.NNTPCancelMessage` is a separate executable. It is not part of the NNTP server runtime.

```text
NNTPCancelMessage --host nntpd01.usenet.ninja --port 563 --tls \
  --username newsmaster --password 'secret' <message-id>
NNTPCancelMessage --host nntpd01.usenet.ninja --port 563 --tls \
  --username newsmaster --password 'secret' --cancel <message-id>
```

Command-line `--host`, `--port`, `--username`, `--password`, `--tls` / `--plaintext`, and `--cancel` / `-cancel` override `appsettings.json`. Without `--cancel` the utility never POSTs. Credentials are mandatory for `--cancel`. If credentials are supplied for inspect, AUTHINFO runs before HEAD. Failed AUTHINFO stops the utility.

`--password` can appear in OS process listings and shell history. Prefer `NntpCancelMessage:Password` / `Nntpd:NewsmasterPassword` via environment or secrets. The password is never printed, logged, or included in exception messages.

Cancel is refused when authentication fails, HEAD fails, the article is missing (`430`), `X-Trace` cannot be decrypted, the original `Newsgroups` header is absent/invalid, or PGPVERIFY signing cannot be configured or used. There is no `--no-sign` / `--unsigned` / `--skip-signature` option. Inspection (`HEAD` + decrypt) does not require PGP.

The cancel article itself is RFC 5536 / RFC 5537: it copies that exact `Newsgroups` list, uses `Control: cancel <target-message-id>` (RFC 5537; Control is authoritative; no RFC 1036 `cmsg`), generates an independent Message-ID, and omits server-owned headers so POST can write Path / Injection-Date / Injection-Info / a new `X-Trace` identifying the authenticated newsmaster.

PGPVERIFY is a de-facto Netnews control-message authentication convention. It is **not** standardized by RFC 5537. RFC 5537 §5.1 identifies PGPVERIFY as an existing unstandardized mechanism. VectorNNTP implements it for interoperability with INN `pgpverify` and similar peers. Do not describe the signature as RFC-compliant. The signature is **not** PGP/MIME, S/MIME, or an `X-PGP-Signature` extension.

The admin utility signs the FORMAT header set `Subject,Control,Message-ID,Date,From,Sender` plus the body (Unix LF). FORMAT constructs each signed header as `Name: ` (colon + space), including empty Sender as `Sender: ` + EOL. Deployed INN `pgpverify` 1.23–1.31 then strips trailing SP/HT immediately before LF (`$message =~ s/[ \t]+\n/\n/g`), so the hashed empty Sender is `Sender:\n`. VectorNNTP applies that same INN detached-verification rule so CANCEL controls interoperate with deployed INN. This does not rewrite or supersede FORMAT. `Newsgroups` is present on the wire but unsigned, because FORMAT adds it after signing. Path, Injection-Date, Injection-Info, and X-Trace are not signed; the server still generates them. `X-PGP-Sig` is a client header and is passed through POST.

VectorNNTP.NNTPD `HEAD <message-id>` retrieves CanonicalV1 headers through ArticleWork + VATP when available. Numeric HEAD forms remain lookup-failure only.

Client settings bind from `NntpCancelMessage` (`Host`, `Port`, `UseTls`, `From`, `Username`, `Password`, `Pgp`) plus `Nntpd:BindPort` / `BindPortTls` when `Port` is `0`, and the same `XTraceKey` / `XTracePreviousKey` / newsmaster secrets as the server. TLS never falls back to plaintext.

### `NntpCancelMessage:Pgp`

```json
"NntpCancelMessage": {
  "Pgp": {
    "Enabled": true,
    "PrivateKeyPath": "",
    "PrivateKeyPassphrase": "",
    "KeyId": ""
  }
}
```

| Setting | Env | Notes |
|---------|-----|--------|
| `Enabled` | `NntpCancelMessage__Pgp__Enabled` | Must be `true` for `--cancel`. Inspection still works when this is `true` but `PrivateKeyPath` is empty. |
| `PrivateKeyPath` | `NntpCancelMessage__Pgp__PrivateKeyPath` | ASCII-armored OpenPGP secret key or keyring **outside Git**. On Unix the file must not be group- or world-accessible. On Windows restrict the NTFS ACL to the newsmaster account. |
| `PrivateKeyPassphrase` | `NntpCancelMessage__Pgp__PrivateKeyPassphrase` | Unlocks the secret key. Never commit this. Prefer environment or secrets. Never logged. |
| `KeyId` | `NntpCancelMessage__Pgp__KeyId` | Full fingerprint (preferred) or 16-hex Key ID. Required when the file contains more than one signing-capable secret key. The utility never chooses a key from the command line. |

Do not put a real private key or passphrase in `appsettings.json`. `Enabled: true` with empty path/passphrase is the safe committed sample.

Key generation (operator machine, not in this repository):

```text
gpg --quick-generate-key 'newsmaster@usenet.ninja' rsa3072 sign 2y
gpg --export-secret-keys --armor <fingerprint> > /secure/path/newsmaster-secret.asc
gpg --export --armor <fingerprint> > newsmaster-public.asc
chmod 600 /secure/path/newsmaster-secret.asc
```

RSA 3072 is recommended for production. Tests use RSA 2048 only. Signatures are OpenPGP detached **binary-document** signatures with SHA-256, ASCII-armored, then placed into `X-PGP-Sig` as FORMAT specifies (version token, comma-separated header names, tab-folded radix64). Peers export/import the public key into their `pgpverify` keyring.

If multiple signing-capable secret keys are present and `KeyId` is omitted, CANCEL aborts. The selected identity (full fingerprint and User ID) is printed to the terminal; the private key is never displayed.

Example environment:

```text
NNTPD__XTRACEKEY=<64-hex-or-base64-32-byte-key>
NNTPD__XTRACEPREVIOUSKEY=<optional-previous-key>
```

POST returns `240 Article received OK` only after the article has been streamed into one stuffed IHAVE/TAKETHIS queue representation (dot-stuffed wire, NNTP terminator omitted) and admitted with `IArticleIngestionQueue.TryAdmit`. POST does not persist, deliver, or propagate the article; existing ingestion workers own that work after admission. Admission failure returns `441 Posting failed`.

## POST newsgroup posting policy

Production plain and TLS listeners inject the process-wide `INewsgroupCatalogue` into each `NntpSession`. When that catalogue is present and no policy is injected, the session uses `CatalogueNewsgroupPostingPolicy`. `SyntaxOnlyNewsgroupPostingPolicy` remains only the fallback for sessions constructed without a catalogue (tests and other offline hosts). It is not the production POST policy.

These are distinct checks:

| Check | When | Failure |
|-------|------|---------|
| Session-level posting permission (`Authorization.PostingPermitted`) | First-stage POST, before `340` | `440 Posting not permitted`; the article is not read |
| Newsgroup-level posting policy | After `340`, from the parsed `Newsgroups:` header against one captured catalogue snapshot | `441 Posting failed`; the article is drained to the terminator but is not Peeked, admitted, Remembered, or acknowledged with `240` |

RFC 3977 does not expose `Newsgroups:` before `340`, so group-policy failures cannot use `440`.

The policy captures `NewsgroupCatalogue.Current` once for that POST evaluation and uses the same case-insensitive snapshot `TryGet` as GROUP/LIST. It does not query MySQL. Every syntax-validated `Newsgroups:` target must exist in that snapshot. Status `n`/`x`/`j` reject the entire POST. Status `m` is classified as requiring moderation; it is not treated as `y`.

| `posting_status` | Local POST |
|------------------|------------|
| `y` | Ordinary local posting |
| `n` | Rejected — posting prohibited |
| `m` | Moderated. Unapproved posts are submitted for moderator forwarding (or `441` when forwarding is unavailable). Approved posts inject only after AUTHINFO + `nntpmoderators` authorization for every moderated target. |
| `x` | Rejected — closed: local posting and peer articles are prohibited |
| `j` | Rejected — peer-only: local posting is not accepted |
| unknown name | Rejected — the group is not in the captured snapshot |

`Approved:` is a claimed mailbox identity. It is not trusted by itself. See `nntpmoderators` below.

Malformed or empty `Newsgroups:` remain existing parser syntax failures (`441`) and do not consult the snapshot. See `docs/commands.md` and `docs/architecture.md`.

## Transit article-queue memory (`TransitQueueMemoryLimit`)

`Nntpd:TransitQueueMemoryLimit` is the Transit article-queue **payload** budget in bytes. Default is `1073741824` (exactly 1 GiB). Zero and negative values fail startup validation. The implementation accounts with a signed 64-bit integer, so the maximum representable value is `9223372036854775807`.

The budget is the sum of owned queued article payload lengths (`InboundArticle.Payload.Length`), which aliases CanonicalV1 `ArticleRecord.ArtData` for TAKETHIS, POST, and IHAVE. Object overhead is not counted. This is **not** total process memory.

Larger values permit more burst absorption between network ingress and downstream workers. Memory is released as queued articles are consumed. An individual article larger than the configured budget is rejected (IHAVE `437`, TAKETHIS `439`) rather than waited for, so admission cannot deadlock.

`ArticleIngestion:QueueCapacity` is a leftover article-count setting retained so existing configuration files still bind. It is **not** an admission bound. The historical 256-article cap was only a memory-safety choke and has been removed.

## Article ingestion worker pool (`ArticleIngestion:*`)

`IncomingSpoolWriterService` remains the `IApplicationService` boundary. It owns two independently scaled stages: an `IngestionWorkerPool` (article ingress → encode/enqueue OverviewDB work → news/Path-survey/persist) and an `OverviewDbPublisherPool` (OverviewDB work queue → RabbitMQ publish + confirm).

- **MinWorkers / MaxWorkers** — article pool starts at `MinWorkers` and never goes below it or above `MaxWorkers`. Pressure is sampled from the article ingress queue.
- **OverviewDbMinPublisherWorkers / OverviewDbMaxPublisherWorkers** — OverviewDB publisher pool bounds. Pressure is sampled from the in-process OverviewDB work queue. Do not tie these to article worker counts.
- **OverviewDbWorkQueueMemoryLimit** — byte budget for the intermediate OverviewDB work queue. Article workers enqueue already-encoded `OverviewArticleV1` payloads here and do **not** await RabbitMQ. This queue is in-process only; enqueue is not equivalent to broker durability.
- **Scaling** — every `ScaleIntervalSeconds`, each pool samples pressure as `max(byte utilisation, waiting-producer signal)` for its own queue. Sustained pressure ≥ `ScaleUpPressureThreshold` for `ScaleUpConsecutiveIntervals` samples adds one worker. Sustained pressure ≤ `ScaleDownPressureThreshold` for `ScaleDownConsecutiveIntervals` samples removes one worker. Instantaneous single samples do not oscillate either pool.
- **OverviewDbPublisherBatchSize** — outstanding asynchronous confirmation window. Publishers write without awaiting each `basic.ack`; they stop issuing new publishes when this many confirms are in flight.
- **OverviewDbPublisherShutdownSeconds** — bounded drain of the OverviewDB publisher stage on shutdown. Outstanding unconfirmed OverviewDB work may be abandoned afterward (intentional crash-loss semantics).
- **MaxPublishConcurrency** — legacy binding retained for compatibility; the async confirmation window is `OverviewDbPublisherBatchSize`.
- **Scaling** — every `ScaleIntervalSeconds`, each pool samples pressure as `max(byte utilisation, waiting-producer signal)` for its own queue. Sustained pressure ≥ `ScaleUpPressureThreshold` for `ScaleUpConsecutiveIntervals` samples adds one worker. Sustained pressure ≤ `ScaleDownPressureThreshold` for `ScaleDownConsecutiveIntervals` samples removes one worker. Instantaneous single samples do not oscillate either pool.
- **Backpressure** — article ingress still waits or rejects when `TransitQueueMemoryLimit` is exhausted. When the OverviewDB work queue is full, article workers wait at the enqueue boundary (intentional). When the outstanding confirmation window is full, OverviewDB publisher workers wait before issuing more publishes.
- **Shutdown** — stop admitting article ingress and drain article workers (they may still enqueue OverviewDB work while draining); then Complete the OverviewDB work queue, drain publisher workers for `OverviewDbPublisherShutdownSeconds`, then abandon any remaining outstanding OverviewDB confirms.

Example:

```json
"Nntpd": {
  "TransitQueueMemoryLimit": 4294967296,
  "ArticleIngestion": {
    "MinWorkers": 2,
    "MaxWorkers": 32,
    "OverviewDbWorkQueueMemoryLimit": 1073741824,
    "OverviewDbMinPublisherWorkers": 2,
    "OverviewDbMaxPublisherWorkers": 32,
    "OverviewDbPublisherBatchSize": 100,
    "OverviewDbPublisherShutdownSeconds": 2,
    "MaxPublishConcurrency": 32,
    "ScaleIntervalSeconds": 2,
    "ScaleUpPressureThreshold": 0.4,
    "ScaleDownPressureThreshold": 0.1,
    "ScaleUpConsecutiveIntervals": 2,
    "ScaleDownConsecutiveIntervals": 3
  }
}
```

## Redis

Top-level `Redis` section (not nested under `Nntpd`). Redis is a required application dependency: missing hosts or an unsuccessful startup connect/PING fail the host before `Running`.

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `Host` | string array | _(none)_ | **yes** | Redis hostnames or IP addresses used as StackExchange.Redis endpoints/seeds for one shared topology |
| `Port` | int | `6379` | no | TCP port applied to every configured host (`1–65535`) |

There is no `MaxConnections` setting and no application-level connection pool. One long-lived `ConnectionMultiplexer` is shared by all Redis consumers, including PostFilter accept-quota (`nntpd:pf:q:` / `nntpd:pf:m:`). Quota **policy** is NntpDB; Redis holds runtime reservations only. See [postfilter.md](postfilter.md).

Multiple hosts are multiplexer seeds, not independently round-robined servers. They must belong to a topology that actually shares HistoryDB data.

Example:

```json
"Redis": {
  "Host": [ "redis-01.example.net", "redis-02.example.net" ],
  "Port": 6379
}
```

### Live SessionState Redis Lua tests

Ordinary `VectorNNTP.NNTPD.Tests` runs do not open Redis and do not read `Redis:Host` or the application-configured production endpoint.

To execute the production SessionState Lua scripts (`TRY_ADMIT`, `RELEASE`, `RENEW`, `RELEASEOWNER`) through `RedisSessionStateStore` against the dedicated test Redis:

```text
VECTORNNTP_REDIS_INTEGRATION=198.18.0.70:6379
```

Requirements:

- `198.18.0.70:6379` is the authorized SessionState integration-test Redis (not localhost and not an application-configured production endpoint).
- Tests create uniquely named accounts (`vnntp.sess.lua.{guid}`) and delete only those accounts' `nntpd:sess:*` / `nntpd:srcip:*` keys. They do not run `FLUSHDB` or `FLUSHALL`.
- When the variable is unset, the tests skip.
- When the variable is set and Redis is unreachable, the tests fail.

Do not commit credentials. Redis options have no password field; the unauthenticated connection path is used.

### Live TransitPeerState Redis Lua tests

Ordinary `VectorNNTP.NNTPD.Tests` runs do not open Redis and do not read `Redis:Host` or the application-configured production endpoint.

To execute the production TransitPeerState Lua scripts (`TRY_ADMIT`, `RELEASE`, `RENEW`, `RELEASEOWNER`) through `RedisTransitPeerStateStore` against the dedicated test Redis:

```text
VECTORNNTP_REDIS_INTEGRATION=198.18.0.70:6379
```

Requirements:

- `198.18.0.70:6379` is the authorized TransitPeerState integration-test Redis (same dedicated test instance as SessionState; not localhost and not an application-configured production endpoint).
- Tests create uniquely named peer identifiers (`vnntp.tconn.lua.{guid}`) and delete only those identifiers' `nntpd:tconn:*` keys. They do not run `FLUSHDB` or `FLUSHALL`.
- When the variable is unset, the tests skip.
- When the variable is set and Redis is unreachable, the tests fail.

Do not commit credentials. Redis options have no password field; the unauthenticated connection path is used.

### Live AccountBytes Redis Lua tests

Ordinary `VectorNNTP.NNTPD.Tests` runs do not open Redis and do not read `Redis:Host` or the application-configured production endpoint.

To execute the production AccountBytes Lua scripts (`APPLY`, `OBSERVE`) through `RedisAccountByteStore` against the dedicated test Redis:

```text
VECTORNNTP_REDIS_INTEGRATION=198.18.0.70:6379
```

Requirements:

- `198.18.0.70:6379` is the authorized AccountBytes integration-test Redis (same dedicated test instance as SessionState; not localhost and not an application-configured production endpoint).
- Tests create uniquely named accounts (`vnntp.bytes.lua.{guid}`) and delete only those `nntpd:bytes:` keys. They do not run `FLUSHDB` or `FLUSHALL`.
- When the variable is unset, the tests skip.
- When the variable is set and Redis is unreachable, the tests fail.

Do not commit credentials. Redis options have no password field; the unauthenticated connection path is used.

## RabbitMQ

Top-level `RabbitMQ` section (not nested under `Nntpd`, `BackFiller`, or `StorageServer`). Options types and connection validation live in `VectorNNTP.Common.Messaging.RabbitMq`. Applications bind the section themselves and call `AddRabbitMqInfrastructure()`. Nested `Management` is optional for connectivity-only hosts (for example StorageServer and BackFiller); NNTPD requires `Management:BaseUrl` and broker credentials for ArticleWork availability discovery (`NntpdRabbitMqOptionsValidator`).

For NNTPD, RabbitMQ is a required application dependency: missing hosts, invalid settings, or an unsuccessful startup connect fail the host before `Running`. After start, connectivity loss is recovered indefinitely; NNTPD does not expose a consecutive-failure abandon threshold. BackFiller and StorageServer use the same Common connectivity service. BackFiller registers `RabbitMqService` as an early `IHostedService` (before accounts) via a thin hosted adapter and does **not** place it in BackFiller `ApplicationServiceManager` (Cloudflare → ACME → Cache Listener). StorageServer uses start order Cloudflare → RabbitMQ → StorageServerAdvertisementPublisher → ACME → listener. It declares/publishes to `cache.broadcast` but does not own `cache.requests` consumers or Management HTTP. BackFiller connection settings bind only from the top-level `RabbitMQ` section / `VECTOR__RABBITMQ__*`; there is no nested `BackFiller:RabbitMQ` connectivity section.

`RabbitMqService` (Common) is the dedicated application service that owns the broker connection lifecycle. It establishes one process-wide connection, verifies that the connection is open, and replaces it on connectivity loss while incrementing a monotonic connection generation. Client automatic recovery is disabled. The AMQP client-provided name comes from `IRabbitMqConnectionNameProvider`. After a successful start, reconnect continues indefinitely until the connection is restored or the host shuts down. Callers obtain the current connection with `TryGetCurrent`; they do not own or dispose it.

`RabbitMqTopologyService` (NNTPD-only) starts immediately after `RabbitMqService` and declares NNTPD-owned topology only: the durable `cache.requests` fanout exchange (no shared work queue; StorageServers bind ephemeral `cache.<storage-fqdn>` queues), the durable `cache.broadcast` fanout exchange, and the one-way OverviewDB ingest queue `overviewdb.queue`. Per-NNTPD ephemeral `cache.<nntpd-fqdn>` queues are declared by `StorageServerFleetConsumerService` (non-durable, exclusive, auto-delete) and bound to `cache.broadcast`. It does **not** declare per-backbone `backfiller.<backbone>` provider queues. Those are created by VectorNNTP.BackFiller when a backbone becomes usable (usable NNTP capacity), immediately before Article Work consumers start, using the same durable fanout + durable quorum queue (`x-queue-type=quorum`) + same-name binding semantics. Inactive backbones do not declare topology; becoming inactive later stops consumers but does **not** delete exchange/queue/binding. Either NNTPD or BackFiller may start first on an empty broker. Exchange, queue, and routing-key names are trimmed and invariant-lowercased. `cache.requests`, `cache.broadcast`, and `overviewdb.queue` are application constants, not configuration keys. StorageServer advertisement interval (1s) and message TTL (3s) are likewise constants. `cache.requests` is not a BackFiller provider. `overviewdb.queue` is a durable, non-exclusive, non-auto-delete quorum queue (`x-queue-type=quorum`, no queue-wide `x-message-ttl`) published through the AMQP default exchange with routing key `overviewdb.queue`; no dedicated OverviewDB exchange, bind, reply queue, or RPC topology is declared. All VectorNNTP-owned durable application work queues are quorum (`x-queue-type=quorum`). Exclusive auto-delete ArticleWork RPC reply queues remain non-durable classic. Declaration is fail-closed and uses RabbitMQ's idempotent declare/bind operations; incompatible existing topology is not deleted or rewritten. Connection-generation replacement does not redeclare topology. BackFiller ArticleWork availability discovery uses the RabbitMQ Management HTTP API (GET /api/queues/{vhost} with the URL-encoded virtual host, e.g. %2F for /), refreshed approximately every 5 seconds into an in-memory snapshot. A provider queue is eligible only when Management reports consumers > 0. Missing queues and zero-consumer queues are unavailable. Availability is intentionally eventually consistent (~5 seconds normal staleness). A failed Management refresh retains the last successful snapshot (last-known-good) and does not treat the outage as all-consumers-zero. AMQP remains the ArticleWork data plane; Management is the discovery plane only. Management Basic auth uses RabbitMQ:Username / RabbitMQ:Password (never log those secrets).

Each article ingestion worker encodes one Common `OverviewArticleV1` protobuf per accepted article and enqueues the owned bytes onto the bounded in-process OverviewDB work queue (`ArticleIngestion:OverviewDbWorkQueueMemoryLimit`). Article workers do **not** call RabbitMQ or await publisher confirmation; successful enqueue is not durable broker handoff. An independent `OverviewDbPublisherPool` drains that queue and publishes through `OverviewDbHandoffPublisher` to `overviewdb.queue` with an asynchronous outstanding-confirmation window (`ArticleIngestion:OverviewDbPublisherBatchSize`). Publishes return after the write; `BasicAcksAsync` / `BasicNacksAsync` / `BasicReturnAsync` settle outstanding work. Each publication sets a fresh AMQP `MessageId` (UUID), `AppId` to the generated `{Fqdn}`, `Expiration=2000` (per-message TTL in milliseconds; intentional short-lived handoff — not a queue-wide TTL and not an application timer), persistent delivery mode, and `mandatory=true` so a missing or unroutable `overviewdb.queue` is returned to the publisher as a failure. OverviewDB is expected to consume promptly; RabbitMQ is a short-lived handoff buffer, not a long-term backlog store. A negative confirmation, unroutable/basic.return, or publish failure requeues the OverviewDB work item on the in-process OverviewDB work queue while the process remains alive; crash or bounded-shutdown loss of outstanding unconfirmed work is acceptable. NNTPD does not call OverviewDB over RPC, HTTP, gRPC, or a database connection.

`StorageServerFleetConsumerService` starts immediately after topology declaration and maintains the in-memory StorageServer registry from `cache.broadcast` advertisements. `ArticleWorkRpcService` starts immediately after the fleet consumer. It attaches a shared NNTPD-owned RPC reply consumer and publishes BackFiller v1 article-work requests. Every request publication carries AMQP `RequestId`, a unique `CorrelationId`, `ReplyTo`, `ContentType=application/json`, and `Expiration=1000`. Message-id ARTICLE/HEAD/BODY/STAT use that client; on Success they fetch via `IVatpArticleClient` using `uri` + `articleId`. Lookups use the sequential Backfill Scheduler (one backbone attempt at a time, weighted by active consumer count; never concurrent fan-out). The 5-second lookup deadline is the sole ArticleWork scheduler budget (application constant, not a configuration key); there is no separate longer ceiling.

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `Hosts` | string array | _(none)_ | **yes** | Broker hostnames or IP addresses (no URI scheme, credentials, path, or query) |
| `Port` | int | `5672` | **yes** | AMQP TCP port (`1–65535`) |
| `Username` | string | _(none)_ | **yes** (for Management) | Broker / Management Basic-auth username. Required with `Password` for Management API availability discovery. Supply via `VECTOR__RABBITMQ__USERNAME` |
| `Password` | string | _(none)_ | **yes** (secret; for Management) | Broker / Management Basic-auth password. Supply via `VECTOR__RABBITMQ__PASSWORD` or secrets. Never commit or log |
| `VirtualHost` | string | `/` | **yes** | RabbitMQ virtual host |
| `EnableSsl` | bool | `true` | **yes** | Whether the connection uses TLS |
| `RequestedHeartbeatSeconds` | int | `60` | **yes** | AMQP heartbeat (`0–3600`; `0` disables) |
| `SocketTimeoutSeconds` | int | `30` | **yes** | Socket read/write timeout (`5–600`) |
| `RequestedChannelMax` | int | `2047` | **yes** | Requested channel limit per connection (`1–65535`) |
| `RpcTimeoutSeconds` | int | `30` | **yes** | Client continuation/handshake timeout (`1–3600`) |
| `ConnectionBlockedTimeoutSeconds` | int | `30` | **yes** | Client connection timeout (`5–3600`; must be ≥ `RpcTimeoutSeconds`) |
| `NetworkRecoveryIntervalSeconds` | int | `5` | **yes** | Client recovery-interval setting (`1–3600`; unused while automatic recovery is disabled) |
| `PoolReconnectBaseDelayMs` | int | `250` | **yes** | Application reconnect base delay (`50–60000`) |
| `PoolReconnectMaxDelayMs` | int | `30000` | **yes** | Application reconnect max delay (`50–300000`; ≥ base) |
| `ChannelLeaseTimeoutSeconds` | int | `60` | **yes** | Validated; reserved for later channel work (`1–3600`; ≥ `RpcTimeoutSeconds`) |
| `WorkRequestMaxPayloadBytes` | int | `1024` | **yes** | Maximum admitted Article Work request payload bytes (`1–4096`). Consumed by VectorNNTP.BackFiller |
| `ChannelPoolSize` | int | `512` | **yes** | Validated; reserved for later consumer buffering (`1–8192`) |
| `MinConnections` | int | `4` | **yes** | Validated; reserved for later pool policy (`1–512`; ≤ `MaxConnections`) |
| `MaxConnections` | int | `16` | **yes** | Validated; reserved for later pool policy (`1–512`) |
| `MaxPendingLeaseWaiters` | int | `1024` | **yes** | Validated; reserved for later channel-pool policy (`0–65536`) |
| `ConnectionScaleDownIdleSeconds` | int | `300` | **yes** | Validated; reserved for later pool policy (`30–86400`) |
| `ScaleDownCooldownSeconds` | int | `30` | **yes** | Validated; reserved for later pool policy (`0–3600`) |
| `MinimumConnectionLifetimeSeconds` | int | `300` | **yes** | Validated; reserved for later idle-retirement (`30–86400`) |
| `PublishConfirmTimeoutSeconds` | int | `10` | **yes** | Publisher-confirm wait used by OverviewDB handoff and BackFiller Article Work response publication (`1–3600`) |
| `MaximumShutdownDrainTimeoutSeconds` | int | `30` | **yes** | Validated; shutdown is cancellation-driven (`1–3600`) |
| `DegradedThreshold` | double | `0.75` | **yes** | Validated; reserved for later health policy (`>0` and `≤1`) |
| `UnhealthyThreshold` | int | `5` | **yes** | Validated; reserved for later health policy (`1–120`) |
| `ConsumerPrefetchCount` | ushort | _(none)_ | no | Optional Basic.Qos prefetch. Consumed by VectorNNTP.BackFiller Article Work consumers (`1–65535`) |
| `DiagnosticPayloadCorrelationId` | string | _(none)_ | no | Optional diagnostic gate; not used in this phase |
| `Management:BaseUrl` | string | _(none)_ | **yes** | Absolute Management HTTP API root (`http`/`https` host+port only; no `/api` path). Example: `http://rabbit-01.example.net:15672` |
| `Management:RequestTimeoutSeconds` | int | `5` | **yes** | Per-request Management API timeout (`1–60`) |

There is no application-level RabbitMQ connection pool. One long-lived connection is owned by `RabbitMqService`. `MinConnections` / `MaxConnections` are validated for contract compatibility and are not enforced.

Example (no secrets):

```json
"RabbitMQ": {
  "Hosts": [ "rabbit-01.example.net" ],
  "Port": 5672,
  "EnableSsl": false,
  "VirtualHost": "/",
  "Management": {
    "BaseUrl": "http://rabbit-01.example.net:15672",
    "RequestTimeoutSeconds": 5
  }
}
```

Do not commit credentials. Supply `RabbitMQ:Username` / `RabbitMQ:Password` via `VECTOR__RABBITMQ__USERNAME` and `VECTOR__RABBITMQ__PASSWORD`, or user secrets.

## BackFiller article retention (`BackFiller:ArticleRetention`)

In-memory retention holds CanonicalV1 `ArtData` between ArticleWork Success and VATP OPEN. A published Success RequestId is a hard OPEN capability until OPEN consumes it, cancel, `RetentionTtlSeconds` expiry, or process dispose. FIFO reclaim under `MaximumRetainedPayloadGigabytes` skips entries that still have openable RequestIds; capacity pressure rejects new admissions (`CapacityUnavailable`) instead of invalidating those capabilities. See `docs/vatp.md`.

| Key | Type | Default | Range | Description |
|-----|------|---------|-------|-------------|
| `MaximumRetainedPayloadGigabytes` | int | `4` | `1`–80% of physical RAM (and supported ceiling) | Retained ArtData byte budget |
| `RetentionTtlSeconds` | int | `60` | `1–60` | Maximum entry lifetime from insertion |
| `MaxOpenableRequestIdsPerArticle` | int | `16` | `1–256` | Bound on concurrent openable Success RequestIds per Message-ID |
| `SweepIntervalSeconds` | int | `1` | `>0` | TTL sweep cadence |

## NntpDB (`ConnectionStrings:NntpDB` and `NntpDb`)

**NNTPD owns the database service and lifecycle; MySqlConnector owns physical connection pooling.** There is no application-owned connection pool.

The NNTPD MySQL database is a **hard application dependency**. `NntpDbService` participates in application startup/shutdown and performs a mandatory `SELECT 1` check. There is no in-memory, mock, or degraded production fallback.

`ConnectionStrings:NntpDB` is the shared NNTPD and BackFiller connection string. Both applications bind the Common `NntpDbOptions` type and therefore use the same database, schema, and tables. Supply credentials through environment variables or secrets (`ConnectionStrings__NntpDB`). Do not put `ConnectionStrings` in production appsettings. Never log the connection string, passwords, or tokens.

MySQL Connector settings (server, user, SSL, and provider pooling) belong in that connection string. NNTPD does not set `Pooling=false` and does not idle-reap or cache `MySqlConnection` instances. Callers open a logical connection, use it, and dispose it (`await using`); dispose returns the physical connection to MySqlConnector's native pool.

MySqlConnector 2.6.2 pooling options used by the committed connection string:

| Connection-string option | Value | Role |
|--------------------------|-------|------|
| `Pooling` | `true` | Enable the provider pool (MySqlConnector default is also `true`) |
| `MinimumPoolSize` | `2` | Idle connections the provider keeps after `ConnectionIdleTimeout` |
| `MaximumPoolSize` | `32` | Maximum physical connections in the provider pool |
| `ConnectionIdleTimeout` | `300` | Seconds an idle pooled connection above the minimum may remain (provider reaper) |

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `ConnectionStrings:NntpDB` | string | _(none)_ | **yes** | MySQL connection string for NNTPD and BackFiller (includes provider pool settings) |
| `NntpDb:StartupTimeout` | `TimeSpan` | `00:00:15` | no | Wall-clock budget for application-level startup connect / retry (`> 0`) |

Startup opens a logical `MySqlConnection` from `ConnectionStrings:NntpDB`, executes `SELECT 1`, and disposes that logical connection. DNS, TCP, authentication, timeout, or `SELECT 1` failures fail host startup. Transient connectivity errors may retry until `StartupTimeout` elapses. A connection string rejected by `MySqlConnectionStringBuilder`, authentication failures, and a failed `SELECT 1` result fail immediately without retry. A successful check does not keep that physical connection open; MySqlConnector owns reuse.

Example environment variable (never commit credentials):

```text
ConnectionStrings__NntpDB=Server=mysql.example.net;Port=3306;Database=nntpdb;User ID=nntpd;Password=...;Pooling=true;MinimumPoolSize=2;MaximumPoolSize=32;ConnectionIdleTimeout=300;
```

`NntpDb:StartupTimeout` remains a non-secret application option (default `00:00:15`) and may stay in NNTPD appsettings. Production appsettings must not contain a `ConnectionStrings` section.

### Live MySQL moderator integration tests

Ordinary `VectorNNTP.NNTPD.Tests` runs do not open MySQL and do not read `ConnectionStrings:NntpDB` or the committed production target.

To exercise `MySqlNntpModeratorRepository` against a real `nntpmoderators` table, set a dedicated connection string:

```text
VECTORNNTP_NNTPDB_INTEGRATION=Server=...;Port=3306;Database=...;User ID=...;Password=...;
```

Requirements:

- The target already has the production `nntpmoderators` schema (do not point this at an arbitrary production writer unless you accept uniquely prefixed test rows).
- The tests insert rows under `test.vnntp.modcat.{run-id}...` and delete only those `moderator_id` values / that prefix.
- When the variable is unset, the tests skip.
- When the variable is set and MySQL is unreachable, the tests fail.

Do not commit the connection string. Diagnostics must not print passwords or the full string.

## Moderation (`nntpmoderators`)

Top-level `Moderation` section (not nested under `Nntpd`, and **not** `Control:PgpAuthorities`). Runtime moderator authorization is loaded from MySQL `nntpmoderators` into an immutable in-memory snapshot (`ModeratorCatalogueService`). PGP control authorities do not authorize `Approved:` and are not used to derive moderator addresses.

A leftover `Moderation:Moderators` array fails startup validation. Do not keep a second static authorization list in configuration.

`Moderation:Source` is provenance only (the imported INN `samples/moderators` URL). It does not create local VectorNNTP moderator accounts, passwords, or AUTHINFO identities.

Each enabled `nntpmoderators` row (`is_enabled = 'Y'`, `moderator_id ASC`) is one first-match rule:

| Column | Meaning |
|--------|---------|
| `group_pattern` | RFC 3977 wildmat matched in the application (not by MySQL). |
| `moderator_address` | Routing mailbox or INN `%s` template. Where an unapproved proto-article is submitted. |
| `account_name` | AUTHINFO principal required for local approved reinjection. Empty is routing-only. |

`%s` is replaced with the matched newsgroup name after converting `.` to `-`. Example: `fido7.some.group` + `%s@fido7.org` → `fido7-some-group@fido7.org`. The `perl.*` exception keeps its literal prefix: `news-moderator-%s@perl.org`. Expansion is for submission routing only; it does not grant POST authorization.

Matching is first-match in `moderator_id` order. Do not sort alphabetically or prefer the most specific pattern.

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `Source:Url` | string | _(none)_ | no | Human-facing INN `samples/moderators` URL |
| `Source:InnUrl` | string | _(none)_ | no | INN GitHub raw `samples/moderators` URL |
| `Source:RetrievedFrom` | string | _(none)_ | no | Which URL was actually retrieved (`InnUrl` for the current snapshot) |
| `Moderators` | array | `[]` | no | Must be empty. A non-empty list fails startup. |

Passwords are **not** stored in this section. AUTHINFO secrets remain `Nntpd:NewsmasterPassword` / `nntpusers`. The initial catalogue load fails host startup when the query cannot complete. A later refresh failure keeps the last known-good snapshot. POST captures `Current` once and does not query MySQL.

Trust model:

```text
moderator_address / %s template     ← routing destination (unapproved submission)
Approved: moderator@example.com     ← assertion
AUTHINFO USER MODERATOR01           ← authenticated principal
nntpmoderators.account_name         ← authorization
```

`Approved` alone is not sufficient. `From:` and `Message-ID` never authorize. A routing template match alone is not sufficient. A normal authenticated user with a copied `Approved:` header is rejected. An unauthenticated client with `Approved:` is rejected. A moderator for group A cannot approve group B unless a later-or-same row authorizes that account for that group (first matching pattern still wins).

Moderator reinjection is a normal NNTP POST after AUTHINFO. There is no `MODERATE` command. Reader AUTHINFO does not grant `ControlCancelPermitted`.

Cross-posting: an unapproved article is forwarded to the leftmost moderated group only (RFC 5537 §3.5.1). Further sequential moderator forwarding is the moderators' duty (RFC 5537 §3.9). Reinjection is accepted only when every remaining moderated target is authorized for the authenticated principal and the `Approved:` identities.

`IModerationSubmissionService` is the forwarding boundary. POST does not speak SMTP. The production implementation (`EmailModerationSubmissionService`) composes a moderator email and calls `IEmailService.SendAsync`. Acceptance is **durable local spool acceptance**, not remote SMTP delivery. When `Email:Enabled` is `false` (default) the service reports unavailable and unapproved moderated POST returns `441`. Enable email and supply SMTP settings (host, envelope sender, credentials via secrets) to persist moderator mail under `spool/smtp`. Spool-write or encode failures also return `441`.

`Control:PgpAuthorities` is a separate catalogue.

Example provenance-only configuration (runtime rows live in `nntpmoderators`):

```json
"Moderation": {
  "Source": {
    "InnUrl": "https://raw.githubusercontent.com/InterNetNews/inn/main/samples/moderators",
    "RetrievedFrom": "InnUrl",
    "Url": "https://github.com/InterNetNews/inn/blob/main/samples/moderators"
  }
}
```

## Outbound email (`Email`)

`Email` is a generic application email subsystem. Moderation is one producer. SMTP settings never belong on POST or on `nntpmoderators`.

The filesystem spool (`spool/smtp` by default) is the durable outbound email queue. `IEmailService.SendAsync` validates, MIME-encodes, and atomically writes the complete message plus SMTP envelope. It does **not** wait for remote SMTP. Acceptance means the complete message has been durably written to the local spool. SMTP delivery is asynchronous. Successful SMTP delivery removes the spool file. Undelivered files survive process restart. SMTP acceptance followed by filesystem deletion is at-least-once and cannot be exactly-once. `240` after moderated POST means this local spool acceptance only. Disk/permission/serialization failures fail the write; there is no in-memory fallback and no queue-capacity limit.

Spool files under `Email:Spool:Directory` (non-recursive scan of `*.eml` only):

| File | Meaning |
|------|---------|
| `.tmp` | Incomplete write. Never delivered. |
| `.eml` | Pending message. Eligible for delivery. |
| `.wrk` | In-flight claim. Crash recovery may return this to `.eml`. |
| `failed/*.eml` | Permanent failure. Retained for inspection. Never automatically delivered or requeued. |
| `.delivered` | SMTP accepted the message, but local deletion failed. Retained for inspection. **Never automatically delivered** — retrying would duplicate a message the server already accepted. |

EHLO/HELO uses the generated application FQDN (`Nntpd` `ServerId` + `DnsSuffix`). There is no `Email`-specific hostname setting. Certificate validation is mandatory; there is no option to accept invalid SMTP server certificates.

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `Email:Enabled` | bool | `false` | no | When `false`, `SendAsync` returns disabled. No spool file is written and no SMTP connection is opened. |
| `Email:DefaultFrom` | string | `noreply@usenet.ninja` in appsettings | **yes when enabled** | Default RFC 5322 From mailbox. |
| `Email:EnvelopeSender` | string | same as DefaultFrom | no | SMTP `MAIL FROM`. Never derived from To/Cc. |
| `Email:Smtp:Host` | string | empty | **yes when enabled** | SMTP hostname or IP. |
| `Email:Smtp:Port` | int | `587` | no | TCP port (`1–65535`). Does not select TLS mode. |
| `Email:Smtp:Security` | enum | `StartTls` | no | `None` (plaintext relay), `StartTls` (RFC 3207), `ImplicitTls` (immediate TLS, typically 465). Explicit; no silent downgrade. |
| `Email:Smtp:Username` | string | empty | no (secret) | SMTP AUTH username. Use `Email__Smtp__Username`. |
| `Email:Smtp:Password` | string | empty | no (secret) | SMTP AUTH password. Use `Email__Smtp__Password`. Never commit. |
| `Email:Smtp:RequireTlsForAuthentication` | bool | `true` | no | Refuse AUTH unless the session is already TLS-protected. Plaintext AUTH requires an explicit `false` and `Security=None`. |
| `Email:Smtp:ConnectTimeout` | duration | `00:00:15` | no | TCP/TLS connect budget (`100ms`–`5m`). |
| `Email:Smtp:CommandTimeout` | duration | `00:00:30` | no | SMTP read/write budget (`100ms`–`10m`). |
| `Email:Smtp:MaxAttempts` | int | `3` | no | In-process delivery attempts including the first (`1–20`). Not persisted on disk. |
| `Email:Smtp:InitialRetryDelay` | duration | `00:00:02` | no | First retry delay (exponential + jitter). |
| `Email:Smtp:MaximumRetryDelay` | duration | `00:01:00` | no | Retry delay ceiling. |
| `Email:Spool:Directory` | string | `spool/smtp` | no | Application-relative spool directory (`Path.GetFullPath`). Created automatically when email is enabled. |
| `Email:Spool:ShutdownTimeout` | duration | `00:00:15` | no | How long stop waits for the current SMTP operation (`1s`–`5m`). Pending `.eml` files remain on disk. |
| `Email:Spool:ScanInterval` | duration | `00:00:01` | no | Idle rescan interval (`20ms`–`5m`). A successful write also wakes the worker. |

AUTH mechanisms: PLAIN (preferred when advertised) and LOGIN. AUTH payloads are never logged. STARTTLS required + server without STARTTLS fails. Implicit TLS never sends SMTP before the handshake.

Example (secrets via environment, not this file):

```json
"Email": {
  "Enabled": true,
  "DefaultFrom": "noreply@usenet.ninja",
  "EnvelopeSender": "noreply@usenet.ninja",
  "Smtp": {
    "Host": "smtp.example.net",
    "Port": 587,
    "Security": "StartTls"
  },
  "Spool": { "Directory": "spool/smtp" }
}
```

## Control PGP authorities (`Control:PgpAuthorities`)

Top-level `Control` section (not nested under `Nntpd`). `Control:PgpAuthorities` is an authoritative catalogue of Usenet PGP control authorities derived from ISC/INN `control.ctl`.

This is trusted reference data. It is not currently an authorization engine.

The catalogue does **not** enable control-message processing. `newgroup`, `rmgroup`, `checkgroups`, control-message dispatch, `control.ctl` parsing at runtime, authorization evaluation, and PGP signature verification are not implemented from this configuration. Those will be added separately later.

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `Source:Url` | string | _(none)_ | no | ISC canonical `control.ctl` URL (`https://downloads.isc.org/pub/usenet/CONFIG/control.ctl`) |
| `Source:InnUrl` | string | _(none)_ | no | INN GitHub `samples/control.ctl` URL |
| `Source:LastModified` | string | _(none)_ | no | Source `Last modified` date from `control.ctl` (`2023-08-05` for the current snapshot) |
| `Source:RetrievedFrom` | string | _(none)_ | no | Which URL was actually retrieved (`InnUrl` when the ISC copy is unavailable) |
| `Authorities` | array | `[]` | no | One entry per source hierarchy that publishes PGP authority metadata |

Each authority stores only public metadata present in the source: `Name`, `Contact`, `AdminGroup`, `Url`, `KeyUrl`, `KeyFingerprint`, `KeyMail`, `SyncableServer`, and explicit `Authorizations` (`Message`, `From`, `Newsgroups`, `VerificationIdentity`) from `verify-*` rules. Optional fields are omitted when the source does not provide them. Fingerprints are stored as uppercase hexadecimal without spaces. Private keys, passphrases, and armored public-key blocks are not stored; public key material can be retrieved later from `KeyUrl`.

An omitted or empty `Control` section is valid. NNTPD starts normally without this catalogue. Binding the section does not carry or process the listed hierarchies.

The current `appsettings.json` snapshot was taken from the INN source (`Last modified: 2023-08-05`) because `https://downloads.isc.org/pub/usenet/CONFIG/control.ctl` was unavailable.

## Bind addresses

`Nntpd:BindAddress` is a JSON array of one or more entries. Every entry is validated; none are silently dropped. Root-level `BindAddress` is not consumed by NNTPD.

| Form | Meaning |
|------|---------|
| `*` | Wildcard — all local interfaces |
| `0.0.0.0` | IPv4 any-address wildcard |
| `::` | IPv6 any-address wildcard |
| Explicit IPv4 / IPv6 | Must parse as an IP **and** be assigned to a local NIC |

Rules:

- Explicit addresses may be public or private; there is no “global address only” requirement.
- Unassigned but syntactically valid addresses are rejected with a clear configuration error.
- Invalid syntax is rejected.
- Wildcard entries do not require NIC assignment checks.
- NIC assignment is checked through an injectable probe (`ILocalIpAddressAssignee`) so tests stay deterministic.
- Configuration validation never opens listen sockets.

### Bind-address resolution for DNS

At startup, `IBindAddressResolver` expands `BindAddress` into the eligible IP set used for Cloudflare A/AAAA reconciliation:

| Entry | Expansion |
|-------|-----------|
| `*` / `+` | All eligible unicast addresses on local NICs (IPv4 and IPv6) |
| `0.0.0.0` | Eligible local IPv4 addresses only |
| `::` | Eligible local IPv6 addresses only |
| Explicit IP | That address, if eligible for DNS publication |

**Eligible** addresses may be public or private (including RFC1918). They must **not** be multicast, unspecified (`0.0.0.0` / `::` as a concrete address), loopback, or link-local (IPv4 `169.254.0.0/16`, IPv6 link-local). `IPAddress.IsGlobal` is **not** used as the sole eligibility test.

Addresses are deduplicated and classified as IPv4 or IPv6. The resolved set is the set that DNS must match—not an unrelated dump of every interface address when bind entries are explicit.

If the resolved set is empty, **startup fails** with a clear error. The host does not start with stale or empty DNS.

Example:

```json
"Nntpd": {
  "BindAddress": [ "*" ]
}
```

Multiple addresses:

```json
"Nntpd": {
  "BindAddress": [ "198.18.0.66", "2001:db8::1" ]
}
```

## ProxyHosts (HAProxy PROXY protocol)

`ProxyHosts` is an optional JSON array of **literal** IPv4/IPv6 addresses for trusted HAProxy peers. DNS names and CIDR/network prefixes are not supported.

| Situation | Behavior |
|-----------|----------|
| `ProxyHosts` omitted / `[]` | PROXY processing **disabled**. Effective client identity is always the TCP peer. No PROXY bytes are read. |
| TCP peer **matches** `ProxyHosts` | Peer is a trusted HAProxy source. PROXY protocol **v1 or v2 is required**. Effective client IP/port come from the PROXY header (or remain the TCP peer for UNKNOWN/LOCAL/UNSPEC per the PROXY specification). Malformed, incomplete, or timed-out headers **fail the connection** — there is no silent fallback to TCP identity. |
| TCP peer **does not match** `ProxyHosts` | Peer is **untrusted**. PROXY is **not** consumed and **cannot** influence identity. Effective client identity remains the TCP peer. |

### Mixed-mode product policy

VectorNNTP deliberately allows direct clients and trusted HAProxy peers on the same listener when `ProxyHosts` is non-empty:

- Trust is decided **only** from the accepted socket’s TCP peer address (never from PROXY contents).
- Untrusted peers never get PROXY parsing; a forged PROXY header cannot replace client IP or source port.
- If an untrusted peer sends octets that look like a PROXY v1/v2 header, those octets **remain in the application/TLS input stream** and are **not** interpreted as client identity. Once an NNTP session/command layer exists, they are ordinary application bytes (and will typically fail command parsing). On the TLS listener they are presented to `SslStream` as handshake input and normally fail the handshake.

This is **not** the same as an HAProxy-recommended exclusive PROXY port. The PROXY specification discourages sharing one listener between public clients and PROXY senders (“MUST NOT guess” whether a header is present). Operators who want exclusive HAProxy access should restrict the listen address/firewall to trusted proxy IPs in addition to configuring `ProxyHosts`.

Additional notes:

- IPv4-mapped IPv6 peer addresses match a configured IPv4 entry (and vice versa after canonicalization).
- Supported protocol: HAProxy PROXY protocol versions **1 and 2** (`docs/standards/haproxy/proxy-protocol.txt`).
- TLS backend ordering: TCP accept → PROXY (when the peer is trusted) → TLS handshake → NNTP.
- Connection-establishment PROXY gathering currently uses a large temporary buffer (up to the protocol maximum). That is a known allocation cost, not a steady-state data-path cost.

Example:

```json
"ProxyHosts": [ "198.51.100.10", "2001:db8::proxy" ]
```

## Transit named peers (top-level `Transit`)

Trusted feed peers are configured in a **top-level** `Transit` dictionary. Dictionary keys are protocol-safe **identifiers**. There is no nested `Transit:Peers` layer.

This is **peer authorization**, not ordinary user authentication. A unique IP-ACL match grants `AuthorizedTransit` + `StreamingPermitted` without `IsAuthenticated`, reader, or posting, and retains the named peer policy.

`AUTHINFO USER/PASS` is a **public** command (READER, STREAM, transit peers, and non-transit clients may issue it). Credential authority is the session MODE, not the source IP. `MODE STREAM` authenticates against that session's Transit peer credentials only (both configured `Username` and `Password` non-empty and ordinal match). `MODE READER` and an unspecified mode authenticate against the ordinary provider (newsmaster, then MySQL `nntpusers`) even when the client IP matches a Transit `AllowFrom`. There is no Transit ↔ MySQL fallback. AUTHINFO success is not Transit authorization: a READER authentication does not become a Transit peer.

| Situation | Behavior |
|-----------|----------|
| `Transit` omitted / `{}` | Deny-by-default. Sessions start with no transit/streaming privileges. |
| Effective client uniquely matches one peer's IP ACL | Session is that named Transit peer. Enables `MODE STREAM`, `CHECK`, `TAKETHIS`, `IHAVE`. `MODE STREAM` AUTHINFO uses that peer's credentials only. `MODE READER` AUTHINFO uses MySQL/newsmaster. |
| Effective client matches no peer | Same as empty dictionary for that connection. AUTHINFO uses the ordinary authentication provider unless `MODE STREAM` was accepted (then Transit auth fails without MySQL). |
| Effective client matches more than one peer | Transit is **denied**. A WARNING is logged on **every** such connection (`source IP` + matching peer names). Literal/CIDR and duplicate-hostname overlap is rejected at configuration validation; residual DNS-vs-literal overlap is still denied at identification time. |

`Nntpd:Transit:StreamOutstandingArticleDepth` is unrelated peer policy: it only bounds concurrent outbound STREAM article TX operations (valid `4–16`).

## SPEEDTEST diagnostic (`Nntpd:SpeedTest`)

`SPEEDTEST <identifier>` is a VectorNNTP extension. `<identifier>` is the configured Transit dictionary key, never `PeerName` and never a client-supplied host or port. Limits apply only to this diagnostic and do not change TAKETHIS/IHAVE/CHECK/STREAM.

Example:

```json
"Nntpd": {
  "SpeedTest": {
    "MaxDurationSeconds": 10,
    "MaxBytes": 67108864,
    "MaxConcurrent": 2,
    "MaxConcurrentPerPeer": 1
  }
}
```

Outbound peer connections from `ConnectTo` are not opened by SPEEDTEST. See `docs/architecture.md` (SPEEDTEST diagnostic).

### Identifier and PeerName

The JSON key is the **identifier**: a stable protocol/machine identity used by `SPEEDTEST <identifier>` and authorization. Identifiers are **not** normalized (no case-folding, hyphenation, or lowercasing). Two identifiers that differ only by case remain distinct.

Identifier grammar: **1–256 visible ASCII characters** (`0x21–0x7E`). No space, TAB, other whitespace, or control characters. No Unicode. The exact configured string is the identity.

`PeerName` is the required human-readable administrative name. It is **not** derived from the identifier and is **not** a SPEEDTEST command argument. Spaces, commas, punctuation, and printable Unicode are allowed (`Giganews, Inc.`, `Blueworld Hosting`). A PeerName must be non-empty, not whitespace-only, at most 256 characters, and must not contain control characters. The configured string is preserved exactly.

Example:

```json
"Transit": {
  "usenet-ninja": {
    "PeerName": "Usenet Ninja",
    "MaxIncomingConnections": 10,
    "MaxOutgoingConnections": 10,
    "AllowFrom": [ "198.18.0.0/15" ]
  }
}
```

Command: `SPEEDTEST usenet-ninja`  
Result fields: `PEER=usenet-ninja` and `PEERNAME=Usenet Ninja`.

### Peer fields

| Field | Type | Default | Required? | Description |
|-------|------|---------|-----------|-------------|
| `PeerName` | string | _(none)_ | **yes** | Human-readable administrative display name. Preserved exactly. Not a protocol identifier. |
| `MaxIncomingConnections` | int | _(none)_ | **yes** | Cluster-wide max simultaneous inbound connections for this peer's `Identifier` (`0–4096`). Redis is authoritative. Source IP is ACL identity only; it is not the limit key. Counted only after peer identification. A later READER AUTHINFO releases that slot. `0` means closed (reject all new inbound connections), not unlimited. Lowering the limit does not disconnect existing sessions; new admits are rejected until cluster usage falls below the new limit. |
| `MaxOutgoingConnections` | int | _(none)_ | **yes** | Future outbound connection limit (`0–4096`). Stored and validated only; this host does not open outbound sockets from `ConnectTo`. |
| `AllowFrom` | string array | `[]` | no | Inbound source ACL. Empty means the peer cannot match inbound clients (outbound-only policy). |
| `ConnectTo` | string array | `[]` | no | Outbound endpoints with an **explicit** port (`host:port` or `[IPv6]:port`). Parsed only. |
| `Username` | string | _(none)_ | **yes** (for Management) | Broker / Management Basic-auth username. Required with `Password` for Management API availability discovery. Supply via `VECTOR__RABBITMQ__USERNAME` |
| `Password` | string | _(none)_ | **yes** (secret; for Management) | Broker / Management Basic-auth password. Supply via `VECTOR__RABBITMQ__PASSWORD` or secrets. Never commit or log |
| `Ssl` | string | `""` | no | Blank = no TLS; `TLS` = native TLS; `STARTTLS` = upgrade. Case-insensitive; invalid values fail validation. |
| `Patterns` | string | `*` | no | One newsfeeds(5) / `uwildmat_poison` subscription expression (comma-separated string, not a JSON array, regex, or .NET glob). |
| `DeferOnDuplicate` | bool | `true` | no | Stored for later CHECK/IHAVE in-flight duplicate handling (`431`/`436` vs `438`/`435`). CHECK HistoryDB itself is implemented separately. |
| `PathToken` | string | `""` | no | Exact token reserved for outbound Path-header loop prevention. Not a DNS name or IP; not case-folded. Empty is allowed. **Not consumed** by article-routing code yet. Max 255 characters; no control characters. |
| `MaxSize` | long | `10485760` | no | Peer incoming-article size policy in bytes (`1–2147483647`). Stored only; not wired into TAKETHIS ingestion. Distinct from `Nntpd:ArticleIngestion:MaxArticleBytes`. |
| `MessageTypes` | string array | `["default"]` (when omitted or empty) | no | Diablo article-type names (see below). Not regex, MIME types, or newsgroup Patterns. Classification is not implemented. |

### AllowFrom

Each entry is one of:

- DNS hostname: `news.example.net` (resolved to A/AAAA; all current addresses are authorized)
- IPv4 address: `192.0.2.10`
- IPv4 prefix: `192.0.2.0/24`
- IPv6 address: `2001:db8::10`
- IPv6 prefix: `2001:db8:1234::/48`

Private, ULA, documentation, and lab addresses are accepted. `IPAddress.IsGlobal` is not used.

AllowFrom is **materialized into an IP-only runtime ACL** before connection handling:

```text
configured AllowFrom
       │
       ├── literal IP/prefix → IP ACL
       │
       └── DNS hostname
                ↓
           DNS resolution (refresh layer)
                ↓
         resolved IP addresses
                ↓
             IP ACL
```

Connection-time identification is **IP-only**. A connection never triggers DNS resolution, reverse DNS, hostname lookup, or other network I/O for ACL matching.

DNS resolution (refresh layer only):

- Lookups are asynchronous (DnsClient A + AAAA), ahead of connection handling.
- Record TTLs are honoured when DnsClient exposes `TimeToLive` on answers.
- Refresh is never more frequent than **60 seconds**. If TTL is greater than 60 seconds, the TTL is used; if TTL is less than 60 seconds or unavailable, the 60-second floor applies.
- A refresh atomically replaces that hostname's address set.
- Transient failures (timeout, SERVFAIL, transport error) keep the last valid set.
- NXDOMAIN / NOERROR with no A/AAAA clears the set.
- Temporary DNS failure must not erase a previously valid set.
- Every failed resolution attempt logs WARNING with the peer identifier, PeerName, hostname, and reason. Repeated failures are not suppressed. Passwords are never logged.

Matched against `ConnectionClientIdentity.ClientAddress` (PROXY-reported source when the TCP peer is a trusted `ProxyHosts` entry). **Not** the same as `ProxyHosts`.

### MessageTypes

`MessageTypes` is a JSON string array of Diablo `ArtTypeConv` names (case-insensitive, surrounding whitespace ignored). Unknown values fail validation. Duplicates are OR-ed (harmless). `binary` and `binaries` map to the same flag. `pgp` maps to `PgpMessage`. `all` is the union of every concrete flag. `default` and `none` are distinct.

Recognised names: `none`, `default`, `control`, `cancel`, `mime`, `binary`/`binaries`, `uuencode`, `base64`, `yenc`, `bommanews`, `unidata`, `multipart`, `html`, `ps`, `binhex`, `partial`, `pgp`, `all`.

This is stored peer policy only. Articles are not classified yet.

### ConnectTo / Ssl

`ConnectTo` requires an explicit port. Defaults are not inferred from `Ssl` because this host's own TLS listen port is independently configurable (not assumed to be 563). Examples: `news.example.net:119`, `192.0.2.10:119`, `[2001:db8::10]:563`. No outbound connection is established from this setting.

### Patterns

`Patterns` is a newsfeeds(5) expression compiled with INN `uwildmat_poison` semantics (see [libinn-uwildmat](https://www.eyrie.org/~eagle/software/inn/docs/libinn-uwildmat.html) and [newsfeeds(5)](https://www.eyrie.org/~eagle/software/inn/docs/newsfeeds.html)):

- The complete newsgroup name is matched (anchored).
- Comma separates patterns; `\,` is a literal comma.
- The rightmost matching pattern wins.
- `!` excludes; `@` poisons.
- `*` any sequence, `?` one character, `[...]` / `[^...]` sets, `\` escapes.
- Empty or invalid expressions fail configuration validation.

The matcher is stored on the peer policy. Article-ingestion routing does not apply Patterns yet.

### Hot reload

The entire top-level `Transit` section reloads through `IOptionsMonitor` when `appsettings.json` changes (Generic Host change tokens; no custom file poll). Adding, removing, or modifying a peer replaces the active immutable snapshot atomically. New connections use the new snapshot. Existing connections are not disconnected solely because configuration changed. Invalid reloads are ignored; the last valid snapshot remains.

Example:

```json
"Transit": {
  "news-example": {
    "MaxIncomingConnections": 10,
    "MaxOutgoingConnections": 2,
    "AllowFrom": [
      "news.example.net",
      "192.0.2.0/24",
      "2001:db8:1234::/48"
    ],
    "ConnectTo": [ "news.example.net:563" ],
    "Username": "",
    "Password": "",
    "Ssl": "TLS",
    "Patterns": "*",
    "DeferOnDuplicate": true,
    "PathToken": "peer.example",
    "MaxSize": 10485760,
    "MessageTypes": [ "default" ]
  }
}
```

```json
"Nntpd": {
  "Transit": {
    "LogTrash": true,
    "StreamOutstandingArticleDepth": 8,
    "WantTrash": true
  }
}
```

`WantTrash` / `LogTrash` are site-wide `Nntpd:Transit` settings (inn.conf-style), not per-peer keys in the top-level `Transit` dictionary. Accepted `+`/`j` events are emitted after dequeue; rejected `-` events and moderated `m` events are emitted at the IHAVE/TAKETHIS/POST decision. See [logging.md](logging.md#inn-news-log).

## TCP ports

NNTPD bind ports come only from the `Nntpd` section.

| Setting | Default | Valid values | Behavior |
|---------|---------|--------------|----------|
| `Nntpd:BindPort` | `119` | `1–65535` | Missing → default; invalid → hard startup failure |
| `Nntpd:BindPortTls` | `0` | `0` or `1–65535` | `0` / unset → TLS disabled (`IsTlsListenerEnabled == false`); `1–65535` → TLS enabled |
| `AllowCleartextAuth` | `true` | bool | Missing → default `true` (cleartext AUTHINFO USER/PASS permitted) |

Negative values and values above `65535` fail validation. Dependents use `BindPortTls` / `IsTlsListenerEnabled` to distinguish disabled vs enabled TLS listener configuration.

## Cleartext AUTHINFO (`AllowCleartextAuth`)

`AUTHINFO USER/PASS` and password-oriented SASL (PLAIN, LOGIN) present clear-text credentials at the NNTP protocol layer. RFC 4643 requires implementations that offer AUTHINFO PASS to also support TLS, and deprecates using the password command without a strong encryption layer. It does **not** prohibit cleartext use; servers SHOULD offer configuration to disable weak authentication without TLS. When `AllowCleartextAuth` is `false` and TLS is inactive, AUTHINFO SASL is also rejected with `483` and is not advertised.

VectorNNTP policy:

| Connection | `AllowCleartextAuth` | AUTHINFO USER/PASS |
|------------|----------------------|--------------------|
| TLS active | any | permitted |
| TLS inactive | `true` (default) | permitted |
| TLS inactive | `false` | rejected with `483`; CAPABILITIES does not advertise `AUTHINFO USER` |

Prefer TLS when transmitting passwords. Setting `AllowCleartextAuth` to `true` does not make cleartext authentication “secure”; it only allows the mechanism on the cleartext port when operators need it.

## TLS and ACME (Let's Encrypt)

TLS is controlled exclusively by `BindPortTls`:

| `BindPortTls` | TLS | ACME |
|---------------|-----|------|
| unset / `0` | disabled | **completely disabled** — no account registration, no Let's Encrypt calls, no certificate requirement, existing files under `AcmeStateDir` are not touched |
| `1–65535` | enabled | required — account + usable certificate must exist before the application enters `Running` |

### ACME settings

NNTPD-owned ACME directory URL, renewal threshold, and state directory bind from the `Nntpd` section. Root-level copies of those keys are not applied. The ACME account email is the shared `VECTOR__ACMEACCOUNT` environment variable; the PKCS#12 password remains the shared `VECTOR__ACMECERTIFICATEPASSWORD` environment variable. There is no NNTPD-specific email or password variable.

| Setting | Default | Notes |
|---------|---------|-------|
| `AcmeDirectoryUrl` | `https://acme-staging-v02.api.letsencrypt.org/directory` | **Staging** by default. Production requires an explicit override such as `https://acme-v02.api.letsencrypt.org/directory`. |
| `VECTOR__ACMEACCOUNT` | _(none)_ | Required only when TLS is enabled. Shared Common ACME account contact email. Must be a plausible contact email. |
| `AcmeStateDir` | `certs/` | Persistent ACME state root (relative or absolute path). NNTPD binds this from `Nntpd:AcmeStateDir`. Relative paths resolve against `AppContext.BaseDirectory`. Both applications may point at the same physical directory. |
| `LogDir` | `logs/` | Serilog daily file-log root, dedicated Serilog `news` file directory, and dedicated Serilog Path-survey (`inpaths`) file directory (relative or absolute path). Created at logging startup if missing. |
| `AcmeRenewalThresholdDays` | `30` | Certificate is due for renewal when `now >= NotAfter - threshold`. |
| `AcmeCertificatePassword` | _(none)_ | Required only when TLS is enabled. Protects `certificate.pfx`. |

Do **not** commit real emails, PFX passwords, production directory URLs tied to live accounts, or machine-specific absolute paths into tracked `appsettings.json`.

Example (TLS enabled against staging — values are illustrative; supply secrets via environment / user secrets):

```json
"Nntpd": {
  "BindPortTls": 563,
  "AcmeDirectoryUrl": "https://acme-staging-v02.api.letsencrypt.org/directory",
  "AcmeStateDir": "certs/",
  "LogDir": "logs/",
  "AcmeRenewalThresholdDays": 30
}
```

```text
VECTOR__ACMEACCOUNT=ops@example.org
VECTOR__ACMECERTIFICATEPASSWORD=<secret>
```

Production directory (explicit only, under `Nntpd`):

```json
"Nntpd": {
  "AcmeDirectoryUrl": "https://acme-v02.api.letsencrypt.org/directory"
}
```

Staging certificates are **not** trusted by normal clients. Use staging for integration testing; switch the directory URL only when ready for a production CA.

### Certificate identities (SANs)

NNTPD TLS certificates must include exactly these DNS names (no wildcards, no extras):

1. The generated `{Fqdn}` (for example `nntpd01.usenet.ninja`)
2. `news.usenet.ninja`

BackFiller requests only its canonical FQDN `backfiller{ServerId:00}.{DnsSuffix}` (for example `backfiller01.usenet.ninja`) and must not include `news.usenet.ninja`. StorageServer likewise requests only `cache{ServerId:00}.{DnsSuffix}` (for example `cache01.usenet.ninja`) without `news.usenet.ninja`. The ACME implementation is shared; only the requested name set is application-specific. The `nntpd`, `backfiller`, and `cache` prefixes are fixed application identity, not configuration.

Requested names must fall under `DnsSuffix` (label-boundary zone coverage) because DNS-01 challenges are published in the single Cloudflare zone identified by `CloudFlareZoneId`.

### Challenge mechanism

ACME uses **DNS-01** only (Cloudflare TXT records named `_acme-challenge.{domain}`). HTTP-01 and TLS-ALPN-01 are not used. Challenge TXT records use TTL `120` and `proxied=false`. Transient DNS-01 recovery state lives under `{AcmeStateDir}/live/{fqdn}/dns01/` and is inspected only for that certificate identity. The persistent historical record is `{AcmeStateDir}/journal/{fqdn}.json` (one file per FQDN). Authoritative TXT visibility is checked before challenges are triggered. After triggers, the issuer polls ACME authorization/order state until the order is `ready` (or fails/times out) before finalization — DNS visibility is not treated as ACME validation success. ACME challenge TXT must not be confused with A/AAAA FQDN reconciliation — reconcile mutates only A/AAAA for `{Fqdn}`; challenge records use distinct `_acme-challenge.*` names.

### Persistence

The Windows Certificate Store is **not** used (`X509Store` is not employed).

| Path | Format |
|------|--------|
| `{AcmeStateDir}/account/private_key.der` | PKCS#8 DER **ACME account private key** (common to every certificate identity) |
| `{AcmeStateDir}/account/registration.json` | Account URI + directory URL metadata |
| `{AcmeStateDir}/journal/{fqdn}.json` | Persistent ACME transaction journal for one FQDN (complete historical lifecycle) |
| `{AcmeStateDir}/live/{fqdn}/dns01/{entryId}.json` | Transient DNS-01 recovery state for one in-flight FQDN |
| `{AcmeStateDir}/live/{fqdn}/current` | Active generation id for that FQDN |
| `{AcmeStateDir}/live/{fqdn}/.issuance.lock` | Cross-process issuance/promotion lock for that FQDN |
| `{AcmeStateDir}/live/{fqdn}/gens/{id}/certificate.pfx` | PKCS#12/PFX: leaf certificate + private key + issuing chain |
| `{AcmeStateDir}/live/{fqdn}/gens/{id}/complete` | Marker written after PFX write → reload → validate succeeds |

`certificate.pfx` is the canonical TLS server credential. It is loaded with `AcmeCertificatePassword` into an `SslStreamCertificateContext` (leaf + chain) for the implicit TLS listener. Incomplete generations (missing `complete` or invalid `current`) are never treated as active. A known-good generation remains current until a replacement PFX is validated and committed.

The persistent journal is **not** the DNS-01 recovery directory. `{AcmeStateDir}/journal/{fqdn}.json` is one growing file per certificate identity. Each request is a transaction with lifecycle events (DNS create/remove, ACME validation, issuance, persist/promote, or failure). Historical transactions are retained. `{AcmeStateDir}/live/{fqdn}/dns01/` exists only to recover an in-flight TXT record and may be deleted after cleanup. Recovery for one FQDN never inspects another FQDN's recovery directory and never deletes another identity's Cloudflare TXT record. Orphan TXT cleanup records `dns_challenge_recovered_and_removed` in the historical journal. That event is attached to the original transaction only when the recovery file persisted that transaction id; otherwise it is stored as unattributed history and is not attached to a later request.

Authoritative live state is only `{AcmeStateDir}/live/{fqdn}/` (`current`, `.issuance.lock`, `dns01/`, `gens/`). Production code does not create `{AcmeStateDir}/live/gens`, `{AcmeStateDir}/live/current`, `{AcmeStateDir}/journal/acme/`, `{AcmeStateDir}/journal/dns01/`, or `{AcmeStateDir}/dns01/journal/`. Generation GC, the current pointer, and the issuance lock are FQDN-scoped: NNTPD and BackFiller may share one physical `certs` root without touching each other's live state.

### Lifecycle

When TLS is enabled, `AcmeCertificateService` runs after Cloudflare DNS reconciliation:

1. Ensure ACME account (reuse persisted key / register once)
2. Evaluate existing certificate (SANs, validity, key match, renewal threshold)
3. Issue or renew via ACME when needed
4. Publish an immutable TLS certificate context for the TLS listener (atomic swap on renewal)

Failure to obtain a usable certificate prevents `Running`. When TLS is disabled, the service is idle and performs no ACME work. Shutdown does not contact Let's Encrypt.

Background renewal checks run every 6 hours while the service is running (startup also evaluates the 30-day threshold).

## Cloudflare

Cloudflare DNS integration is **mandatory** for this host. Startup fails when either credential is missing, null, empty, or whitespace:

- `CloudFlareApiKey` — secret API token (**required**); sent as `Authorization: Bearer`
- `CloudFlareZoneId` — zone id for the DNS zone (**required**)
- `DnsSuffix` — DNS suffix expected for that zone (default `usenet.ninja`; syntax-validated locally)

There is **no** silent disable path and **no** skip of DNS integration for missing credentials. Validation runs at host startup (`ValidateOnStart` + `NntpdOptionsValidator`) before the application enters `Running`. Configuration validation does **not** call the Cloudflare API.

**Never commit API keys.** Do not put `CloudFlareApiKey` in `appsettings.json`, samples, docs, tracked files, logs, exception messages, or options dumps. Validation and API failure messages name the operation and HTTP/Cloudflare error details; they never include the secret value. Authenticated request headers are not logged. There is no restrictive API-token format validator.

### DNS reconciliation (startup) and FQDN cleanup (shutdown)

This host is **authoritative for the entire configured `{Fqdn}`** inside `CloudFlareZoneId`. Ownership is the **exact** DNS name only (not the zone, not a textual suffix, not parent or child names such as `other.{Fqdn}`).

After options validation, Common `CloudflareDnsReconciliationApplicationService` (a thin `IApplicationService` adapter around `CloudflareDnsReconciliationService`) is registered **first** among application services in NNTPD and BackFiller:

- **Start:** reconciles and verifies A/AAAA before other services start. The host reaches `Running` only when reconciliation **and verification** succeed for **both** A and AAAA.
- **Stop (reverse order):** after other application services stop (future listeners stop accepting connections first), cleanup **deletes every DNS record for the exact `{Fqdn}`**, regardless of type (A, AAAA, CNAME, TXT, MX, SRV, CAA, HTTPS, SVCB, and any other types returned by Cloudflare for that exact name), including duplicates. Success requires a post-delete list showing **no** remaining records for that exact name.
- Parent-domain, child/subdomain, similarly spelled hostnames, and unrelated zone configuration are **never** deleted.

#### Startup reconcile steps

1. Resolve eligible bind addresses (`IBindAddressResolver`). Empty set → startup failure.
2. Reconcile Cloudflare DNS for the generated `{Fqdn}` in `CloudFlareZoneId`:
   - **A** records = exactly the resolved IPv4 set (remove all A records when that set is empty).
   - **AAAA** records = exactly the resolved IPv6 set (remove all AAAA records when that set is empty).
3. **Staged mutations (cross-family):** list A and AAAA, then **create all missing desired records for both families**, then update kept desired records that are not yet managed (`ttl=300`, `proxied=false`) in place, then **delete stale/duplicate records for both families**, then re-list and verify. Create-all-before-delete prefers a temporary address **superset** over a temporary gap when replacing addresses. Create-before-delete does **not** eliminate intermediate visibility. Address changes are **not** performed as broad update-in-place of stale content.
4. Skip mutations when records already match (exact normalized IP set, TTL **300**, and DNS-only).
5. **Verify** by listing again; success requires exact A and AAAA content sets, **TTL 300**, and **`proxied=false`** on every remaining record for `{Fqdn}`. Mismatch, API failure, cancellation, or uncertain mutation → attempt failure (never treated as success).
6. During startup reconcile, only A/AAAA for the exact `{Fqdn}` are mutated. Other names and non-A/AAAA types are untouched until shutdown cleanup.

**Managed record policy:** every published A/AAAA record must have content equal to a desired address, `ttl=300`, and `proxied=false`. Creates use these attributes; retained desired records with wrong TTL and/or proxy state are corrected with a single update.

List responses fail closed when pagination metadata is missing/inconsistent (including missing `result_info` / `total_pages`, unstable `total_pages`, page mismatches, or more than 20 pages). A returned `zone_id` that is empty or does not match the requested zone is a permanent failure; omitted `zone_id` is accepted (path zone is authoritative). Malformed required A/AAAA fields (`id`, `type`, `content`, `ttl`, `proxied`) fail closed and must not drive mutations.

If reconcile fails after work has begun, the service attempts an authoritative cleanup of the exact FQDN before failing startup. That cleanup is **best-effort** with a dedicated **15-second** budget (not unbounded and not `CancellationToken.None`). Cleanup timeout/failure is logged without claiming success and **does not replace** the original startup exception. Partial startup that successfully completed DNS reconcile then fails a later service triggers manager rollback, which calls `StopAsync` and removes the FQDN.

#### Shutdown cleanup steps

1. List all DNS records for the exact `{Fqdn}` (all types, paginated); classify names with case-insensitive, trailing-dot-normalized equality. Ambiguous names fail safely without deleting outside the ownership boundary.
2. Delete each matching record by Cloudflare record id (never invent ids).
3. Re-list and **verify** zero records remain for the exact name.
4. Partial deletion is never reported as successful cleanup. Bounded retries (same 3-attempt / 200–400 ms backoff policy as reconcile) re-read remote state before continuing. Permanent auth failures fail immediately.
5. Cleanup cooperates with `GracefulShutdownTimeout` / cancellation: if the budget expires or Cloudflare is unavailable, cleanup fails visibly (logged / thrown via the service manager) and **does not claim** the FQDN was removed. Forced process kill may skip cleanup entirely.

**Distinctions:**

| Claim | Meaning |
|-------|---------|
| Cleanup attempted | Deletes were issued; outcome may be partial or uncertain |
| Cloudflare confirmed empty | Post-delete API list shows no exact-FQDN records |
| Recursive caches expired | **Not** claimed — resolvers may still answer from TTL cache |

**Private IP publication is intentional.** RFC1918 IPv4 and ULA IPv6 addresses that appear in the resolved bind set are published as A/AAAA content. They are not filtered out. Operators must accept the operational implications of publishing private addresses in public DNS.

#### Non-atomic Cloudflare updates

Cloudflare’s DNS Records API does **not** provide an atomic transaction spanning multiple record mutations. Therefore:

- External DNS consumers may observe an **intermediate** state while A/AAAA creates and deletes are in flight (for example desired A present while AAAA is still missing, or briefly both desired and stale addresses), and while shutdown deletes multiple record types.
- The application **fails closed**: any failed create/update/delete/verify, cancellation, or uncertain mutation outcome prevents `Running` (startup) or is reported as cleanup failure (shutdown). Partial success is never reported as reconcile or cleanup success.
- Recovery is **re-read and converge** (startup) or **re-read and continue deleting** (shutdown). There is no unsafe compensating “rollback” that invents a prior DNS state. Up to **3** attempts with backoff (**200 ms**, then **400 ms**) run within a single call; process crash mid-operation is recovered on the next successful startup (reconcile) or leaves records until an operator/process cleans them (if shutdown cleanup did not finish).
- Each reconcile/cleanup call shares one **`CloudFlareOperationTimeout`** wall-clock budget (default **2 minutes**) with the caller’s cancellation token; the earlier deadline wins. That budget is created **once** for the call and is **not** reset on individual HTTP attempts or reconciler retries.
- Each HTTP attempt is further bounded by **`min(30 seconds, remaining operation budget)`** via a per-request cancellation token (`CloudflareDnsClient.PerRequestTimeout`). The registered `HttpClient.Timeout` is infinite so it cannot outlive a short remaining budget; stall protection comes from the per-request token.
- Nested HTTP 429 retries (up to 3 retries per request, `Retry-After` honored but capped at 2 minutes and **never slept** when it exceeds the **remaining** budget) and reconciler attempt backoffs observe the shared deadline. Budget exhaustion and caller cancellation are failures — never success.
- **Distinguish timeouts:** caller/operation-budget cancellation propagates as `OperationCanceledException` (mutations are logged as outcome-uncertain; the next re-read recovers). A per-request/transport timeout **without** operation cancellation becomes `CloudflareDnsException` with `IsOutcomeUncertain` only for mutations (reads are not marked uncertain).
- Normal shutdown cleanup remains governed by **`GracefulShutdownTimeout`** (linked cancellation). Failed-start cleanup is separately capped at **15 seconds** and never uses an unbounded token.
- **Permanent** failures (HTTP 4xx except 429; missing API key; known Cloudflare auth codes on HTTP 200 + `success: false`; malformed list payloads / pagination / zone mismatch) fail immediately without reconciler retries.
- Concurrent reconcile and cleanup calls on the **same reconciler instance** are serialized (shared gate). `ApplicationServiceManager` rejects concurrent start/stop. Multiple processes or external DNS managers targeting the same zone/FQDN are **not** coordinated; the host re-reads and fails closed on verification mismatch rather than claiming success.
- Exception messages use HTTP status and sanitized Cloudflare error codes/messages. They **do not** embed raw response bodies, API tokens, or authorization headers.

**API permissions:** a Cloudflare API token with **Zone → DNS → Edit** (DNS Write) on the target zone. List operations need DNS Read (included in Edit).

**HTTP 429:** retried a limited number of times **per HTTP request** using `Retry-After` when present (capped), otherwise short exponential backoff, always within the remaining operation budget. Exhausted rate limits fail the current attempt (reconciler may still retry the full attempt if the failure is not marked permanent and budget remains).

**Propagation:** verification is against the Cloudflare API view of authoritative records. It does **not** guarantee immediate global recursive-resolver propagation or instant cache expiry after cleanup.

#### Relation to sockets (current phase)

NNTP listeners bind configured `BindAddress` entries on `BindPort` (plain) and, when TLS is enabled, `BindPortTls` (implicit TLS after ACME publishes a certificate context). DNS reconciliation publishes the **resolved eligible bind-address set** derived from `BindAddress` and local NIC enumeration — that set is related to, but not identical to, listen wildcards (for example `*` expands differently for DNS vs dual-stack listen). Do not interpret startup DNS success alone as proof that sockets are listening; listener start is a separate application-service step after Cloudflare reconciliation.

**Runtime:** A/AAAA are reconciled at **startup**; the exact FQDN is removed at **shutdown**. NIC address changes and configuration reloads are not monitored while Running. If the eventual listen set diverges after start, DNS can drift until the next successful startup reconciliation.

### Environment variables

Use these exact names:

```text
VECTOR__CLOUDFLAREAPIKEY
VECTOR__CLOUDFLAREZONEID
VECTOR__ACMECERTIFICATEPASSWORD
VECTOR__ACMEACCOUNT
VECTOR__RABBITMQ__USERNAME
VECTOR__RABBITMQ__PASSWORD
NNTPD__SERVERID
```

Names are uppercase on Windows and Linux. Values are case-sensitive and must be passed unchanged.

Example (user scope, PowerShell — replace secret values locally; do not commit them):

```powershell
[Environment]::SetEnvironmentVariable("VECTOR__CLOUDFLAREAPIKEY", "<YOUR_API_KEY>", "User")
[Environment]::SetEnvironmentVariable("VECTOR__CLOUDFLAREZONEID", "5811a29d39a0732afb5f160c9b137c3d", "User")
[Environment]::SetEnvironmentVariable("NNTPD__SERVERID", "1", "User")
[Environment]::SetEnvironmentVariable("VECTOR__ACMECERTIFICATEPASSWORD", "<YOUR_PFX_PASSWORD>", "User")
[Environment]::SetEnvironmentVariable("VECTOR__ACMEACCOUNT", "ops@example.org", "User")
[Environment]::SetEnvironmentVariable("VECTOR__RABBITMQ__USERNAME", "<YOUR_RABBITMQ_USERNAME>", "User")
[Environment]::SetEnvironmentVariable("VECTOR__RABBITMQ__PASSWORD", "<YOUR_RABBITMQ_PASSWORD>", "User")
```

`DnsSuffix` should correspond to the zone identified by `CloudFlareZoneId`. The host validates DNS suffix **syntax** only; it does not verify zone membership via the Cloudflare API.

## ServerId (required)

`ServerId` has **no default**. It must be configured explicitly as an integer from `1` through `255` (for example via `Nntpd:ServerId` / `NNTPD__SERVERID`, `BackFiller:ServerId`, or `StorageServer:ServerId`). Missing, null, unparsable, `0`, negative, or greater-than-`255` values cause a hard startup failure. A missing value is distinguishable from an explicit `0` (both fail). The CLR default must not make an omitted setting appear valid (`int?` remains unset until configured). Host labels use invariant `D2` formatting (`01`…`09`, then `10`…`255`).

## StorageServer filesystem roots

StorageServer keeps application logs, the NVMe control tier, and the SATA segment tier under separate roots:

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `StorageServer:LogDir` | string | `/logs` | no | Serilog daily rolling application log directory. Absolute by default (`/logs`). Relative paths (if configured) resolve with Common `ApplicationLocalPath.ResolveApplicationLocalPath` against `AppContext.BaseDirectory`; absolute paths stay absolute and are not rewritten under the application base. Distinct from `CacheDir` and `Storage:ControlDir`. |
| `StorageServer:CacheDir` | string | `cache/` | no | SATA / segment-data root for append-oriented immutable article segments. Relative paths resolve with Common `ApplicationLocalPath.ResolveApplicationLocalPath` against `AppContext.BaseDirectory`. Absolute paths stay absolute. The directory need not exist at validation time; StorageServer must not create cache contents merely to validate configuration. Distinct from `LogDir` and `Storage:ControlDir`. On-disk segment layout under this root is owned by the storage engine (not declared by configuration alone). |
| `StorageServer:Storage:ControlDir` | string | `control/` | no | NVMe control/durability root for the journal, article index, segment catalogue, and storage telemetry. Relative paths resolve with Common `ApplicationLocalPath.ResolveApplicationLocalPath` against `AppContext.BaseDirectory`. Absolute paths stay absolute. Need not exist at validation time; validation must not create contents. Distinct from `LogDir` and `CacheDir`. |
| `StorageServer:Storage:JournalSoftLimitBytes` | long | `67108864` (64 MiB) | no | Journal outstanding-bytes soft limit. At or above this threshold, write pressure becomes Elevated. |
| `StorageServer:Storage:JournalHardLimitBytes` | long | `134217728` (128 MiB) | no | Journal outstanding-bytes hard limit. At or above this threshold, write pressure is Critical and Accept rejects new writes. Must be ≥ soft limit. |
| `StorageServer:Storage:SegmentTargetSizeBytes` | long | `268435456` (256 MiB) | no | Target closed-segment size hint for future rollover. Not enforced by Phase-1 memory fakes. |
| `StorageServer:Storage:ArticleCache:MaxBytes` | long | `0` | no | Process-local RAM article-cache ArtData capacity. `0` disables the cache (Gets miss; Puts do not retain). Must be ≥ `0`. Non-durable; restart clears the cache. Not wired into the durable storage engine in Phase 3A. |
| `StorageServer:Storage:Compaction:Enabled` | bool | `false` | no | When `false`, compaction victim selection returns no Closed victim. Does not schedule or execute compaction. Independent of `MaintenanceEnabled`. |
| `StorageServer:Storage:Compaction:MaintenanceEnabled` | bool | `false` | no | When `false`, `StorageMaintenanceService` does not invoke the coordinator. When `true`, the worker runs after `StorageEngine` recovery (immediate first run, then `Interval` delay after each completion). May be `true` while `Enabled` is `false` so durable retirement/reclamation/open-compaction work can finish without selecting new Closed victims. |
| `StorageServer:Storage:Compaction:Interval` | TimeSpan | `00:01:00` | no | Minimum delay after each `RunOnceAsync` completes before the next run. Must be greater than `00:00:00`. Zero/negative values are rejected. |
| `StorageServer:Storage:Compaction:MinimumDeadBytes` | long | `67108864` (64 MiB) | no | Absolute dead-byte floor for Closed-segment compaction eligibility. Must be ≥ `0`. Combined with `MinimumDeadRatio` (AND). |
| `StorageServer:Storage:Compaction:MinimumDeadRatio` | double | `0.10` | no | Minimum `DeadBytes / SizeBytes` for Closed-segment compaction eligibility. Must be finite in `[0, 1]`. Combined with `MinimumDeadBytes` (AND). When `Enabled` is `true` and both thresholds are `0`, every Closed segment with `SizeBytes > 0` is eligible (including zero-dead). |

Do not point `CacheDir` or `ControlDir` at the log directory. Do not nest logs under `cache/` or `control/` by default.

Article-storage Phase 1 establishes engine contracts and in-memory fakes only. It does not select a database engine, write real journal/segment files, implement compaction/reclamation, or wire VATP OPEN/PUT.

## Generated FQDN

Each application computes one canonical FQDN from its fixed prefix, validated `ServerId`, and `DnsSuffix`. The prefix is application identity, not configuration. There is no `Nntpd:Name`, `BackFiller:Name`, or `StorageServer:Name`.

```text
NNTPD:         nntpd{ServerId:00}.{DnsSuffix}
BackFiller:    backfiller{ServerId:00}.{DnsSuffix}
StorageServer: cache{ServerId:00}.{DnsSuffix}
```

There is **no** dot between the prefix and the id. Output is invariant lowercase. Applications never share a hostname for the same `ServerId` / `DnsSuffix`.

| `ServerId` | NNTPD | BackFiller | StorageServer |
| ---------: | ----- | ---------- | ------------- |
| 1 | `nntpd01.usenet.ninja` | `backfiller01.usenet.ninja` | `cache01.usenet.ninja` |
| 8 | `nntpd08.usenet.ninja` | `backfiller08.usenet.ninja` | `cache08.usenet.ninja` |
| 9 | `nntpd09.usenet.ninja` | `backfiller09.usenet.ninja` | `cache09.usenet.ninja` |
| 10 | `nntpd10.usenet.ninja` | `backfiller10.usenet.ninja` | `cache10.usenet.ninja` |
| 12 | `nntpd12.usenet.ninja` | `backfiller12.usenet.ninja` | `cache12.usenet.ninja` |
| 99 | `nntpd99.usenet.ninja` | `backfiller99.usenet.ninja` | `cache99.usenet.ninja` |
| 255 | `nntpd255.usenet.ninja` | `backfiller255.usenet.ninja` | `cache255.usenet.ninja` |

The FQDN cannot be set in `appsettings.json` or overridden by an environment variable. After validation, hostname-dependent consumers (ACME, certificate SANs, Cloudflare, journal/live paths) use the generated value and do not reconstruct it independently.
