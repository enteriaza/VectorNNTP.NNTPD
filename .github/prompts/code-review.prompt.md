---
mode: agent
description: Review VectorNNTP.NNTPD changes for correctness, architecture, concurrency, and operational safety.
---

# VectorNNTP.NNTPD Code Review

Review the selected change as production code for a high-throughput, highly concurrent NNTP/Usenet recovery service.

The purpose of this review is to identify specific defects, correctness risks, architectural violations, concurrency failures, lifecycle problems, security issues, operational hazards, and material unintended behavior changes.

Do not treat this as a style review or an opportunity for unrelated refactoring.

Correctness and operational safety take precedence over optimization or stylistic preference.

## Execute the review continuously

Execute the entire review continuously from start to finish.

Do not stop after finding the first issue or after reviewing the first changed file.

Inspect the complete change and all relevant surrounding code needed to establish its actual behavior.

Only stop for:

- a genuine blocking issue;
- a compilation or test failure that requires user input;
- missing information that cannot reasonably be established from the repository;
- an explicit stop condition.

Do not ask for permission between individual review steps.

## Read the complete change first

Before reporting findings:

1. Read the complete diff.
2. Identify every changed file.
3. Read the complete contents of each changed file where necessary to understand context.
4. Inspect surrounding callers and consumers.
5. Inspect relevant interfaces, base classes, implementations, and abstractions.
6. Inspect relevant tests and regression coverage.
7. Inspect relevant configuration and startup/lifecycle code.
8. Inspect ownership, cancellation, disposal, and resource-lifetime paths.
9. Search the repository for related usages and assumptions.
10. Inspect relevant historical or performance context where the change affects an established contract or hot path.
11. Determine the actual behavior before and after the change.
12. Do not infer semantics from the diff alone.

A small diff can have a large behavioral impact.

Do not report a finding until the surrounding implementation has been inspected sufficiently to establish that the concern is real.

## Review priority

Review in this order:

1. Correctness;
2. Work ownership and exactly-once settlement;
3. Concurrency and synchronization;
4. Cancellation and lifecycle;
5. Resource ownership and disposal;
6. Protocol behavior;
7. Error and retry behavior;
8. Security and trust boundaries;
9. Startup/readiness/shutdown;
10. Configuration;
11. Logging and diagnostics;
12. API and compatibility;
13. Tests and regression protection;
14. Analyzer/documentation correctness;
15. Performance only where the change materially affects it.

Do not allow minor style or optimization concerns to obscure higher-severity correctness issues.

## Correctness

Determine whether the change preserves the intended production behavior.

Check for:

- incorrect state transitions;
- invalid assumptions;
- incorrect return values;
- lost results;
- duplicated results;
- incorrect error paths;
- incorrect success paths;
- invalid default behavior;
- boundary-condition failures;
- nullability violations;
- invalid casts;
- integer overflow/underflow;
- incorrect comparison semantics;
- incorrect ordering;
- stale state;
- partially applied state changes;
- inconsistent state after exceptions;
- behavior that differs between success, failure, cancellation, and shutdown;
- accidental behavior changes outside the intended scope.

Verify claims against actual callers and consumers.

Do not assume that code is correct merely because it compiles or existing tests pass.

## Exactly-once work settlement

Treat work settlement as a critical invariant.

For code involving article ownership, queues, RabbitMQ, ACK/NACK, completion, retry, failure, cancellation, timeout, or shutdown, trace the complete ownership lifecycle.

Verify that every admitted unit of work is settled exactly once.

Check for:

- double ACK;
- double NACK;
- ACK followed by NACK;
- NACK followed by ACK;
- lost settlement;
- settlement after ownership was transferred;
- settlement after cancellation;
- settlement after timeout;
- settlement during shutdown;
- duplicate retry;
- work being returned to multiple owners;
- work being silently dropped;
- exceptions bypassing settlement;
- cancellation bypassing settlement;
- cleanup paths performing settlement a second time.

Do not treat "the method is called once" as sufficient evidence if multiple control-flow paths can reach settlement.

Trace all success, failure, cancellation, timeout, and disposal paths.

## Concurrency and synchronization

Treat concurrent code as high-risk.

Check for:

