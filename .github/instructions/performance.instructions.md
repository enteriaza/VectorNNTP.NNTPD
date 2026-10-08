---
applyTo: "**/*.{cs,csproj,json,md}"
---

# Performance engineering

VectorNNTP.NNTPD is intended for very-high-throughput NNTP/network service operation. Treat hot paths, network I/O, queueing, parsing, buffering, connection management, and high-frequency logging as performance-sensitive while preserving correctness, resource ownership, cancellation, settlement, lifecycle, and shutdown semantics.

- Avoid unnecessary allocations, copies, boxing, closures, LINQ, temporary collections, task creation, synchronization, lock contention, and retained large objects or buffers on hot paths.
- Pay particular attention to Gen 0/1/2 pressure, LOH allocations, pinned memory, buffer retention, pooled-resource lifetime, and unbounded growth.
- Make buffer and pooled-resource ownership explicit. Never trade allocation reduction for use-after-return, double-return, premature disposal, or lifetime violations.
- Prefer asynchronous, cancellable socket and stream I/O. Avoid blocking waits, synchronous I/O on high-concurrency paths, fire-and-forget work with unobserved failures, and unnecessary task or continuation overhead.
- Prefer bounded queues and explicit backpressure. Do not remove or weaken backpressure merely to increase apparent throughput. Consider queue depth, admission control, memory growth, starvation, retry amplification, and shutdown draining.
- Consider throughput, bytes/sec, work/sec, mean latency, tail latency, CPU utilization, CPU per unit of work, memory, allocation/GC behavior, connection utilization, synchronization, contention, and long-running resource stability.
- Keep high-frequency logs cheap and structured. Avoid constructing expensive diagnostic data when the relevant log level is disabled. Use metrics for high-volume telemetry that would otherwise create excessive log traffic. Never remove security or operationally important diagnostics solely for performance.
- Treat network performance as an end-to-end property. Distinguish application CPU, socket behavior, TCP/TLS overhead, connection churn, provider limits, remote-server processing, network latency, throttling, and external bandwidth constraints.
- Establish a clean and reproducible baseline before changing performance-sensitive code. Use the same build configuration, runtime, architecture, workload, concurrency, topology, and relevant environmental conditions for before/after comparisons.
- Evaluate one meaningful performance change at a time where practical. Require credible measurements before treating an optimization as successful. Do not manufacture expected percentage improvements or accept theoretical micro-optimizations without evidence.
- Do not optimize solely because an allocation, branch, abstraction, LINQ expression, Task, ValueTask, Span, pool, lock, virtual call, or method call exists. First establish that it is relevant to the actual workload and execution path.
- Do not introduce complexity, unsafe code, custom pooling, lock-free synchronization, specialized data structures, or other optimization machinery without a credible performance justification and a clear ownership/correctness model.
- A throughput increase is not automatically an improvement if it materially increases tail latency, CPU cost, memory consumption, queue growth, connection churn, error rate, or resource retention.
- A reduction in CPU or allocations is not automatically an improvement if throughput, latency, correctness, or operational stability deteriorates.
- Preserve exactly-once work settlement, ACK/NACK semantics, retry behavior, cancellation, disposal, readiness, graceful shutdown, and other production invariants when changing performance-sensitive code.
- Keep production implementation, benchmark harness, fake/test server, synthetic workload generator, and real external endpoint behavior clearly separated when evaluating measurements.
- Do not optimize production code to satisfy an unrealistic benchmark harness. If benchmark behavior is not representative of the production path, identify the benchmark limitation separately.
- Preserve established benchmark contracts, runtime identity, architecture, configuration, and CI expectations.
- Benchmark validation must follow the repository's clean/build/run policy. Do not use `--no-build` when the repository requires a clean build and runtime-identity verification.
- Use the repository watchdog and required isolated execution procedure for potentially blocking lifecycle, concurrency, or benchmark tests.
- Treat `.copilot/PERFORMANCE-CONTEXT.md` as the current performance checkpoint when it is relevant to the reviewed area. Preserve it unless the task explicitly requires updating a verified performance result, decision, or experiment outcome.
- When newer source, benchmark, test, or artifact evidence conflicts with the performance checkpoint, prefer the newer verified evidence and identify stale checkpoint information rather than silently relying on outdated measurements.
- Do not reset, delete, weaken, or reinterpret an established performance baseline merely because a change performs worse. Determine whether the baseline is still representative before proposing a replacement.
- Performance changes must remain behavior-preserving unless a behavioral change is explicitly intended and reviewed separately.
- If a performance concern cannot be established from repository evidence or credible measurements, describe it as a hypothesis and identify an appropriate measurement plan rather than presenting it as a defect.
