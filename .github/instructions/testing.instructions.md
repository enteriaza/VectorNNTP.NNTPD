---
applyTo: "**/*Tests.cs"
---

# Testing and validation

- Preserve the existing xUnit suites, benchmark contract tests, coverage, and established regression baselines. Never remove, skip, weaken, suppress, bypass, or conditionally disable a test merely to obtain a passing build.
- Test observable production behavior and engineering contracts rather than implementation details. Prioritize meaningful coverage of configuration diagnostics, protocol parsing, provider failures, cancellation, ownership, exactly-once settlement, backpressure, resource disposal, readiness, startup ordering, and layered shutdown.
- A test is valuable only when it can detect the regression it is intended to protect. Avoid tests whose setup guarantees the expected result, assertions that do not establish the important outcome, and mocks that prevent the relevant failure from being observable.
- Make tests deterministic. Prefer explicit coordination through TaskCompletionSource, channels, barriers, semaphores, cancellation sources, lifecycle signals, and awaited task completion over Thread.Sleep, arbitrary Task.Delay, timing-based polling, or machine-speed assumptions.
- Treat asynchronous, concurrent, lifecycle, and resource-ownership tests as high-risk. Check for races, deadlocks, lost task failures, unobserved exceptions, premature disposal, use-after-disposal, use-after-returned-buffer, cancellation races, shutdown races, and tasks that outlive their owning resources.
- For concurrency and lifecycle tests, isolate one test or method per process where required by the repository. Clean stale test processes before execution, use the repository watchdog where appropriate, and capture available forensic information before terminating a hung testhost.
- Tests involving RabbitMQ, NNTP, external providers, DNS, databases, certificates, sockets, or other dependencies must distinguish real production behavior from test-double behavior. Do not assume a mock or fake accurately represents the production dependency unless that behavior has been verified.
- For work involving queues, RabbitMQ, ACK/NACK, retry, completion, cancellation, timeout, or shutdown, explicitly protect the exactly-once settlement invariant. Verify success, failure, cancellation, timeout, retry, disposal, and shutdown paths where applicable.
- For resource-owning tests, verify acquisition, ownership transfer, cleanup, disposal, cancellation cleanup, exception cleanup, and repeated-disposal behavior where those are part of the production contract.
- For protocol tests, cover meaningful boundary conditions and failure responses rather than only happy paths. Include malformed input, unexpected responses, partial data, connection closure, authentication failure, timeout, cancellation, unavailable articles, and retryable versus terminal failures where applicable.
- For configuration and startup tests, protect validation ordering, invalid combinations, canonical configuration state, dependency readiness, externally visible side effects, startup failure behavior, and readiness semantics where applicable.
- For shutdown tests, verify cancellation propagation, work admission, draining, settlement, connection closure, background-task completion, disposal, and readiness transitions. Do not use arbitrary sleeps to make shutdown appear complete.
- Tests must be isolated from execution order, unrelated global state, stale processes, previous test artifacts, developer-specific configuration, and machine-specific timing unless the dependency is itself the behavior under test.
- Do not disable parallel test execution broadly to hide races or isolation problems. If serialization is genuinely required, establish why and scope it as narrowly as possible.
- Preserve established test and benchmark baselines. In particular, do not weaken or remove the known TransitPublisherTests 44/44 regression baseline when those tests are affected.
- Benchmark contract tests must remain meaningful. Do not modify benchmark contracts, expected runtime identity, architecture, configuration, or validation requirements merely to make benchmark infrastructure pass.
- When a test fails, classify the failure before changing code. Consider production regression, exposed production bug, intentional contract change, stale test contract, test defect, race condition, environment/tooling issue, infrastructure/dependency failure, or unknown cause.
- Do not classify a failure as flaky without evidence. Reproduce where practical and inspect timing, synchronization, logs, task state, process state, and relevant production paths.
- Do not change production behavior merely to make a poorly designed test easier to satisfy. Conversely, do not weaken a test merely because it exposes a genuine production defect.
- Behavioral changes require meaningful regression coverage. The test should protect the actual failure mode or contract rather than merely exercising the changed lines.
- Do not add tests solely to increase line, branch, or method coverage. Add tests when they protect meaningful behavior, invariants, failure modes, or previously demonstrated regressions.
- Do not introduce dependency-injection seams, interfaces, wrappers, or other production abstractions solely to make straightforward code easier to mock unless the abstraction has an independent architectural purpose.
- Preserve useful existing tests even when they appear redundant unless there is evidence that they provide no meaningful protection. Do not remove regression coverage simply because another test currently exercises similar code.
- When changing tests, preserve the intent and diagnostic value of existing assertions. Prefer precise assertions that make failures immediately understandable.
- Avoid broad assertions such as merely checking that a task completed, an exception occurred, or a mock was called when the actual production contract requires verification of a specific result or state.
- Keep test setup and teardown explicit and deterministic. Every resource created by a test must have a clear owner and cleanup path.
- Test code should follow the repository's C# conventions, analyzer requirements, nullable rules, and documentation standards without weakening analyzer configuration or adding warning suppressions.
- Run restore, clean/build, the relevant test scope, and a warning-free rebuild for changed scope when validating an implemented change. Do not use --no-build when a clean build is required to establish the actual compiled test/runtime state.
- Do not broaden test execution unnecessarily when validating a narrowly scoped change. Start with the smallest relevant test scope, then expand only when required by the change or by a failure that needs additional diagnosis.
- Preserve forensic evidence from failing or hanging tests before terminating processes. Do not repeatedly kill and rerun a test without first attempting to determine why it hangs.
- Never suppress, skip, quarantine, delete, weaken, or bypass a failing test as a substitute for diagnosing the failure.
