# VectorNNTP.NNTPD Code Review Skill

## Purpose

Review changes to the VectorNNTP.NNTPD codebase for correctness, reliability, performance, concurrency safety, resource safety, maintainability, and consistency with the established architecture.

VectorNNTP.NNTPD is a high-performance .NET service responsible for retrieving unavailable Usenet articles from external NNTP providers.

The service is designed for very high throughput and high concurrency. Code review must therefore consider both functional correctness and behaviour under sustained load, failure, cancellation, and shutdown.

---

## Review Philosophy

Prioritise findings in this order:

1. Correctness
2. Data and work integrity
3. Concurrency and thread safety
4. Resource lifetime and disposal
5. Cancellation and shutdown correctness
6. Reliability and failure handling
7. Security
8. Performance
9. Observability
10. Maintainability

Do not recommend changes merely because they are stylistically different from the reviewer's preferred implementation.

Do not introduce abstraction, complexity, allocation, synchronisation, or indirection without a concrete benefit.

Prefer the smallest change that correctly solves the identified problem.

---

## Mandatory Review Process

Before making recommendations:

1. Read the complete changed file(s).
2. Inspect the surrounding implementation and call sites.
3. Identify the relevant interfaces, base classes, consumers, producers, and services.
4. Inspect existing tests covering the affected behaviour.
5. Search for related implementations elsewhere in the repository.
6. Understand ownership and lifecycle of affected resources.
7. Understand cancellation and shutdown propagation.
8. Check whether the proposed behaviour is consistent with the existing architecture.
9. Only then identify defects or improvement opportunities.

Do not review a changed method in isolation when its correctness depends on callers, consumers, producers, lifecycle management, shared state, or shutdown coordination.

---

## Scope Discipline

A review must distinguish between **defects**, **risks**, and **improvements**.

### Defects

Issues that can cause:

- Incorrect behaviour
- Lost work
- Duplicate work
- Incorrect settlement
- Data corruption
- Resource leaks
- Deadlocks
- Race conditions
- Unbounded resource consumption
- Incorrect cancellation
- Incorrect shutdown
- Security vulnerabilities
- Production failures

These should be reported clearly and prominently.

### Risks

Behaviour that may be correct but has a meaningful operational or scalability risk.

### Improvements

Potential enhancements that are not required for correctness.

Do not present subjective preferences as defects.

Do not recommend unrelated refactoring merely because it could make the code "cleaner".

---

## Concurrency

Concurrency is a critical concern in VectorNNTP.NNTPD.

For every change involving shared state, queues, consumers, producers, workers, providers, or asynchronous operations, explicitly consider:

- Thread safety
- Race conditions
- Atomicity
- Memory visibility
- Ordering
- Concurrent mutation
- Duplicate completion
- Lost completion
- Work ownership
- Work transfer
- Cancellation races
- Shutdown races

Pay particular attention to code using:

- `Interlocked`
- `Volatile`
- `lock`
- `SemaphoreSlim`
- `Channel<T>`
- `ConcurrentDictionary`
- `ConcurrentQueue<T>`
- `ConcurrentBag<T>`
- `Task`
- `ValueTask`
- `CancellationToken`
- `CancellationTokenSource`

Do not assume that asynchronous code is automatically thread-safe.

---

## Work Admission and Settlement

Work admission and settlement are critical invariants.

Every admitted unit of work must eventually reach exactly one terminal settlement path.

Review changes for:

- Work admitted but never settled
- Work settled more than once
- Work removed from accounting without settlement
- Settlement occurring before ownership is established
- Settlement occurring after ownership has already transferred
- Cancellation paths that bypass settlement
- Exceptions that bypass settlement
- Early returns that bypass settlement
- Shutdown paths that leave admitted work outstanding

The following invariant must always hold:

> Every admitted unit of work is settled exactly once.

This invariant applies regardless of whether the work:

- Succeeds
- Fails
- Times out
- Is cancelled
- Is rejected by an upstream provider
- Encounters a protocol error
- Is interrupted during shutdown

---

## Cancellation

Cancellation must be treated as a correctness concern, not merely a convenience.

Review:

- Which cancellation token is being used
- Who owns the token
- When cancellation can occur
- Whether cancellation is expected or exceptional
- Whether cancellation propagates correctly
- Whether cancellation can race with completion
- Whether cancellation can race with acknowledgement
- Whether cancellation can race with settlement
- Whether cancellation can cause work to disappear without accounting