- data races;
- unsynchronized shared state;
- incorrect atomicity assumptions;
- lock ordering problems;
- deadlocks;
- lock contention;
- starvation;
- lost wakeups;
- race-dependent lifecycle transitions;
- double initialization;
- double disposal;
- use-after-disposal;
- use-after-returned-buffer;
- concurrent collection misuse;
- channel misuse;
- incorrect memory visibility;
- task completion races;
- cancellation races;
- shutdown races.

Pay particular attention to code that changes:

- shared state;
- connection state;
- queue state;
- lifecycle state;
- ownership state;
- buffer ownership;
- counters;
- retry state;
- readiness state.

Do not recommend replacing synchronization with lock-free mechanisms merely because they appear faster.

## Async and task behavior

Inspect asynchronous code for:

- blocking waits;
- `.Wait()`;
- `.Result`;
- synchronous I/O;
- unobserved task failures;
- fire-and-forget tasks;
- missing awaits;
- incorrect task ownership;
- task lifetime exceeding owning resources;
- premature task completion;
- continuation races;
- cancellation registration leaks;
- excessive task fan-out;
- unbounded concurrency;
- incorrect exception propagation.

Verify that asynchronous operations do not outlive the objects or buffers they depend upon.

Do not assume that an async method is safe merely because it returns `Task`.

## Cancellation

Verify cancellation behavior across every affected asynchronous operation.

Check:

- cancellation before work starts;
- cancellation during work;
- cancellation while waiting;
- cancellation during I/O;
- cancellation during retries;
- cancellation during shutdown;
- cancellation after partial completion;
- cancellation combined with disposal;
- cancellation combined with timeout;
- cancellation combined with settlement.

Ensure cancellation does not accidentally cause:

- lost work;
- duplicate work;
- duplicate settlement;
- resource leaks;
- swallowed exceptions;
- incorrect success reporting;
- premature shutdown.

Do not catch and suppress `OperationCanceledException` without establishing why that behavior is correct.

## Resource ownership and disposal

Trace ownership of:

- sockets;
- streams;
- connections;
- channels;
- buffers;
- pooled arrays;
- memory owners;
- timers;
- cancellation registrations;
- tasks;
- database resources;
- certificates;
- cryptographic resources;
- dependency clients.

For every changed ownership path, determine:

- who creates the resource;
- who owns it;
- when ownership transfers;
- who disposes it;
- what happens on exceptions;
- what happens on cancellation;
- what happens during shutdown;
- whether disposal can race with active use;
- whether disposal can occur twice;
- whether resources can leak.

Treat ownership mistakes as correctness defects, not merely cleanup concerns.

## Buffer and pool safety

For `ArrayPool<T>`, `MemoryPool<T>`, `IMemoryOwner<T>`, `Memory<T>`, `ReadOnlyMemory<T>`, `Span<T>`, or similar resources, verify:

- acquisition;
- ownership;
- lifetime;
- asynchronous use;
- mutation;
- return/disposal;
- exception paths;
- cancellation paths;
- shutdown paths.

Look specifically for:

- use-after-return;
- double-return;
- returning the wrong buffer;
- retaining pooled memory too long;
- exposing mutable pooled memory beyond its lifetime;
- reading from disposed memory owners.

Do not recommend pooling unless it is justified by the actual ownership model and workload.

## Protocol behavior

For NNTP and related network protocols, verify:

- protocol state transitions;
- command ordering;
- response parsing;
- multiline responses;
- connection closure;
- authentication;
- timeout behavior;
- retry behavior;
- malformed responses;
- unexpected responses;
- partial reads;
- partial writes;
- message identifiers;
- article availability;
- provider failures;
- connection reuse;
- reconnect behavior.

Check that protocol changes preserve the expected wire behavior.

Do not assume that a change is safe because it works against a single happy-path server response.

## External dependencies and trust boundaries

Treat external NNTP providers, RabbitMQ, DNS, databases, Cloudflare, ACME/certificate infrastructure, and other external inputs as failure-prone or untrusted.

Check:

- malformed responses;
- unavailable services;
- authentication failures;
- timeouts;
- connection resets;
- partial data;
- unexpected status codes;
- invalid identifiers;
- invalid certificates;
- dependency startup failures;
- dependency shutdown;
- retry storms;
- resource exhaustion;
- unbounded response sizes;
- untrusted input reaching sensitive operations.

