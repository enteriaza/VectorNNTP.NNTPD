# VectorNNTP.NNTPD — Configuration

Configuration binds from the `Nntpd` section (case-insensitive). Sources include `appsettings.json`, environment variables, and command-line arguments via the Generic Host.

Validation runs at startup through `IValidateOptions<NntpdOptions>` and data annotations (`ValidateOnStart`). **Validation does not bind sockets and does not call Cloudflare APIs.**

## Settings

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `ApplicationName` | string | `VectorNNTP.NNTPD` | no | Display name for logs and Windows Service metadata |
| `GracefulShutdownTimeout` | `TimeSpan` | `00:00:30` | no | Overall wall-clock bound for the reverse-order application-service stop sequence (also drives host `ShutdownTimeout`). Manager awaits each stop; ignore-cancel services can block until host/supervisor kill. |
| `StartupTimeout` | `TimeSpan?` | `null` | no | Optional startup bound; `null` means host cancellation only |
| `StopHostOnUnexpectedServiceTermination` | bool | `true` | no | Request host stop when a service fails while `Running` |
| `Systemd:*` | object | see existing docs | no | systemd notify / watchdog options |
| `BindAddress` | string array | omitted → `["*"]` after normalize | no | Local listen addresses (see below) |
| `BindPort` | int | `119` | no | Cleartext NNTP TCP port (`1–65535`) |
| `BindPortTls` | int | `0` | no | TLS NNTP TCP port; `0` / unset disables TLS (`1–65535` enables). When enabled, ACME is required. |
| `AcmeDirectoryUrl` | string | Let's Encrypt **staging** directory | no | Absolute HTTPS ACME directory URL (authoritative; never silently switched to production) |
| `AcmeEmail` | string | _(none)_ | **yes when TLS enabled** | ACME account contact email; ignored when `BindPortTls` is `0` |
| `AcmeStateDir` | string | `certs/` | no | Filesystem directory for ACME account + certificate DER state |
| `AcmeRenewalThresholdDays` | int | `30` | no | Renew when `NotAfter - threshold` is reached (`1–90`) |
| `AcmeCertificatePassword` | string | _(none)_ | **yes when TLS enabled** (secret) | Password protecting the TLS server PKCS#12/PFX |
| `CloudFlareApiKey` | string | _(none)_ | **yes** (secret) | Cloudflare API key for DNS integration |
| `CloudFlareZoneId` | string | _(none)_ | **yes** | Cloudflare zone identifier |
| `CloudFlareOperationTimeout` | duration | `00:02:00` | no | Wall-clock budget for one reconcile or cleanup operation (shared by HTTP 429 retries and reconciler attempt backoffs) |
| `DnsSuffix` | string | `usenet.ninja` | no | DNS suffix used to generate the FQDN |
| `ServerId` | int | _(none)_ | **yes** | Server identity `1–99`; no silent default |
| `Fqdn` | _(generated)_ | `nntpd{ServerId:00}.{DnsSuffix}` | n/a | **Not configurable** |

Setting names are PascalCase and match the `NntpdOptions` property names. Obsolete snake_case keys (`bind_address`, `server_id`, …) are not aliased.

## Bind addresses

`BindAddress` is a JSON array of one or more entries. Every entry is validated; none are silently dropped.

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
"BindAddress": [ "*" ]
```

Multiple addresses:

```json
"BindAddress": [ "198.18.0.66", "2001:db8::1" ]
```

## TCP ports

| Setting | Default | Valid values | Behavior |
|---------|---------|--------------|----------|
| `BindPort` | `119` | `1–65535` | Missing → default; invalid → hard startup failure |
| `BindPortTls` | `0` | `0` or `1–65535` | `0` / unset → TLS disabled (`IsTlsListenerEnabled == false`); `1–65535` → TLS enabled |

Negative values and values above `65535` fail validation. This phase does not bind sockets or implement TLS listeners; dependents use `BindPortTls` / `IsTlsListenerEnabled` to distinguish disabled vs enabled configuration.

## TLS and ACME (Let's Encrypt)

TLS is controlled exclusively by `BindPortTls`:

| `BindPortTls` | TLS | ACME |
|---------------|-----|------|
| unset / `0` | disabled | **completely disabled** — no account registration, no Let's Encrypt calls, no certificate requirement, existing files under `AcmeStateDir` are not touched |
| `1–65535` | enabled | required — account + usable certificate must exist before the application enters `Running` |

### ACME settings

| Setting | Default | Notes |
|---------|---------|-------|
| `AcmeDirectoryUrl` | `https://acme-staging-v02.api.letsencrypt.org/directory` | **Staging** by default. Production requires an explicit override such as `https://acme-v02.api.letsencrypt.org/directory`. |
| `AcmeEmail` | _(none)_ | Required only when TLS is enabled. Must be a plausible contact email. |
| `AcmeStateDir` | `certs/` | Persistent ACME state root (relative or absolute path). |
| `AcmeRenewalThresholdDays` | `30` | Certificate is due for renewal when `now >= NotAfter - threshold`. |
| `AcmeCertificatePassword` | _(none)_ | Required only when TLS is enabled. Protects `certificate.pfx`. |

Do **not** commit real emails, PFX passwords, production directory URLs tied to live accounts, or machine-specific absolute paths into tracked `appsettings.json`.

Example (TLS enabled against staging — values are illustrative; supply secrets via environment / user secrets):

```json
"Nntpd": {
  "BindPortTls": 563,
  "AcmeEmail": "ops@example.org",
  "AcmeDirectoryUrl": "https://acme-staging-v02.api.letsencrypt.org/directory",
  "AcmeStateDir": "certs/",
  "AcmeRenewalThresholdDays": 30
}
```

