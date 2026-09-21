---
name: lifecycle-and-concurrency
description: >-
  Protects VectorNNTP application lifecycle, service orchestration, cancellation,
  disposal, and concurrency safety around ApplicationLifecycle and hosted services.
  Use when changing startup/shutdown, IApplicationService, systemd notify/watchdog,
  async ownership, or diagnosing races, abandoned tasks, or shutdown bugs.
---

# Lifecycle and concurrency

## Purpose

Keep startup, running, and shutdown correct under concurrency and failure.

## When to use

- Changes to `ApplicationLifecycle`, `ApplicationServiceManager`, hosted services, systemd integration
- Cancellation, disposal, or concurrent stop/start work
- Lifecycle or concurrency bugs and their tests

## Authoritative architecture

Inspect before changing:

- `docs/architecture.md`
- `ApplicationLifecycle` state machine and serialization rules
- `ApplicationServiceManager` start order / reverse stop / rollback
- `NntpdHostedService`, `NntpdHostLifetime` single-flight shutdown
- `SystemdLifecycleNotifier`, `SystemdWatchdogService`, `IApplicationHealth`

Do **not** invent readiness or ordering guarantees. Prefer verified code comments and tests.

## Mandatory workflow

1. Restate the lifecycle contract under change (states, ordering, idempotency).
2. Trace ownership of tasks, CTS, sockets/streams (when present), and disposables.
3. Map cancellation: who owns the token; expected vs exceptional cancel.
4. Check concurrent stop/dispose/start interactions.
5. Ensure exceptions on background work are observed and classified.
6. Add deterministic lifecycle regression tests (see `testing` skill).
7. Run relevant tests; report exact results.

## Invariants to verify (when present in code)

- Valid state transitions only; invalid transitions throw as documented
- Startup failure rolls back partially started services
- Repeated stop is safe (await in-flight stop)
- Concurrent lifecycle ops are serialized where the implementation requires it
- Unexpected service termination behavior matches options (`StopHostOnUnexpectedServiceTermination`)
- systemd READY/STOPPING/WATCHDOG stay off the future NNTP data path
- No custom competing SIGTERM/SIGINT handlers unless architecture changes intentionally

## Avoid

- Blocking waits / sleeps to “wait for shutdown”
- Fire-and-forget without observation
- Assuming DI construction order equals readiness
- Premature dispose of resources still used by async work
- Importing RabbitMQ drain/settlement shutdown models from BackFiller

## Validation checklist

- [ ] Contract traced in real lifecycle types
- [ ] Ownership and cancellation paths explicit
- [ ] Concurrent stop/dispose considered
- [ ] Background exceptions observed
- [ ] Deterministic tests added/updated and run
- [ ] No invented lifecycle guarantees in docs/comments
