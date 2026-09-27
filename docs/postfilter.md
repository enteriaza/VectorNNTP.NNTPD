# PostFilter v1 — operator guide

Authoritative operator reference for NNTPD PostFilter as implemented in `src/VectorNNTP.NNTPD/PostFilter/`.

**NntpDB is the only authoritative cluster policy.** Every NNTPD instance loads `nntppostfilterpolicy` (and its collection tables), compiles an immutable local snapshot, and evaluates POST against that snapshot. There is no `Nntpd:PostFilter` options binding. A leftover `Nntpd:PostFilter` section fails startup. Redis holds quota **runtime state** only, never policy.

Protocol replies stay `240 Article received OK` or `441 Posting failed`; PostFilter reasons are never sent on the NNTP wire.

| Concern | Owner |
|---|---|
| Authoritative policy | MySQL `nntppostfilterpolicy` + collection tables |
| Repository | `MySqlPostFilterPolicyRepository` |
| Snapshot / refresh | `PostFilterPolicyCompiler`, `PostFilterPolicyService` (60s) |
| POST evaluation | `PostFilterEvaluator` (snapshot only; no SQL) |
| Redis quota | `PostFilter/Quota/` |
| SPAMD client | `SpamdCheckClient`, `SpamdScanArticleBuilder` |
| Settings table | [configuration.md](configuration.md) |
| POST protocol | [commands.md](commands.md) |
| Validation matrix | `tests/VectorNNTP.NNTPD.Tests/PostFilter/PostFilterProductionPolicyMatrixTests.cs` |

NNTPD does not create or migrate these tables. Apply the schema below once per NntpDB. Seed `Gate=Disabled` so the cluster stays inert until operators change the database.

---

## 1. What PostFilter does

PostFilter is a **POST-only** accept-path policy that runs after a destuffed article has been parsed into an immutable `ArticleRecord`. When the gate is `Active`, it can:

1. Reject a POST before queue admission (deny lists, ArtType policy, unauthenticated session, quota, SpamAssassin).
2. Reserve distributed accept-quota in Redis for the authenticated account.
3. Optionally send a **disposable** email-like scan representation to SpamAssassin SPAMD `CHECK`.
4. After `TryAdmit` succeeds, commit the reservation (or leave it until TTL if COMMIT cannot run).

PostFilter does **not** mutate `ArticleRecord`, does **not** create a second canonical article, and does **not** change History, spool, or worker processing.

---

## 2. Where it runs

PostFilter is invoked only from the ordinary POST injection path, after History Peek and `ArticleRecordIngress.TryCreateFromDestuffed`, and before `CreateQueued` / `TryAdmit`.

```text
POST
  → posting-permitted? (else 440; no article is read)
  → 340
  → StreamingPostArticleReader
  → catalogue `m` unapproved proto-article?
        yes → IModerationSubmissionService (no PostFilter, no Peek, no TryAdmit)
  → History Peek (Seen / Unavailable → 441; no PostFilter)
  → ArticleRecordIngress.TryCreateFromDestuffed
  → ArticleRecord (immutable canonical bytes)
  → PostFilter.EvaluateAsync  (one captured policy snapshot)
        Gate Disabled → accept, no Redis, no SPAMD
        Gate Closed   → 441
        deny / ArtType / unauthenticated → 441 (no Redis)
        Redis RESERVE
        optional SPAMD CHECK
  → CreateQueued
  → TryAdmit
        fail → Redis RELEASE + 441
  → Redis COMMIT          (never RELEASE on COMMIT failure)
  → History Remember
  → 240 Article received OK
```

One POST captures `PostFilterPolicyService.Current` once. That snapshot is used for the entire request. POST never queries NntpDB. Mid-request database edits do not affect that evaluation.

---

## 3. What it does not filter

PostFilter does **not** run for:

| Path | Why |
|------|-----|
| IHAVE | Transit ingest; no `IPostFilter` call |
| TAKETHIS | Transit ingest; no `IPostFilter` call |
| BackFiller | Separate process; no PostFilter |
| Catalogue status `m` unapproved submission | `SubmitForModerationAsync` returns before ArticleRecord / PostFilter |
| Incoming spool / queue workers | Filter already decided at POST admission |
| History duplicate / History unavailable | Peek rejects before PostFilter |
| Receive/parse failures (`Nntpd:MaxArticleSize`, malformed article) | Rejected before ArticleRecord |

Authorized moderator reinjection (`Approved:` + catalogue authorization) is ordinary POST and **does** run PostFilter.

---

## 4. Default / safe posture

| Gate | Meaning |
|------|---------|
| `Disabled` (default) | Skip remaining stages. No quota reservation. No SPAMD call. Ordinary POST continues. |
| `Active` | Evaluate deny, ArtType, quotas, and SpamAssassin. |
| `Closed` | Reject every POST that reaches PostFilter (`441`). No Redis. No SPAMD. |

`Disabled` is the recommended seed for `nntppostfilterpolicy.gate`. Existing POST behaviour continues until an operator updates NntpDB. There is no per-node Gate in appsettings.

When `Gate` is `Active`, an unauthenticated session that passes deny/ArtType is rejected at the quota stage (`reason=unauthenticated`) without RESERVE. AUTHINFO is therefore required for any POST that reaches an `Active` filter.

---

## 5. Authoritative NntpDB policy

```text
NntpDB  (nntppostfilterpolicy + collections)
    → MySqlPostFilterPolicyRepository
    → PostFilterPolicyCompiler
    → immutable PostFilterPolicySnapshot
    → Volatile publish
    → POST reads Current only
```

NNTPD follows the same catalogue contract as `nntpgroups` / `nntpmoderators`:

- Initial load during `PostFilterPolicyService.StartAsync`. Failure prevents `RUNNING`. There is **no** appsettings fallback.
- Refresh every **60 seconds**. Success compiles a new snapshot and publishes it with `Volatile.Write`.
- Refresh failure (MySQL down, missing row, compile error) keeps last-known-good. PostFilter is not disabled and does not revert to local JSON.
- Empty/malformed/duplicate collection entries fail compile of that refresh (or startup).

`revision` is an operator-incremented `BIGINT` on the singleton row. Logs emit `revision={Revision}`. Increment it when changing policy so every node can report which revision it is running. NNTPD still reloads row contents every 60s even if `revision` is unchanged.

### 5.1 Schema (operator/DBA; NNTPD SELECTs only)

```sql
CREATE TABLE nntppostfilterpolicy (
  policy_id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
  revision BIGINT UNSIGNED NOT NULL,
  updated_utc DATETIME(3) NOT NULL,
  gate VARCHAR(16) NOT NULL,
  long_window_ms BIGINT UNSIGNED NOT NULL,
  short_window_ms BIGINT UNSIGNED NOT NULL,
  max_messages_long BIGINT UNSIGNED NOT NULL,
  max_bytes_long BIGINT UNSIGNED NOT NULL,
  max_identical_long BIGINT UNSIGNED NOT NULL,
  max_messages_short BIGINT UNSIGNED NOT NULL,
  max_bytes_short BIGINT UNSIGNED NOT NULL,
  max_identical_short BIGINT UNSIGNED NOT NULL,
  sa_enabled CHAR(1) NOT NULL,
  sa_on_failure VARCHAR(16) NULL,
  sa_max_article_size INT NOT NULL,
  sa_port INT UNSIGNED NOT NULL,
  sa_protocol_version VARCHAR(16) NOT NULL,
  sa_max_connections INT UNSIGNED NOT NULL,
  sa_host_selection VARCHAR(16) NOT NULL,
  sa_connect_timeout_ms INT UNSIGNED NOT NULL,
  sa_operation_timeout_ms INT UNSIGNED NOT NULL
);

CREATE TABLE nntppostfilteraccounts (
  list_kind VARCHAR(8) NOT NULL,
  account_name VARCHAR(255) NOT NULL,
  PRIMARY KEY (list_kind, account_name)
);

CREATE TABLE nntppostfiltercidrs (
  list_kind VARCHAR(8) NOT NULL,
  cidr VARCHAR(64) NOT NULL,
  PRIMARY KEY (list_kind, cidr)
);

CREATE TABLE nntppostfilterarttypes (
  list_kind VARCHAR(16) NOT NULL,
  art_type VARCHAR(32) NOT NULL,
  PRIMARY KEY (list_kind, art_type)
);

CREATE TABLE nntppostfiltersahosts (
  host_order INT UNSIGNED NOT NULL PRIMARY KEY,
  host VARCHAR(255) NOT NULL
);
```