Distinguish carefully between:

- Cancellation before admission
- Cancellation during admission
- Cancellation after admission
- Cancellation during network I/O
- Cancellation during processing
- Cancellation during settlement
- Cancellation during shutdown

Do not assume that `OperationCanceledException` means no work was admitted.

---

## Shutdown and Drain Behaviour

Shutdown is a first-class requirement.

Review all shutdown-related changes for:

- New work being accepted after shutdown begins
- Queued work being stranded
- Admitted work being stranded
- Incorrect drain counts
- Incorrect completion counts
- Cancellation bypassing settlement
- Tasks being abandoned
- Connections being disposed prematurely
- Consumers stopping before their work has been accounted for
- Producers stopping before pending work is settled
- Shutdown completing before all required work has reached a terminal state

Where applicable, verify the distinction between:

- Stopping admission
- Cancelling pending work
- Cancelling admitted work
- Draining admitted work
- Final settlement
- Resource disposal

A shutdown implementation must not report completion while tracked admitted work remains unsettled.

---

## NNTP Protocol Correctness

Review NNTP-related changes for protocol correctness.

Pay attention to:

- Command ordering
- Response-code handling
- Multiline responses
- Dot-stuffing
- Message termination
- Header/body boundaries
- Message identifiers
- Article retrieval semantics
- Connection reuse
- Connection reset behaviour
- Partial responses
- Unexpected responses
- Provider-specific behaviour
- Authentication state
- Timeout handling

Never assume that a network connection represents a valid NNTP session merely because the socket is connected.

---

## Network I/O

BackFiller is expected to operate at very high network throughput.

Review network changes for:

- Blocking I/O
- Unnecessary synchronous operations
- Excessive buffering
- Unnecessary buffer copies
- Excessive allocations
- Incorrect stream lifetime
- Premature disposal
- Connection leaks
- Socket exhaustion
- Incorrect timeout handling
- Cancellation propagation
- Partial reads and writes
- Handling of remote connection termination

Avoid introducing synchronous waits such as:

```csharp
.Wait()
.Result
.GetAwaiter().GetResult()
```

unless there is a documented and unavoidable reason.

---

## Performance

BackFiller is a performance-sensitive service.

Performance review should focus on measurable impact rather than theoretical micro-optimisation.

Consider:

- Allocations
- Garbage collection
- CPU consumption
- Lock contention
- Context switching
- Queue contention
- Memory bandwidth
- Network throughput
- Connection utilisation
- Task overhead
- Async state-machine overhead
- Buffer copying
- String allocations
- LINQ usage in hot paths
- Logging overhead
- Synchronisation overhead

Be especially suspicious of new allocations inside high-frequency loops.

Look for:

- Repeated string construction
- Unnecessary `ToString()`
- Interpolated strings in hot paths
- LINQ over large collections
- Temporary arrays
- Repeated collection resizing
- Boxing
- Closure allocations
- Delegate allocations
- Unnecessary `Task` creation
- Unnecessary `ValueTask` conversion
- Buffer copies

However:

> Do not sacrifice correctness or clarity for a theoretical performance improvement without evidence that the code is performance-sensitive.

---

## Memory Management

Review high-throughput paths for unnecessary memory retention.

Consider:

- Object lifetime
- Buffer lifetime
- Pool ownership
- Returning pooled buffers
- Retaining large objects
- Large Object Heap pressure
- Collection growth
- Cached data
- Temporary allocations
- Memory leaks caused by incomplete cleanup

If `ArrayPool<T>` or other pooling mechanisms are used, verify that buffers are:

- Returned exactly once
- Not returned while still in use
- Not accessed after return
- Cleared when required by security or correctness considerations

---

## Async and Task Usage

Review asynchronous code for:

- Unnecessary task creation
- Incorrect `async` and `await`
- Missing awaits
- Fire-and-forget operations
- Lost exceptions
- Incorrect cancellation handling
- Synchronisation-context assumptions
- Sequential awaits that should be concurrent
- Concurrent operations that should be sequential
- `Task.Run` used unnecessarily for I/O

Fire-and-forget work is particularly dangerous in a service.

If an asynchronous operation is intentionally detached, verify that:

