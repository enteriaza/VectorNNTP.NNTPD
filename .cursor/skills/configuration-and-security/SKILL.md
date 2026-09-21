---
name: configuration-and-security
description: >-
  Safely implements VectorNNTP configuration binding, validation, secrets handling,
  bind-address NIC checks, and Cloudflare environment variables. Use when changing
  NntpdOptions, validators, appsettings, env vars, secrets, or configuration docs/tests.
---

# Configuration and security

## Purpose

Keep configuration correct, fail-fast on mandatory errors, and never leak secrets.

## When to use

- Options / `appsettings.json` / env var changes
- Validation, bind addresses, ServerId/FQDN, Cloudflare settings
- Secret handling in logs, tests, or docs

## Authoritative contract

Inspect current sources before editing:

- `docs/configuration.md`
- `NntpdOptions`, `NntpdOptionsValidator`
- `NntpdServiceCollectionExtensions` (`BindConfiguration`, `ValidateOnStart`, bind-address normalize)
- `ILocalIpAddressAssignee` for deterministic NIC checks

Do not hardcode obsolete snake_case keys or old port defaults if the repo has moved on.

## Current naming (verify in repo)

PascalCase settings under section `Nntpd`, including: `BindAddress`, `BindPort`, `BindPortTls`, `CloudFlareApiKey`, `CloudFlareZoneId`, `DnsSuffix`, `ServerId`. Generated `Fqdn` is not independently configurable.

Cloudflare env vars (exact):

```text
nntpd__cloudflareapikey
nntpd__CloudFlareZoneId
```

## Mandatory workflow

1. Read existing options registration and validation path.
2. Distinguish **defaults** vs **required** settings; preserve missing-vs-zero distinctions (e.g. `ServerId` as `int?`).
3. Ensure invalid mandatory config fails **startup** clearly (`ValidateOnStart` / `IValidateOptions`).
4. Keep secrets out of `appsettings.json`, tracked samples, logs, exceptions, and test output.
5. Validate bind addresses: wildcards vs explicit IPs assigned to local NICs; no `IsGlobal` requirement.
6. Do not call Cloudflare or mutate DNS during ordinary validation.
7. Add deterministic validation tests with injected NIC data.
8. Update `docs/configuration.md` when the contract changes.

## Avoid

- Logging entire options objects that contain API keys
- Inventing restrictive API-key format validators
- Silent defaults for required settings
- Retaining undocumented obsolete aliases
- Committing `.env` files with secrets

## Related skills

- `testing` for deterministic options tests
- `documentation` for config docs accuracy

## Validation checklist

- [ ] Current contract inspected (not memorized)
- [ ] Required vs default behavior correct
- [ ] Startup validation fails clearly without secret leakage
- [ ] Env var names match documented exact strings
- [ ] Tests deterministic; no live Cloudflare
- [ ] Docs updated if behavior changed