`list_kind` values: accounts/CIDRs use `deny` or `allow`; ArtTypes use `reject` or `sa_exclude`. `sa_enabled` is `Y`/`N` (same convention as `nntpmoderators.is_enabled`).

Seed (cluster-inert, matches the former local defaults):

```sql
INSERT INTO nntppostfilterpolicy (
  policy_id, revision, updated_utc, gate,
  long_window_ms, short_window_ms,
  max_messages_long, max_bytes_long, max_identical_long,
  max_messages_short, max_bytes_short, max_identical_short,
  sa_enabled, sa_on_failure, sa_max_article_size, sa_port,
  sa_protocol_version, sa_max_connections, sa_host_selection,
  sa_connect_timeout_ms, sa_operation_timeout_ms
) VALUES (
  1, 1, UTC_TIMESTAMP(3), 'Disabled',
  86400000, 600000,
  0, 0, 0, 0, 0, 0,
  'N', NULL, 131072, 783,
  '1.5', 4, 'RoundRobin',
  5000, 30000
);

INSERT INTO nntppostfilterarttypes (list_kind, art_type) VALUES ('sa_exclude', 'YEncoded');
```

Missing `policy_id = 1` prevents startup. NNTPD will not invent a Disabled policy.

### 5.2 Former `Nntpd:PostFilter` mapping

| Former key | NntpDB |
|------------|--------|
| `Gate` | `nntppostfilterpolicy.gate` |
| `DeniedAccounts` / `AllowlistedAccounts` | `nntppostfilteraccounts` (`deny` / `allow`) |
| `DeniedCidrs` / `AllowlistedCidrs` | `nntppostfiltercidrs` (`deny` / `allow`) |
| `RejectArtTypes` | `nntppostfilterarttypes` (`reject`) |
| `Quota:LongWindow` / `ShortWindow` | `long_window_ms` / `short_window_ms` |
| `Quota:MaxMessages*` / `MaxBytes*` / `MaxIdentical*` | matching columns |
| `SpamAssassin:Enabled` | `sa_enabled` (`Y`/`N`) |
| `SpamAssassin:OnFailure` | `sa_on_failure` (`Reject`/`Accept`, NULL when disabled) |
| `SpamAssassin:MaxArticleSize` | `sa_max_article_size` (exclusive; `0` disables) |
| `SpamAssassin:ExcludeArtTypes` | `nntppostfilterarttypes` (`sa_exclude`) |
| `SpamAssassin:Hosts` | `nntppostfiltersahosts` (`host_order`, `host`) |
| `SpamAssassin:Port` / `ProtocolVersion` / `MaxConnections` / `HostSelection` | `sa_port`, `sa_protocol_version`, `sa_max_connections`, `sa_host_selection` |
| `SpamAssassin:ConnectTimeout` / `OperationTimeout` | `sa_connect_timeout_ms` / `sa_operation_timeout_ms` |

Do not automatically copy leftover appsettings values. Operators apply the seed, then edit NntpDB.

SPAMD endpoints are **cluster policy** (same Hosts/Port on every node). Reservation identity still uses node-local `ServerId` (already NNTPD identity, not PostFilter policy). Redis connection settings remain the top-level `Redis` section.

`ArticleType` names: `Default`, `Control`, `Cancel`, `Mime`, `Binary`, `UuEncode`, `Base64`, `YEncoded`, `BommaNews`, `UniData`, `Multipart`, `Html`, `PostScript`, `BinHex`, `Partial`, `PgpMessage`.