- Its lifetime is understood
- Exceptions are observed
- Shutdown behaviour is defined
- Resource ownership remains valid
- The operation cannot silently lose work

---

## Exceptions and Failure Handling

Review exception handling for:

- Exceptions being swallowed
- Exceptions being logged and rethrown unnecessarily
- Incorrect exception classification
- Cancellation being treated as a normal failure
- Provider failures being treated as application failures
- Permanent failures being retried indefinitely
- Transient failures not being retried where appropriate
- Retry storms
- Lost stack traces
- Cleanup being skipped after exceptions

Avoid broad exception handling such as:

```csharp
catch (Exception)
{
}
```

unless there is a compelling and documented reason.

---

## Retry Behaviour

Review retry logic for:

- Infinite retries
- Missing retry limits
- Retry storms
- Duplicate work
- Incorrect backoff
- Retrying non-transient failures
- Retrying after cancellation
- Retrying after shutdown
- Provider health implications
- Work accounting across retries

A retry must not accidentally create a second independently tracked unit of work when the original work remains tracked.

---

## Resource Lifetime

Every acquired resource must have a clearly defined owner and disposal path.

Review:

- `IDisposable`
- `IAsyncDisposable`
- Streams
- Sockets
- Network connections
- `CancellationTokenSource` instances
- Timers
- Semaphores
- Channels
- Pooled buffers
- Temporary files

Look for:

- Leaks
- Double disposal
- Premature disposal
- Disposal from the wrong owner
- Disposal while asynchronous work is still active

---

## Logging

Logging must provide useful operational information without becoming a performance problem.

Review:

- Log level selection
- Sensitive information
- Excessive logging
- Hot-path logging
- Structured logging
- Correlation information
- Provider identification
- Article/request identification
- Exception preservation

Never log:

- Passwords
- Authentication tokens
- API keys
- Secrets
- Sensitive credentials

Avoid expensive message construction when the log level is disabled.

Prefer structured logging over constructing large strings manually.

---

## Observability

Where applicable, review changes for their impact on:

- Metrics
- Counters
- Histograms
- Queue depth
- Active work
- Provider health
- Provider utilisation
- Request latency
- Retrieval latency
- Error rates
- Retry rates
- Cancellation rates
- Throughput

Instrumentation should provide useful operational information without materially degrading the hot path.

---

## Testing

Every behavioural change should have appropriate test coverage.

Tests should cover the normal path and relevant failure paths.

For concurrency and lifecycle changes, specifically consider tests for:

- Successful completion
- Failure
- Cancellation
- Timeout
- Provider failure
- Empty responses
- Invalid responses
- Partial responses
- Concurrent completion
- Concurrent cancellation
- Shutdown during processing
- Shutdown before admission
- Shutdown after admission
- Duplicate completion attempts
- Settlement without ACK/NACK
- ACK/NACK followed by cancellation

Regression tests should reproduce the actual failure mode rather than merely increasing general coverage.

---

## Test Quality

Do not accept tests that only verify implementation details when the actual requirement is behavioural.

Prefer assertions against:

- Observable results
- State transitions
- Work accounting
- Settlement
- Cancellation behaviour
- Resource ownership
- Provider interactions

Tests should be deterministic.

Avoid arbitrary sleeps such as:

```csharp
await Task.Delay(1000);
```

when synchronisation primitives or deterministic coordination can be used instead.

---

## API and Public Surface

Review changes to public APIs carefully.

Consider:

- Breaking changes
- Nullability
- Cancellation-token conventions
- Default values
- API consistency
- Backwards compatibility
- Documentation
- Thread-safety guarantees

Do not expose internal implementation details unnecessarily.

---

## Configuration

Review configuration changes for:

- Sensible defaults
- Validation
- Fail-fast behaviour
- Environment-specific configuration
- Secret handling
- Backwards compatibility
- Configuration reload implications
- Invalid or dangerous values

Configuration that can cause uncontrolled concurrency, memory consumption, or connection usage should have appropriate validation and limits.

---

## Security

Review for:

- Secret exposure
- Credential leakage
- Unsafe deserialisation
- Injection vulnerabilities
- Untrusted input
- Path traversal
- Authentication bypass
- Improper authorisation
- Sensitive logging
- Dependency vulnerabilities
- Unsafe network behaviour