Do not assume external services behave like the test doubles.

## Retry and failure handling

Trace every affected failure path.

Check:

- retry classification;
- retry limits;
- backoff;
- duplicate retries;
- retry after successful completion;
- retry after settlement;
- retry after cancellation;
- retry after shutdown;
- terminal versus transient failures;
- exception classification;
- preservation of original failure context.

A retry mechanism must not amplify a failure into uncontrolled work or queue growth.

## Startup and readiness

For startup/configuration/lifecycle changes, verify:

- configuration binding;
- validation ordering;
- canonical runtime snapshot creation;
- directory validation;
- dependency validation;
- service construction;
- service startup;
- readiness;
- externally visible side effects.

Do not infer readiness from DI registration order.

Where explicit lifecycle state exists, verify that readiness is signaled only at the correct operational milestone.

Do not allow expensive or irreversible externally visible work to occur before required validation.

## Shutdown and draining

For shutdown-related changes, verify:

- cancellation propagation;
- admission control;
- queue draining;
- active-work completion;
- settlement;
- connection closure;
- resource disposal;
- background task completion;
- readiness transition;
- bounded shutdown.

Check for:

- new work admitted after shutdown begins;
- abandoned work;
- duplicate settlement;
- premature disposal;
- tasks that prevent shutdown;
- tasks that outlive their owning service;
- resources left open;
- shutdown that depends on arbitrary sleeps.

Prefer deterministic lifecycle coordination.

## Logging and diagnostics

Review logging changes for correctness and operational safety.

Check:

- structured logging;
- correlation identifiers;
- `MessageId`;
- outcome fields;
- duration fields;
- UTC timestamps;
- culture-invariant machine-facing formatting;
- exception preservation;
- event IDs;
- log levels;
- message templates;
- disabled-level cost;
- hot-path allocation;
- duplicate logging.

Ensure protocol TX/RX logging does not expose:

- article payloads;
- credentials;
- secrets;
- sensitive connection information.

Do not remove useful operational diagnostics merely to reduce log volume or benchmark cost.

Where logging is on a hot path, ensure expensive data is not constructed unnecessarily when the relevant level is disabled.

## Security

Review changed trust boundaries and sensitive operations.

Check for:

- credential leakage;
- secret logging;
- sensitive data exposure;
- unsafe certificate validation;
- improper hostname validation;
- command injection;
- path traversal;
- unsafe deserialization;
- unbounded input;
- denial-of-service opportunities;
- insecure defaults;
- privilege assumptions;
- accidental exposure of internal state.

Treat external protocol and configuration input as untrusted.

Do not report theoretical security concerns without a credible path to impact.

## Configuration

For configuration changes, verify:

- validation;
- defaults;
- invalid combinations;
- precedence;
- startup ordering;
- runtime immutability where expected;
- canonical snapshot behavior;
- backward compatibility;
- environment-variable behavior;
- configuration-dependent resource creation.

Do not allow configuration to create externally visible side effects before validation completes.

## API and behavior compatibility

Check for unintended changes to:

- public API;
- internal contracts;
- constructor behavior;
- method signatures;
- accessibility;
- serialization;
- configuration formats;
- protocol behavior;
- log contracts;
- event IDs;
- structured logging fields;
- exit codes;
- lifecycle states.

Distinguish intentional behavior changes from accidental ones.

Do not recommend API changes merely for stylistic consistency.

## Analyzers and documentation

Review changed code for:

- compiler warnings;
- analyzer violations;
- nullable-reference issues;
- XML documentation errors;
- invalid documentation references;
- inappropriate suppressions;
- `NoWarn`;
- disabled analyzers.

Do not recommend suppressing a warning merely to obtain a clean build.

Fix the underlying source or design issue where appropriate.

Do not turn documentation review into a demand for boilerplate comments.

Documentation should describe actual engineering contracts.

## Tests and regression protection

Determine whether the change has adequate regression protection.

Check:

- existing tests;
- affected failure paths;
- new behavior;
- changed behavior;
- cancellation;
- disposal;
- concurrency;
- protocol behavior;
- configuration;
- lifecycle;
- exactly-once settlement.