Account names compare **ordinal** after trim. Empty or duplicate collection rows fail compile. Ceiling `0` disables that quota dimension. Reservation TTL is not a database column (see §7).

---

## 6. Policy semantics

Compiled snapshot fields include gate, deny/allow sets, reject ArtType mask, quota windows/ceilings, SpamAssassin enablement, `OnFailure`, eligibility, hosts, port, protocol, pool size, host selection, timeouts, and reservation hold.

Evaluation order when `Gate` is `Active`:

1. Deny (account or CIDR) → `441`, no Redis.
2. `RejectArtTypes` if any listed flag is set on `ArtType` → `441`, no Redis.
3. Missing AUTH username → `441` (`unauthenticated`), no Redis.
4. Redis `RESERVE` (fail-closed on Unavailable).
5. SpamAssassin CHECK only when enabled, not allowlisted, and `ShouldScan` is true.
6. Accept with a live lease.

Allowlist skips step 5 only.

---

## 7. Redis

Redis holds **distributed runtime quota state**, not policy. Policy is the local immutable snapshot. The same account on every NNTPD node shares the same Redis hashes.

| Item | Current behaviour |
|------|-------------------|
| Identity | Authenticated AUTH username (UTF-8 SHA-256, lowercase hex). Not client IP. |
| Quota HASH | `nntpd:pf:q:{sha256hex}` — committed counters + live reservations |
| Multipost HASH | `nntpd:pf:m:{sha256hex}` — committed identical-body counts |
| Reservation field | `r:{nodeId}:{incarnation}:{generation}` |
| Node id | `nntpd{ServerId:00}` host label (same pattern as session ownership) |
| Model | Two fixed-epoch windows (L + S). Bucket id = `nowMs / windowMs`. |
| Hold / crash TTL | Floor **10s**. When SpamAssassin is enabled: `max(10s, OperationTimeout + 1s)`. Not a v1 operator knob. |
| Idle key TTL | `max(L,S) + hold + 1s` — garbage collection only; does not change bucket maths. |

`RESERVE` is atomic Lua EVAL. A deny writes **nothing**. A live reservation counts toward the ceiling immediately. `COMMIT` moves reserved units into the current buckets and deletes the reservation. `RELEASE` deletes a matching live reservation and **never** decrements committed usage.

These conservative outcomes are intentional:

| Condition | Client | Queue | Quota |
|-----------|--------|-------|-------|
| `RESERVE` Unavailable | `441` | not queued | no reservation |
| `COMMIT` Unavailable after `TryAdmit` | `240` | article remains queued | reservation **not** released; committed usage may temporarily under-count |
| `RELEASE` Unavailable | reject/cancel path already decided | not queued (if reject) | reservation may remain until hold TTL expires |
| Process crash after `RESERVE`, before `COMMIT`/`RELEASE` | no `240` | not admitted | reservation expires at hold TTL |

