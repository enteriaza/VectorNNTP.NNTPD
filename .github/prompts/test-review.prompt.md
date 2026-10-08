---
mode: agent
description: Review VectorNNTP.NNTPD tests for meaningful, deterministic regression protection.
---

# VectorNNTP.NNTPD Test Review

Review the selected test file, test class, or test area as an engineering regression-protection review.

The goal is not to maximize test count or coverage percentage.

The goal is to determine whether the tests provide strong, deterministic protection for the actual production contracts, failure modes, lifecycle guarantees, concurrency invariants, protocol behavior, and operational requirements of VectorNNTP.NNTPD.

## Execute the review continuously

Execute the entire review continuously from start to finish.

Do not stop after reviewing the first test, class, or file.

Inspect all relevant tests and production code needed to establish the actual contract before forming conclusions.

Only stop for:

- a genuine blocking issue;
- a compilation or test failure that requires user input;
- missing information that cannot reasonably be established from the repository;
- an explicit stop condition.

Do not ask for permission between individual review steps.

## Understand the production contract first

Before judging whether a test is correct or sufficient:

1. Read the complete target test file or selected test area.
2. Identify every test, fixture, helper, theory, data source, setup path, and teardown path.
3. Locate the production code exercised by those tests.
4. Inspect relevant callers, consumers, interfaces, base types, configuration, lifecycle orchestration, and related tests where necessary.
5. Search the repository for additional usages and related regression coverage.
6. Determine the actual observable production contract.
7. Identify the failure modes the tests are intended to prevent.
8. Compare those contracts and failure modes against what the tests actually prove.

Do not infer production semantics solely from:

- test names;
- assertion messages;
- comments;
- parameter names;
- mocked interfaces;
- expected values;
- method names.

Verify important claims against the implementation and repository usage.

## Review assertions for meaningful protection

For every test, determine whether it would actually fail if the intended production behavior regressed.

Look specifically for:

- assertions that do not test the important outcome;
- assertions against implementation details instead of observable behavior;
- tests that can pass even when the production behavior is broken;
- overly broad assertions;
- assertions that validate only that an operation completed;
- missing negative/error-path assertions;
- missing state-transition assertions;
- missing ordering assertions where ordering is contractual;
- missing ownership/settlement assertions;
- tests that exercise code without establishing a meaningful invariant;
- mocks or stubs that make the test incapable of detecting the relevant failure;
- tests whose setup accidentally guarantees the expected result regardless of production behavior.

A test should provide evidence of a meaningful contract, not merely execute code.

## Regression protection

Determine what regression each test protects against.

Strong tests should protect actual production behavior such as:

- article recovery and processing;
- NNTP protocol behavior;
- connection lifecycle;
- configuration validation;
- startup ordering;
- dependency readiness;
- graceful shutdown;
- cancellation;
- disposal;
- queue/backpressure behavior;
- RabbitMQ settlement;
- exactly-once settlement;
- retry and failure handling;
- timeout behavior;
- listener/transit behavior;
- validation result mapping;
- logging contracts where logging itself is an observable requirement;
- resource ownership;
- concurrency invariants;
- protocol edge cases;
- operational failure modes.

Identify important production behaviors that are not adequately covered.

Do not recommend additional tests merely because a method or branch lacks coverage.

Recommend additional tests when there is a meaningful unprotected contract or failure mode.

## Async and concurrency tests

Treat asynchronous and concurrent tests as high-risk areas.

Check for:

- `Thread.Sleep`;
- `Task.Delay` used as synchronization;
- polling loops without deterministic completion;
- arbitrary timeouts used to hide races;
- race-prone shared state;
- fire-and-forget tasks;
- tasks that are started but not awaited;
- background exceptions that can escape observation;
- cancellation tokens that are never exercised;
- disposal occurring concurrently with active operations;
- deadlock potential;
- hangs that are difficult to diagnose;
- tests that depend on machine speed;
- tests that pass only because of timing luck;
- insufficient synchronization around lifecycle transitions.

Prefer deterministic coordination such as:

- `TaskCompletionSource`;
- explicit signals;
- channels;
- semaphores;
- barriers;
- cancellation;
- explicit lifecycle state;
- awaited task completion.

Do not recommend arbitrary sleeps as a synchronization mechanism.

A timeout may be appropriate as a safety bound, but it must not be the mechanism that makes the test deterministic.

## Cancellation and disposal

Where production behavior is asynchronous or resource-owning, verify that tests meaningfully exercise:

- cancellation before work begins;
- cancellation during active work where applicable;
- cancellation during shutdown;
- disposal after normal completion;
- disposal after failure;
- disposal while asynchronous operations remain active;
- repeated disposal where the contract permits it;
- correct propagation or suppression of cancellation;
- cleanup after cancellation;
- cleanup after exceptions.

Do not add cancellation/disposal tests merely for coverage.

Test them where they protect an actual lifecycle or ownership contract.

## Exactly-once settlement

For work involving queues, RabbitMQ, article ownership, ACK/NACK, retry, completion, or failure settlement, explicitly verify exactly-once behavior.

Check for regressions involving:

- double ACK;
- double NACK;
- ACK followed by NACK;
- NACK followed by ACK;
- settlement after cancellation;
- settlement after timeout;
- settlement during shutdown;
- lost settlement;
- duplicate retry;
- work being returned to multiple owners;
- work being silently dropped.

Where exactly-once settlement is a production invariant, tests should assert the invariant rather than merely checking that a method was called.

## Protocol and configuration tests

For NNTP and configuration behavior, check meaningful boundary and failure cases.

Consider:

