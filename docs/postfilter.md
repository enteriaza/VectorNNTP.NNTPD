# PostFilter v1 — operator guide

Authoritative operator reference for NNTPD PostFilter as implemented in `src/VectorNNTP.NNTPD/PostFilter/` and bound from `Nntpd:PostFilter`.

This document describes the **current** implementation. It does not invent settings, filters, or integrations that are not present in `src/` and `tests/`. Setting names and defaults are taken from `PostFilterOptions` / `PostFilterSpamAssassinOptions`. Protocol replies stay `240 Article received OK` or `441 Posting failed`; PostFilter reasons are never sent on the NNTP wire.

| Concern | Owner |
|---|---|
| Policy options | `src/VectorNNTP.NNTPD/Configuration/PostFilterOptions.cs` |
| Snapshot / refresh | `PostFilterPolicyCompiler`, `PostFilterPolicyService` |
| POST evaluation | `PostFilterEvaluator`, `Session/Commands/Post.cs` |
| Redis quota | `PostFilter/Quota/` |
| SPAMD client | `SpamdCheckClient`, `SpamdScanArticleBuilder` |
| Settings table | [configuration.md](configuration.md) |
| POST protocol | [commands.md](commands.md) |
| Validation matrix | `tests/VectorNNTP.NNTPD.Tests/PostFilter/PostFilterProductionPolicyMatrixTests.cs` |

Production `appsettings.json` ships with `Nntpd:PostFilter:Gate` = `Disabled`. Enabling the feature is an explicit operator decision. Do not treat this document as a licence to turn the gate on in the default configuration.

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

One POST captures `PostFilterPolicyService.Current` once. Mid-command option changes do not affect that evaluation.

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

`Disabled` is the compiled default and the value in production `appsettings.json`. Existing deployments keep current POST behaviour until an operator sets `Gate` to `Active` or `Closed`.

When `Gate` is `Active`, an unauthenticated session that passes deny/ArtType is rejected at the quota stage (`reason=unauthenticated`) without RESERVE. AUTHINFO is therefore required for any POST that reaches an `Active` filter.

---

## 5. Configuration

Section path: `Nntpd:PostFilter`. Bound into `PostFilterOptions` and compiled into an immutable `PostFilterPolicySnapshot`. POST does not parse JSON.

Startup compile failure (validator + `PostFilterPolicyService`) prevents `RUNNING`. After a successful initial publish, the service recompiles `IOptionsMonitor<NntpdOptions>.CurrentValue` every **five minutes**. Refresh failure logs EventId 2805 and **retains last-known-good**. A successful refresh is silent.

Generic Host reloads `appsettings.json` by default; the snapshot still changes only on the five-minute tick (or process restart). Change `Gate` and wait for the next publish, or restart, before assuming the new policy is live.

Every setting below is compiled into the snapshot (including SpamAssassin host/pool/timeout fields used only when `SpamAssassin:Enabled` is true).

### 5.1 Gate, deny, allow, ArtType

| Key | Type | Default | Required? | Semantics |
|-----|------|---------|-----------|-----------|
| `Nntpd:PostFilter:Gate` | `Disabled` / `Active` / `Closed` | `Disabled` | no | Operational gate. See §4. |
| `Nntpd:PostFilter:DeniedAccounts` | string array | `[]` | no | Exact AUTH usernames denied before quotas. Compared **ordinal** (case-sensitive) after trim. Empty/whitespace entries ignored. |
| `Nntpd:PostFilter:DeniedCidrs` | string array | `[]` | no | Client CIDRs denied before quotas. Invalid CIDR fails compile/startup. |
| `Nntpd:PostFilter:AllowlistedAccounts` | string array | `[]` | no | AUTH usernames that skip SpamAssassin **only**. Deny, ArtType, and quotas still run. Ordinal after trim. |
| `Nntpd:PostFilter:AllowlistedCidrs` | string array | `[]` | no | Client CIDRs that skip SpamAssassin only. |
| `Nntpd:PostFilter:RejectArtTypes` | string array | `[]` | no | `ArticleType` flag names rejected (for example `YEncoded`). Empty disables type policy. Parse is case-insensitive; `None` is invalid. |