Redis itself remains a required NNTPD dependency (startup connect/PING). PostFilter adds the keys above on the same multiplexer. See [configuration.md](configuration.md#redis).

---

## 8. Quotas

Dimensions (each independently disableable with ceiling `0`):

| Dimension | What is counted |
|-----------|-----------------|
| Messages | `1` per reserved POST |
| Bytes | `ArticleRecord.ArtSize` |
| Identical-message / multipost | `1` when a body identity is computed |

Identical-message identity is **XXH3-64 of the canonical body octets after the first `CRLF CRLF`**, lowercase hex. It is **not** `ArticleRecord.ArtHash`. Articles whose `ArtType` includes `YEncoded`, `Binary`, `UuEncode`, `Base64`, or `BinHex` do not participate (no multipost units).

Accounts are isolated: the same body on two usernames does not share a ceiling. Cross-node POSTs for one username share Redis state.

Deterministic reserve outcomes (log `reason`, never on the wire):

| Status | Meaning |
|--------|---------|
| `Accepted` | Live reservation counts toward the ceiling |
| `DeniedMessagesLong` / `DeniedMessagesShort` | Message ceiling |
| `DeniedBytesLong` / `DeniedBytesShort` | Byte ceiling |
| `DeniedIdenticalLong` / `DeniedIdenticalShort` | Identical-body ceiling |
| `Conflict` | Same token, different payload (should not occur in production) |
| `Unavailable` | Redis/EVAL unavailable — fail closed |

---

## 9. SpamAssassin

SPAMD does **not** receive `ArticleRecord` / `ArtData` directly.

```text
ArticleRecord (read-only)
  → SpamdScanArticleBuilder (disposable buffer)
  → SPAMD CHECK SPAMC/1.5
```

`ArtData` is not mutated. The scan buffer is independent and discarded after CHECK.

### 9.1 Scan representation (current builder only)

Synthetic headers prepended:

- `Received: from [<client-ip>] by <server-fqdn> with NNTP [id <Message-ID>]; <date>`
- `To: usenet@<server-fqdn>`
- `X-Usenet-Newsgroups: <Newsgroups>` when present
- `Date:` only when the original article has no Date header

Stripped (not copied into the scan): `Path`, `Xref`, `Injection-Info`, `X-Trace`, `X-Complaints-To`, `NNTP-Posting-Host`.

All other original header blocks are copied. The body after the first `CRLF CRLF` is copied verbatim. A missing/unsafe FQDN becomes `localhost`.

### 9.2 Transport

- Protocol: SPAMC/SPAMD `CHECK` with configured `ProtocolVersion` (default `1.5`).
- Persistent connection pool sized by `MaxConnections`.
- Each connection **serializes** CHECKs. No pipelining. No multiplexing (SPAMD has no request IDs).
- `RoundRobin`: rotate start host. `Failover`: always start at `Hosts[0]`.
- Connect-time failures fail over to the next host.
- After the request is written, the CHECK is **not** retried on another host.
- Stale idle socket (`closed` / `connection` on a reused connection): retry **once on the same host**.
- Bytes after the response `\r\n\r\n` are `Failed leftover`; the connection is evicted.

### 9.3 Eligibility (`ShouldScan`)

CHECK runs only when all of the following are true:

- `Gate` is `Active` (or the filter otherwise reached the SA stage)
- `SpamAssassin:Enabled` is true
- the session is not allowlisted
- `MaxArticleSize` is `0` **or** `ArtSize < MaxArticleSize` (exclusive)
- `ArtType` has no bits in `ExcludeArtTypes`

A skip does **not** contact SPAMD and is **not** an `OnFailure` event. Quota reservation already exists and the rest of POST continues.

Default eligibility matches the historical small-article boundary: `ArtSize < 131072` and not `YEncoded`. A 768 KiB article is not sent to SPAMD under that default.

### 9.4 Results

| Scanner outcome | Filter | Reservation |
|-----------------|--------|-------------|
| HAM (`Spam: False`) | Accept | keep lease |
| SPAM (`Spam: True`) | `441` | RELEASE |
| Failed + `OnFailure=Reject` | `441` | RELEASE |
| Failed + `OnFailure=Accept` | Accept | keep lease |
| Caller cancellation | throw | RELEASE (`CancellationToken.None`) |

---

## 10. Failure matrix

Client rejects after `340` are `441 Posting failed`. Success is `240 Article received OK`.

| Condition | Redis | SPAMD | Queue | Client | Result |
|-----------|-------|-------|-------|--------|--------|
| `Gate=Disabled` | none | none | admitted | `240` | Normal POST; no reserve |
| `Gate=Closed` | none | none | no | `441` | No reserve, no CHECK |
| CIDR / account deny | none | none | no | `441` | Before quota |
| ArtType deny | none | none | no | `441` | Before quota |
| Unauthenticated + `Active` | none | none | no | `441` | `reason=unauthenticated` |
| Quota denial | deny writes nothing | none | no | `441` | Live reservations of others still count |
| SPAMD spam | RELEASE | CHECK ran | no | `441` | Not remembered |
| SPAMD failure `Reject` | RELEASE | failed | no | `441` | Not an eligibility skip |
| SPAMD failure `Accept` | COMMIT on admit | failed | admitted | `240` | Reservation kept |
| Redis unavailable at `RESERVE` | fail-closed | none | no | `441` | Intentional |
| `TryAdmit` failure | RELEASE | already done | no | `441` | Article not queued |
| `COMMIT` unavailable | reservation left | — | **admitted** | `240` | Under-count until TTL; not a rollback |
| Successful POST | COMMIT | HAM or skip | admitted | `240` | Remember |
| Cancel after `RESERVE` | RELEASE (`None`) | — | no | no completion | No `240` |

Eligibility skips (size / excluded ArtType / allowlist / SA disabled) are **not** dependency failures: SPAMD is never contacted.

---

## 11. Observability

Filter reasons stay off the wire. Use logs.

| EventId | Level | When |
|---------|-------|------|
| 2800 | Information | Reject (`stage`, `reason`, `account`, `artId`) |
| 2801 | Debug | Accept (`Gate` disabled or complete; `reserved`) |
| 2802 | Warning | `COMMIT` NOOP after successful admit |
| 2803 | Warning | `COMMIT` unavailable after admit (no RELEASE) |
| 2804 | Debug | `RELEASE` status |
| 2805 | Warning | Policy refresh failed; last-good retained |
| 2806 | Warning | SpamAssassin failure (`action`, `detail`) |
| 2807 | Information | SPAMD transport stopped (connects/checks/reuses/reconnects/evictions) |
| 2808 | Information | Policy published (`revision`, gate, SA flags, hosts, pool) |
| 2809 | Error | Initial NntpDB policy load failed |
| 2810 | Information | Policy revision changed (`from` → `to`) |
| 2811 | Error | Policy repository failed |

`artId` in these logs is `ArticleRecord.ArtHash` hex, not the identical-body XXH3-64.

In-process counters (not a metrics HTTP endpoint):

- `PostFilterMetrics.CommitNoop` / `CommitUnavailable` — incremented only after successful `TryAdmit`
- `SpamdTransportMetrics` — summarized at transport stop (EventId 2807)

**Current limitations (do not add noisy logging solely to close these):**

- EventIds 2800–2808 **overlap** RabbitMQ lifecycle EventIds. Filter on message text (`PostFilter …`) or logger category, not EventId alone.
- Successful 60s refresh with an unchanged revision is not separately logged (2808 still fires on every publish).
- `RESERVE` Unavailable appears as EventId 2800 `stage=Quota reason=Unavailable`, not a dedicated Redis-down event.

POST also emits existing `PostLogMessages` Accepted/Rejected lines (`441` category `PolicyRejected` plus the internal reason).

---

## 12. Safe deployment / activation

1. Apply the schema and Disabled seed to NntpDB. Do not add `Nntpd:PostFilter` to appsettings.
2. Confirm every NNTPD can `SELECT` `nntppostfilterpolicy` (initial load is required for RUNNING).
3. Confirm Redis (`Redis:Host` / startup PING). PostFilter uses the same multiplexer for quota state.
4. If SpamAssassin will be used, confirm SPAMD from every NNTPD host, then set `sa_enabled='Y'` and hosts in NntpDB.
5. Edit deny/allow/ArtType/quotas in NntpDB. Keep `gate='Disabled'` while editing. Increment `revision`.
6. Start with **conservative** ceilings (or `0` to disable a dimension). There is no universal production number.
7. Watch EventId 2808 (`revision=`) on each node. Failed compile/refresh keeps last-good (2805 / 2809).
8. Set `gate='Active'`, increment `revision`. Within 60s both nodes should log 2810 and the new revision.
9. POST a known HAM from an authenticated account that is not denied. Expect `240` and a Redis COMMIT.
10. POST enough volume to hit a test ceiling. Expect `441` and EventId 2800 `stage=Quota`.
11. Confirm clients see only `240` / `441`. Confirm both nodes report the same revision.
12. Inspect Redis keys `nntpd:pf:q:*` / `nntpd:pf:m:*` (runtime state, not policy).

To disable quickly: `UPDATE nntppostfilterpolicy SET gate='Disabled', revision = revision + 1` (or `Closed`) and wait up to 60s.

---

## 13. Troubleshooting

| Symptom | Distinguish | What to check |
|---------|-------------|---------------|
| Every POST is `441` | Closed gate vs deny vs quota vs SA vs receive/parse | EventId 2800 `stage`/`reason`. `Closed` / `denied` / `arttype` do not touch Redis. `Nntpd:MaxArticleSize` and catalogue policy reject **before** PostFilter (POST rejected logs, no 2800). |
| Redis unavailable | `reason=Unavailable` at Quota | Redis process, `Redis:Host`, multiplexer health. Fail-closed is expected. |
| Quota rejects unexpectedly | Ceiling includes **live reservations** | Other in-flight POSTs, short window still open, shared cluster state, case-sensitive account names, `ArtSize` vs assumed size. |
| SPAMD unavailable | EventId 2806 `action=Reject` or `Accept` | Hosts/port/firewall. `OnFailure=Accept` still yields `240`. |
| SPAMD rejects HAM | `reason=spam` | Disposable scan headers (Received/To/X-Usenet-Newsgroups). Tune SA rules, not ArticleRecord. |
| SPAMD never contacted | Eligibility skip vs Disabled vs allowlist vs Closed | Debug 2801 `reserved=false` (Disabled). Size: `ArtSize >= MaxArticleSize`. `ExcludeArtTypes`. Allowlist. `Enabled=false`. |
| Large articles skip SA | Exclusive size gate | Default `131072`. `0` disables the size gate. |
| yEnc / listed ArtType skip SA | Default `ExcludeArtTypes=["YEncoded"]` | Empty array disables type exclusion. |
| Allowlisted account still quota-denied | Allowlist skips SA only | Deny/ArtType/quotas still apply. |
| `240` but COMMIT warning | EventId 2802 / 2803 | Article is queued. Do not RELEASE. Committed count may lag until TTL. |
| Reservation fields linger | RELEASE Unavailable or crash | Hold TTL (10s, or `OperationTimeout+1s` when SA is enabled). Expected. |

---

## 14. Current limitations

Absent from this implementation (not implied as a roadmap):

- DNSBL, URIBL, TOR, rDNS / domain identity
- Dictionary / bad-word / regex filtering
- Reject-streak logic
- Anonymous / IP quotas (quota identity is AUTH username only)
- Moderation as a PostFilter outcome (`Moderate` is not implemented; catalogue `m` submission is a separate path)
- Header/body mutation of the canonical article
- Generic filter framework / plugin stages beyond gate, deny, ArtType, quota, and SpamAssassin
- Per-POST metrics export or a dedicated Redis-down EventId
- Configurable reservation TTL

---

## 15. Test / validation evidence

POST-boundary coverage lives in `PostFilterProductionPolicyMatrixTests` (test-local snapshots; production `Gate` stays `Disabled`):

- Gate `Disabled` / `Closed`
- Account and CIDR deny; ArtType deny; allowlist (SA skip only)
- Message, byte, and identical-body quotas
- Account isolation; shared in-memory quota across two reservation identities
- SPAMD HAM / SPAM; size and ArtType skip; allowlisted skip
- `OnFailure=Reject` / `OnFailure=Accept`
- Redis unavailable at RESERVE; `TryAdmit` failure; cancel after RESERVE
- COMMIT unavailable after admit (`240`, queued, no RELEASE)
- Complete reserve → scan → admit → COMMIT → Remember
- `ArticleRecord` immutability and disposable SPAMD representation

Additional suites: `PostFilterEvaluatorTests`, `PostFilterPostCommandTests`, quota Lua/engine/live-cluster tests, SPAMD transport/eligibility/builder tests.