Behavioral changes should have meaningful regression coverage.

Tests should assert observable behavior and actual invariants rather than implementation details.

Do not recommend removing, skipping, weakening, suppressing, or bypassing tests merely to obtain a passing build.

Preserve established regression baselines, including the known `TransitPublisherTests` `44/44` baseline where applicable.

If a change is documentation-only, do not demand tests merely because code coverage is incomplete.

## Performance

Review performance only when the change materially affects a hot path, resource lifetime, throughput, latency, or scalability.

Consider:

- allocations;
- GC/LOH pressure;
- copying;
- buffer ownership;
- pooling;
- synchronization;
- contention;
- task overhead;
- socket I/O;
- connection utilization;
- queueing;
- backpressure;
- logging;
- CPU;
- memory;
- throughput;
- latency;
- tail latency.

Do not recommend theoretical micro-optimizations.

Require a stable baseline and measurable evidence, or identify a credible measurement plan, before treating an optimization as worthwhile.

Keep performance review subordinate to correctness.

## Scope and unintended changes

Determine whether the change is narrowly scoped to its stated purpose.

Look for:

- unrelated behavior changes;
- accidental refactoring;
- changed defaults;
- changed logging;
- changed error handling;
- changed timing;
- changed concurrency;
- changed resource lifetime;
- changed API surface;
- modified tests unrelated to the change;
- configuration changes without corresponding justification.

Do not report harmless formatting changes as defects unless they create a meaningful review or merge risk.

Do not recommend unrelated cleanup.

## Finding standard

Report only specific, actionable defects or material risks.

A finding must have a credible chain:

1. identify the changed behavior;
2. identify the violated or endangered contract;
3. explain the concrete failure mode;
4. explain the likely impact;
5. identify the smallest appropriate correction.

Do not report:

- personal stylistic preferences;
- speculative micro-optimizations;
- hypothetical problems without a credible execution path;
- generic maintainability advice;
- "could be cleaner" observations;
- unrelated technical debt.

If no material defects are found, say so clearly.

Do not manufacture findings to make the review appear thorough.

## Severity

Classify findings as:

- Critical — severe correctness, security, data-integrity, settlement, lifecycle, or operational failure with substantial impact.
- High — material production defect or serious regression risk.
- Medium — meaningful defect or reliability risk with constrained impact or triggering conditions.
- Low — minor defect with a credible but limited impact.
- Observation — useful context that does not require a change.

Severity must reflect actual impact and likelihood, not how interesting the issue appears.

## Recommended correction

For every actionable finding, provide:

- severity;
- file and location;
- defect/risk;
- concrete impact;
- why the current implementation is insufficient;
- recommended correction;
- relevant regression test or validation requirement.

Prefer the smallest correction that restores the actual contract.

Do not prescribe a large refactor when a targeted correction is sufficient.

## Do not modify code during review

This is a review task.

Do not modify production code, tests, configuration, documentation, benchmarks, or other repository files unless the user explicitly asks for implementation of the review findings.

If implementation is explicitly requested, keep the changes narrowly scoped to the approved findings.

Never modify unrelated files.

## Validation

If no changes are requested:

- do not alter the repository;
- inspect the complete diff and relevant context;
- report the review findings.

If changes are explicitly requested:

1. Verify the final diff.
2. Confirm unrelated files were not modified.
3. Build the relevant project(s).
4. Run the relevant tests.
5. Verify affected regression baselines.
6. Confirm analyzers and documentation remain valid.
7. Confirm no unintended behavior changes were introduced.

Do not weaken tests, suppress analyzers, or alter baselines to make validation pass.

## Final report

Provide:

- overall assessment;
- scope reviewed;
- files reviewed;
- relevant production paths inspected;
- findings ordered by severity;
- file and location for each finding;
- concrete impact for each finding;
- recommended correction for each finding;
- required regression coverage;
- any important areas explicitly reviewed with no material findings;
- any pre-existing issues discovered but intentionally left untouched;
- build result, if validation was performed;
- test result, if tests were run;
- confirmation that no unrelated refactoring was recommended;
- confirmation that no tests were weakened, skipped, removed, or suppressed.

If there are no material findings, explicitly state that no actionable defects or material risks were identified.
