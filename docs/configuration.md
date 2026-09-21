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
| `BindPortTls` | int | `0` | no | TLS NNTP TCP port; `0` / unset disables TLS (`1–65535` enables) |
| `CloudFlareApiKey` | string | _(none)_ | **yes** (secret) | Cloudflare API key for DNS integration |
| `CloudFlareZoneId` | string | _(none)_ | **yes** | Cloudflare zone identifier |
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
3. **Staged mutations (cross-family):** list A and AAAA, then **create all missing desired records for both families**, then unproxy kept desired records (`proxied: false`), then **delete stale/duplicate records for both families**, then re-list and verify. Create-all-before-delete prefers a temporary address **superset** over a temporary gap when replacing addresses. Create-before-delete does **not** eliminate intermediate visibility.
4. Skip mutations when records already match (exact normalized IP set and DNS-only).
5. **Verify** by listing again; success requires exact A and AAAA content sets **and** `proxied=false` on every remaining record for `{Fqdn}`. Mismatch, API failure, cancellation, or uncertain mutation → attempt failure (never treated as success).
6. During startup reconcile, only A/AAAA for the exact `{Fqdn}` are mutated. Other names and non-A/AAAA types are untouched until shutdown cleanup.

If reconcile fails after work has begun, the service attempts an authoritative cleanup of the exact FQDN before failing startup (best-effort; failure to clean up is logged and does not replace the original exception). Partial startup that successfully completed DNS reconcile then fails a later service triggers manager rollback, which calls `StopAsync` and removes the FQDN.

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
- **Permanent** failures (HTTP 4xx except 429; missing API key; known Cloudflare auth codes on HTTP 200 + `success: false`) fail immediately without reconciler retries.
- Transport timeouts / connection errors during mutations are marked **uncertain** (`CloudflareDnsException.IsOutcomeUncertain`): Cloudflare may already have applied the change. The client does not assume “no change”; the next attempt re-reads.
- Concurrent reconcile and cleanup calls on the **same reconciler instance** are serialized (shared gate). `ApplicationServiceManager` rejects concurrent start/stop. Multiple processes or external DNS managers targeting the same zone/FQDN are **not** coordinated; the host re-reads and fails closed on verification mismatch rather than claiming success.

**API permissions:** a Cloudflare API token with **Zone → DNS → Edit** (DNS Write) on the target zone. List operations need DNS Read (included in Edit).

**HTTP 429:** retried a limited number of times **per HTTP request** using `Retry-After` when present, otherwise short exponential backoff. Exhausted rate limits fail the current attempt (reconciler may still retry the full attempt if the failure is not marked permanent).

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
```

Example (user scope, PowerShell — replace the key value locally; do not commit it):

```powershell
[Environment]::SetEnvironmentVariable("nntpd__cloudflareapikey", "<YOUR_API_KEY>", "User")
[Environment]::SetEnvironmentVariable("nntpd__CloudFlareZoneId", "5811a29d39a0732afb5f160c9b137c3d", "User")
[Environment]::SetEnvironmentVariable("nntpd__ServerId", "1", "User")
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