```text
nntpd__AcmeCertificatePassword=<secret>
```

Production directory (explicit only):

```json
"AcmeDirectoryUrl": "https://acme-v02.api.letsencrypt.org/directory"
```

Staging certificates are **not** trusted by normal clients. Use staging for integration testing; switch the directory URL only when ready for a production CA.

### Certificate identities (SANs)

Every TLS certificate must include exactly these DNS names (no wildcards, no extras):

1. The generated `{Fqdn}` (for example `nntpd01.usenet.ninja`)
2. `news.usenet.ninja`

Both names must fall under `DnsSuffix` (label-boundary zone coverage) because DNS-01 challenges are published in the single Cloudflare zone identified by `CloudFlareZoneId`.

### Challenge mechanism

ACME uses **DNS-01** only (Cloudflare TXT records named `_acme-challenge.{domain}`). HTTP-01 and TLS-ALPN-01 are not used. Challenge TXT records use TTL `120` and `proxied=false`. A durable journal under `{AcmeStateDir}/dns01/journal/` supports crash recovery cleanup. Authoritative TXT visibility is checked before challenges are triggered. After triggers, the issuer polls ACME authorization/order state until the order is `ready` (or fails/times out) before finalization — DNS visibility is not treated as ACME validation success. ACME challenge TXT must not be confused with A/AAAA FQDN reconciliation — reconcile mutates only A/AAAA for `{Fqdn}`; challenge records use distinct `_acme-challenge.*` names.

### Persistence

The Windows Certificate Store is **not** used (`X509Store` is not employed).

| Path | Format |
|------|--------|
| `{AcmeStateDir}/account/private_key.der` | PKCS#8 DER **ACME account private key** (separate from the TLS credential) |
| `{AcmeStateDir}/account/registration.json` | Account URI + directory URL metadata |
| `{AcmeStateDir}/live/current` | Active generation id |
| `{AcmeStateDir}/live/gens/{id}/certificate.pfx` | PKCS#12/PFX: leaf certificate + private key + issuing chain |
| `{AcmeStateDir}/live/gens/{id}/complete` | Marker written after PFX write → reload → validate succeeds |

`certificate.pfx` is the canonical TLS server credential. It is loaded with `AcmeCertificatePassword` into an `X509Certificate2` (with private key) for the future TLS listener. Incomplete generations (missing `complete` or invalid `current`) are never treated as active. A known-good generation remains current until a replacement PFX is validated and committed.

### Lifecycle

When TLS is enabled, `AcmeCertificateService` runs after Cloudflare DNS reconciliation:

1. Ensure ACME account (reuse persisted key / register once)
2. Evaluate existing certificate (SANs, validity, key match, renewal threshold)
3. Issue or renew via ACME when needed
4. Expose material through `IServerCertificateProvider` for a future TLS listener

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

After options validation, `CloudflareDnsReconciliationService` (an `IApplicationService`) is registered **first** among application services:

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

NNTP listeners/sockets are **not** implemented yet. DNS reconciliation publishes the **resolved eligible bind-address set** derived from `BindAddress` and local NIC enumeration — not a measured set of currently bound sockets. When wildcard binding is configured, DNS enumerates eligible NIC addresses that a future `0.0.0.0` / `::` listener would accept; when explicit IPs are configured, DNS publishes those eligible addresses. Do not interpret startup DNS success as proof that sockets are already listening.

**Runtime:** A/AAAA are reconciled at **startup**; the exact FQDN is removed at **shutdown**. NIC address changes and configuration reloads are not monitored while Running. If the eventual listen set diverges after start, DNS can drift until the next successful startup reconciliation.

### Environment variables

Use these exact names:

```text
nntpd__cloudflareapikey
nntpd__CloudFlareZoneId
nntpd__ServerId
nntpd__AcmeCertificatePassword
```

Example (user scope, PowerShell — replace secret values locally; do not commit them):

```powershell
[Environment]::SetEnvironmentVariable("nntpd__cloudflareapikey", "<YOUR_API_KEY>", "User")
[Environment]::SetEnvironmentVariable("nntpd__CloudFlareZoneId", "5811a29d39a0732afb5f160c9b137c3d", "User")
[Environment]::SetEnvironmentVariable("nntpd__ServerId", "1", "User")
[Environment]::SetEnvironmentVariable("nntpd__AcmeCertificatePassword", "<YOUR_PFX_PASSWORD>", "User")
```

`DnsSuffix` should correspond to the zone identified by `CloudFlareZoneId`. The host validates DNS suffix **syntax** only; it does not verify zone membership via the Cloudflare API.

## ServerId (required)

`ServerId` has **no default**. It must be configured explicitly as an integer from `1` through `99` (for example via `Nntpd:ServerId` or `nntpd__ServerId`). Missing, null, unparsable, `0`, negative, or greater-than-`99` values cause a hard startup failure. A missing value is distinguishable from an explicit `0` (both fail). The CLR default must not make an omitted setting appear valid (`int?` remains unset until configured).

## Generated FQDN

The FQDN is computed only from validated `ServerId` and `DnsSuffix`:

```text
nntpd{ServerId:00}.{DnsSuffix}
```

There is **no** dot between `nntpd` and the two-digit id.

| `ServerId` | FQDN |
| ---------: | ---- |
| 1 | `nntpd01.usenet.ninja` |
| 8 | `nntpd08.usenet.ninja` |
| 9 | `nntpd09.usenet.ninja` |
| 10 | `nntpd10.usenet.ninja` |
| 99 | `nntpd99.usenet.ninja` |

The FQDN cannot be set in `appsettings.json` or overridden by an environment variable. Dependent services must use the generated value only after source settings have been validated.