`ArticleType` names currently accepted: `Default`, `Control`, `Cancel`, `Mime`, `Binary`, `UuEncode`, `Base64`, `YEncoded`, `BommaNews`, `UniData`, `Multipart`, `Html`, `PostScript`, `BinHex`, `Partial`, `PgpMessage`.

Deny and allow lists match **either** account **or** client address. Allowlist never bypasses deny.

### 5.2 Quotas

Windows must be positive. Ceilings must be `>= 0`. Negative ceilings fail startup. **Ceiling `0` disables that dimension.**

| Key | Type | Default | Required? | Semantics |
|-----|------|---------|-----------|-----------|
| `Nntpd:PostFilter:Quota:LongWindow` | duration | `1.00:00:00` (1 day) | no | Sustained (L) fixed window. Must be `> 0`. |
| `Nntpd:PostFilter:Quota:ShortWindow` | duration | `00:10:00` | no | Burst (S) fixed window. Must be `> 0`. |
| `Nntpd:PostFilter:Quota:MaxMessagesLong` | long | `0` | no | L message ceiling. `0` disables. |
| `Nntpd:PostFilter:Quota:MaxBytesLong` | long | `0` | no | L byte ceiling (`ArticleRecord.ArtSize`). `0` disables. |
| `Nntpd:PostFilter:Quota:MaxIdenticalLong` | long | `0` | no | L identical-body ceiling. `0` disables. |
| `Nntpd:PostFilter:Quota:MaxMessagesShort` | long | `0` | no | S message ceiling. `0` disables. |
| `Nntpd:PostFilter:Quota:MaxBytesShort` | long | `0` | no | S byte ceiling. `0` disables. |
| `Nntpd:PostFilter:Quota:MaxIdenticalShort` | long | `0` | no | S identical-body ceiling. `0` disables. |

There is no configuration key for reservation TTL. See §7.

Do not copy lab ceilings into production. Choose values for the local environment.

### 5.3 SpamAssassin

When `Enabled` is `false` (default), CHECK is never invoked. Hosts / `OnFailure` / protocol / pool ranges are validated only when enabled.

| Key | Type | Default | Required? | Semantics |
|-----|------|---------|-----------|-----------|
| `Nntpd:PostFilter:SpamAssassin:Enabled` | bool | `false` | no | Invoke SPAMD CHECK for eligible, non-allowlisted articles. |
| `Nntpd:PostFilter:SpamAssassin:OnFailure` | `Reject` / `Accept` | `null` | **yes when enabled** | Scanner-fault action (connect, protocol, timeout, leftover). Cancellation is not `OnFailure`. Spam findings always reject in v1 (`OnSpam` is not configurable). |
| `Nntpd:PostFilter:SpamAssassin:MaxArticleSize` | int | `131072` | no | Exclusive `ArtSize` gate. `ArtSize >= MaxArticleSize` skips CHECK. `0` disables the size gate. Must be `>= 0`. |
| `Nntpd:PostFilter:SpamAssassin:ExcludeArtTypes` | string array | `["YEncoded"]` | no | `ArticleType` flags excluded from CHECK. Empty disables type exclusion. |
| `Nntpd:PostFilter:SpamAssassin:Hosts` | string array | `[]` | **yes when enabled** | SPAMD hosts (shared `Port`). Whitespace-only entries fail compile. |
| `Nntpd:PostFilter:SpamAssassin:Port` | int | `783` | no | Shared port for every host (`1–65535` when enabled). |
| `Nntpd:PostFilter:SpamAssassin:ProtocolVersion` | string | `1.5` | **yes when enabled** | Version on the request line (`CHECK SPAMC/1.5`). |
| `Nntpd:PostFilter:SpamAssassin:MaxConnections` | int | `4` | **yes when enabled** | Persistent pool size (`1–32` when enabled). |
| `Nntpd:PostFilter:SpamAssassin:HostSelection` | `RoundRobin` / `Failover` | `RoundRobin` | no | Deterministic first-host pick. Neither is random. |
| `Nntpd:PostFilter:SpamAssassin:ConnectTimeout` | duration | `00:00:05` | no | Connect timeout. When enabled: must be `> 0` and `<= OperationTimeout`. |
| `Nntpd:PostFilter:SpamAssassin:OperationTimeout` | duration | `00:00:30` | **yes when enabled** | Wall-clock CHECK budget including connect (`1s`–`2m` when enabled). |