- malformed protocol input;
- unexpected protocol responses;
- connection closure;
- authentication failure;
- timeout;
- cancellation;
- partial data;
- invalid message identifiers;
- unavailable articles;
- provider failures;
- retryable versus terminal failures;
- configuration validation;
- invalid combinations of configuration values;
- wildcard/listener semantics;
- IPv4/IPv6 behavior;
- dependency readiness;
- startup failure ordering.

Do not recommend exhaustive combinatorial testing unless the contract warrants it.

Prioritize cases that represent realistic operational failures or previously demonstrated regressions.

## Test isolation and determinism

Tests must not depend on:

- execution order;
- another test having run first;
- global mutable state;
- machine-specific timing;
- local environment assumptions;
- external services unless the test explicitly requires them;
- developer-specific configuration;
- undeclared files or directories;
- leftover processes;
- previous test artifacts.

Check fixtures for correct setup and teardown.

Check that tests clean up resources they own.

Check that parallel execution cannot create races unless parallelism itself is deliberately under test.

If a test requires serialization, determine whether that requirement is real and document the reason rather than masking the problem with broad disabling of parallel execution.

## Test doubles and mocks

Evaluate whether mocks, stubs, fakes, and test doubles represent the actual production contract.

Look for:

- mocks that are too permissive;
- mocks that cannot reproduce the failure being tested;
- excessive mocking of implementation details;
- verification of calls without verification of outcomes;
- setups that make failure impossible;
- unrealistic protocol responses;
- test doubles that behave differently from production abstractions;
- brittle mock expectations that would reject harmless implementation changes.

Prefer testing observable behavior over incidental call structure.

Do not recommend introducing interfaces or dependency-injection seams solely to make straightforward code easier to mock.

## Lifecycle and hosted-service tests

For worker, hosted-service, startup, readiness, and shutdown tests, verify:

- startup ordering;
- validation before externally visible work;
- dependency readiness;
- readiness signaling;
- failure propagation;
- cancellation;
- graceful drain;
- bounded shutdown;
- disposal;
- background task completion;
- no work being admitted after shutdown begins;
- no work being abandoned during normal shutdown.

Do not infer readiness from service-registration order.

Where the project has an explicit lifecycle state or readiness contract, tests should verify that contract directly.

## Existing regression baselines

Preserve established regression baselines and CI expectations.

In particular, do not weaken or remove established `TransitPublisherTests` coverage or the known `44/44` baseline when those tests are part of the reviewed area.

Protect benchmark contracts and benchmark infrastructure tests from accidental weakening.

Do not modify tests simply because they are difficult, slow, or sensitive if that sensitivity represents a real production invariant.

If a test is genuinely flaky, identify the underlying cause and distinguish:

1. test synchronization defect;
2. production concurrency defect;
3. environmental/infrastructure issue;
4. legitimate timing-sensitive production behavior.

Do not classify a failure as test flakiness without evidence.

## Failure classification

When a test fails or appears unreliable, determine whether the evidence points to:

- a test defect;
- a production defect;
- a race condition;
- an invalid assumption in the test;
- an environmental failure;
- a dependency failure;
- a timeout caused by the test;
- a timeout caused by the production implementation;
- an expected platform-specific difference.

Do not recommend changing a test merely because it exposes a production failure.

Do not recommend changing production code merely because a test is inconvenient.

## What not to recommend

Never recommend:

- removing tests to obtain a passing build;
- skipping tests;
- weakening assertions;
- suppressing test failures;
- increasing arbitrary timeouts merely to hide failures;
- replacing deterministic synchronization with sleeps;
- disabling parallel test execution without establishing why it is required;
- deleting regression coverage;
- reducing assertions solely to make tests less brittle;
- changing production behavior solely to accommodate a poorly designed test;
- suppressing analyzers instead of fixing the underlying issue;
- adding tests solely to increase coverage percentages;
- mocking implementation details solely because they are easy to verify.

## Recommendations

For every issue found, classify it appropriately.

Use categories such as:

- Critical — regression protection is materially absent or the test can provide false confidence.
- High — important production behavior is inadequately protected.
- Medium — meaningful weakness or nondeterminism exists but the primary contract remains covered.
- Low — maintainability or clarity issue with limited regression risk.
- Observation — useful context that does not require a change.

For each actionable recommendation, explain:

1. what is wrong;
2. why it matters;
3. what production failure it could allow;
4. what the test should establish instead;
5. the smallest appropriate change.

Do not provide vague recommendations such as "add more coverage."

## Changes

This is a review task.

Do not modify production code or tests unless the user explicitly asks for implementation of the recommended changes.

If implementation is explicitly requested, keep changes narrowly scoped to the identified regression-protection issue and preserve existing test intent and baselines.

Never modify unrelated files.

## Validation

If no changes are requested, do not alter the repository.

If changes are explicitly requested:

1. Verify the final diff.
2. Confirm that unrelated files were not modified.
3. Build the relevant project(s).
4. Run the relevant test scope.
5. Confirm that established regression baselines remain intact.
6. Report failures accurately rather than weakening tests to make them pass.

## Final report

Provide:

- overall assessment;
- tests/classes/files reviewed;
- strongest regression protections identified;
- false-positive or weak tests identified;
- missing meaningful regression coverage;
- determinism/concurrency concerns;
- cancellation/disposal concerns;
- exactly-once settlement concerns;
- protocol/configuration concerns;
- lifecycle/startup/shutdown concerns;
- test-isolation concerns;
- mock/test-double concerns;
- benchmark/CI baseline concerns;
- recommendations classified by severity;
- any failures encountered and their classification;
- changes made, if changes were explicitly requested;
- build result, if validation was performed;
- test result, if tests were run;
- confirmation that no tests were weakened, skipped, removed, or suppressed.