External Usenet provider responses must be treated as untrusted input.

---

## Dependency Changes

When dependencies change, review:

- Whether the dependency is necessary
- Version selection
- Known vulnerabilities
- Transitive dependencies
- Runtime compatibility
- Licensing implications
- Performance impact
- Maintenance status

Avoid adding a dependency for functionality that can be implemented safely and efficiently using existing platform capabilities.

---

## Code Style

Follow the repository's existing coding conventions.

Do not propose broad stylistic rewrites unless they address a concrete problem.

Prefer:

- Clear names
- Small focused methods
- Explicit ownership
- Predictable control flow
- Appropriate comments explaining *why*, not merely *what*
- Existing repository patterns

Avoid unnecessary abstraction.

---

## Review Severity

Classify findings using the following levels.

### Critical

Immediate security vulnerability, severe data or work loss, deadlock, corruption, or a failure that can make the service fundamentally unsafe to operate.

### High

A significant correctness, concurrency, resource, reliability, or shutdown defect likely to cause production failures.

### Medium

A meaningful defect or operational risk that should be addressed but is unlikely to cause catastrophic failure.

### Low

A minor correctness, maintainability, observability, or performance issue.

### Informational

An optional improvement, observation, or recommendation that is not a defect.

Do not inflate severity.

---

## Review Output

When performing a code review, report findings in this order:

1. Critical
2. High
3. Medium
4. Low
5. Informational

For every actionable finding, include:

- **Severity**
- **Location**
- **Problem**
- **Why it matters**
- **Recommended correction**

Keep findings specific and actionable.

Example:

```text
### HIGH — Admitted work can bypass settlement

**Location:** `RabbitMqBackboneConsumerSession.cs:123`

**Problem:**
When cancellation occurs after the delivery has been admitted, the cancellation path exits without performing the required settlement.

**Why it matters:**
The drain counter remains permanently elevated, causing shutdown to wait indefinitely.

**Recommended correction:**
Ensure the cancellation path settles the admitted delivery exactly once while preserving the existing ACK/NACK settlement semantics.
```

---

## False Positives

Before reporting a finding, verify whether the apparent issue is intentionally handled elsewhere.

Search for:

- Cleanup in callers
- Cleanup in `finally`
- Completion callbacks
- Consumer-level settlement
- Provider-level recovery
- Centralised exception handling
- Shutdown coordination
- Existing concurrency guards

Do not report an issue simply because the handling is not visible in the method currently being reviewed.

---

## Changes That Require Extra Scrutiny

Perform especially thorough review when changes affect:

- Work admission
- Work queues
- Consumer sessions
- Provider connections
- Connection pooling
- Cancellation
- Shutdown
- Drain accounting
- ACK/NACK handling
- Retry logic
- Buffer management
- Memory pooling
- High-frequency loops
- Shared state
- `Interlocked` operations
- Locking
- Async coordination
- Backpressure
- Concurrency limits

These areas can introduce subtle production failures even when the code appears straightforward.

---

## Review Invariants

The following invariants should be preserved unless the architecture explicitly changes them:

1. Every admitted unit of work is settled exactly once.
2. Cancellation must not silently lose admitted work.
3. Shutdown must not complete while required admitted work remains unsettled.
4. ACK/NACK semantics must remain correct.
5. Resources must remain owned until all dependent asynchronous work has completed.
6. External provider failures must not crash the service unnecessarily.
7. Retry behaviour must remain bounded and controlled.
8. Concurrency must remain bounded by the configured policy.
9. High-throughput paths must not introduce unnecessary allocation or synchronisation.
10. Exceptions must not silently disappear.
11. Secrets must never be exposed through source code or logging.
12. Behavioural changes must have appropriate regression coverage.

---

## Final Review Standard

A change is acceptable only when it is:

- Functionally correct
- Concurrency-safe
- Cancellation-safe
- Shutdown-safe
- Resource-safe
- Testable
- Observable
- Appropriate for sustained high-throughput operation
- Consistent with the existing architecture

Do not optimise for the smallest diff at the expense of correctness.

Do not optimise for theoretical performance at the expense of maintainability.

Do not approve code merely because tests pass.

The purpose of review is to identify defects and risks that may only appear under concurrency, sustained load, failure, cancellation, or production operating conditions.