Example (does **not** change the production default file; `Gate` stays operator-chosen):

```json
"Nntpd": {
  "PostFilter": {
    "Gate": "Disabled",
    "Quota": {
      "LongWindow": "1.00:00:00",
      "ShortWindow": "00:10:00",
      "MaxMessagesLong": 0,
      "MaxBytesLong": 0,
      "MaxIdenticalLong": 0,
      "MaxMessagesShort": 0,
      "MaxBytesShort": 0,
      "MaxIdenticalShort": 0
    },
    "SpamAssassin": {
      "Enabled": false,
      "OnFailure": "Reject",
      "Hosts": [ "198.18.0.70" ],
      "Port": 783,
      "ProtocolVersion": "1.5",
      "MaxConnections": 4,
      "HostSelection": "RoundRobin",
      "ConnectTimeout": "00:00:05",
      "OperationTimeout": "00:00:30",
      "MaxArticleSize": 131072,
      "ExcludeArtTypes": [ "YEncoded" ]
    }
  }
}
```

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
| 2808 | Information | Initial policy publish (`gate`, SA flags, hosts, pool) |

`artId` in these logs is `ArticleRecord.ArtHash` hex, not the identical-body XXH3-64.

In-process counters (not a metrics HTTP endpoint):

- `PostFilterMetrics.CommitNoop` / `CommitUnavailable` — incremented only after successful `TryAdmit`
- `SpamdTransportMetrics` — summarized at transport stop (EventId 2807)

**Current limitations (do not add noisy logging solely to close these):**

- EventIds 2800–2808 **overlap** RabbitMQ lifecycle EventIds. Filter on message text (`PostFilter …`) or logger category, not EventId alone.
- Successful five-minute refresh is not logged (only the initial publish and refresh failures).
- `RESERVE` Unavailable appears as EventId 2800 `stage=Quota reason=Unavailable`, not a dedicated Redis-down event.

POST also emits existing `PostLogMessages` Accepted/Rejected lines (`441` category `PolicyRejected` plus the internal reason).

---

## 12. Safe deployment / activation

1. Leave `Gate=Disabled` on first deploy. Confirm ordinary POST still returns `240`.
2. Confirm NNTPD already has a working Redis topology (`Redis:Host` / startup PING). PostFilter uses the same multiplexer.
3. If SpamAssassin will be used, confirm SPAMD accepts `CHECK` on the configured `Hosts`/`Port` from the NNTPD hosts. Do not enable SA until that works.
4. Configure deny/allow/ArtType/quotas/`OnFailure` in `Nntpd:PostFilter`. Keep `Gate=Disabled` while editing.
5. Start with **conservative** ceilings (or leave `0` to disable a dimension). There is no universal production number.
6. Watch EventId 2808 at startup and file/journal logs for compile errors (bad CIDR, missing `OnFailure` when SA is enabled).
7. Set `Gate=Active` and wait for the next snapshot (up to five minutes) or restart.
8. POST a known HAM from an authenticated account that is not denied. Expect `240` and a Redis reservation that COMMITs.
9. POST enough volume to hit a test ceiling. Expect `441` and EventId 2800 `stage=Quota`.
10. If SA is enabled, POST an eligible HAM and an ineligible (large / excluded ArtType) article. Confirm CHECK vs skip.
11. Confirm clients see only `240` / `441`, never internal reasons.
12. Inspect Redis keys `nntpd:pf:q:*` / `nntpd:pf:m:*` for the account hash. Live fields are `r:…`; committed fields are `c:m:` / `c:b:`.

To disable quickly: set `Gate=Disabled` (or `Closed` to reject all POSTs that reach the filter) and wait for refresh or restart.

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
