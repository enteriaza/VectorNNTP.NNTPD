---
mode: agent
description: Review VectorNNTP.NNTPD changes as high-throughput NNTP and network infrastructure.
---

# VectorNNTP.NNTPD Performance Review

Review the selected production code, benchmark, test, configuration, or performance-related change as high-throughput NNTP/network infrastructure.

The goal is not to maximize theoretical throughput or eliminate every allocation.

The goal is to identify changes that materially affect throughput, latency, tail latency, CPU consumption, memory usage, allocation/GC behavior, connection utilization, queueing, backpressure, scalability, resource lifetime, or operational stability while preserving correctness and lifecycle guarantees.

Treat performance as an engineering property that must be demonstrated with evidence.

## Execute the review continuously

Execute the entire review continuously from start to finish.

Do not stop after reviewing the first method, file, benchmark, or suspected bottleneck.

Inspect all relevant production code, benchmarks, tests, configuration, topology, and historical performance evidence needed to establish the actual behavior before forming conclusions.

Only stop for:

- a genuine blocking issue;
- a compilation or test failure that requires user input;
- missing information that cannot reasonably be established from the repository;
- an explicit stop condition.

Do not ask for permission between individual review steps.

## Establish the performance context first

Before making performance recommendations:

1. Read the complete target file or selected change.
2. Identify the execution path affected by the change.
3. Locate callers and consumers.
4. Inspect relevant interfaces, abstractions, configuration, lifecycle orchestration, benchmarks, tests, and supporting infrastructure.
5. Search the repository for related implementations and usages.
6. Read `.copilot/PERFORMANCE-CONTEXT.md` when it is relevant to the reviewed area.
7. Inspect existing benchmark results, baselines, historical measurements, or performance notes where available.
8. Determine whether the code is on a hot path, warm path, startup path, shutdown path, or operationally insignificant path.
9. Identify the expected workload, concurrency, data volume, network behavior, and resource lifetime.
10. Establish which performance characteristics are actually contractual or operationally important.

Do not infer performance importance solely from:

- method names;
- apparent algorithmic complexity;
- allocation-looking syntax;
- intuition about async/await;
- intuition about pooling;
- intuition about sockets;
- intuition about LINQ;
- intuition about logging;
- benchmark names.

Verify important claims against actual call paths, workload characteristics, measurements, and repository evidence.

## Performance evidence hierarchy

Use evidence in approximately this order:

1. Controlled measurements from the actual production path.
2. Reproducible benchmark results using stable configuration.
3. Historical repository benchmark results and established baselines.
4. Production-like controlled harness measurements.
5. Source-level analysis supported by execution-path evidence.
6. Static reasoning and theoretical analysis.

Do not treat a theoretical optimization as a demonstrated performance improvement.

If measurement is unavailable, state that clearly and provide a credible measurement plan rather than presenting speculation as fact.

When `.copilot/PERFORMANCE-CONTEXT.md` conflicts with newer source, benchmark, test, or artifact evidence, prefer the newer verified evidence and identify the checkpoint as stale if appropriate.

Do not manufacture benchmark numbers, expected percentage improvements, or performance claims.

## Establish a stable baseline

Before evaluating a performance change, determine whether there is an appropriate baseline.

A meaningful baseline should use, where applicable:

- the same source revision or clearly identified before/after revisions;
- the same build configuration;
- the same target framework;
- the same runtime;
- the same architecture;
- the same operating-system environment;
- the same CPU topology;
- the same NUMA topology where relevant;
- the same network path;
- the same provider/server;
- the same payload characteristics;
- the same concurrency;
- the same queue depth;
- the same duration;
- the same warm-up conditions;
- the same GC/runtime settings;
- the same benchmark parameters.

Do not compare measurements taken under materially different conditions without accounting for the difference.

For noisy measurements, prefer repeated runs and distributions over a single observed result.

Where BenchmarkDotNet is used, inspect the benchmark configuration and generated results rather than relying only on a displayed mean.

## One measurable change at a time

Performance changes should normally isolate one meaningful variable.

Do not combine unrelated optimizations and then attribute the entire observed improvement to one change.

If multiple changes are already combined, identify the attribution problem and recommend a controlled experiment where appropriate.

Prefer:

1. establish baseline;
2. make one targeted change;
3. rebuild under the same conditions;
4. rerun the same workload;
5. compare the relevant metrics;
6. determine whether the difference is material;
7. retain or revert based on evidence.

Do not recommend optimization solely because a change "looks faster."

## Hot-path analysis

Identify whether the reviewed code executes frequently enough for its cost to matter.

For hot paths, inspect:

- allocations;
- temporary objects;
- arrays;
- spans;
- strings;
- string formatting;
- parsing;
- encoding/decoding;
- copying;
- buffer slicing;
- pooling;
- memory ownership;
- synchronization;
- locks;
- interlocked operations;
- channels;
- queues;
- task creation;
- continuations;
- async state machines;
- cancellation registration;
- exception construction;
- logging;
- LINQ;
- enumerators;
- delegates;
- closures;
- boxing;
- virtual/interface dispatch;
- repeated configuration access;
- repeated DNS or endpoint work;
- socket operations;
- system calls.

Do not assume any one of these is a problem merely because it exists.

Determine whether its frequency, size, contention, lifetime, or measured cost makes it relevant.

## Allocations, GC, and memory

Review:

- allocation rate;
- allocation frequency;
- object lifetime;
- Gen 0/1/2 pressure;
- LOH allocations;
- pinned memory;
- fragmentation;
- retained buffers;
- pooled-object lifetime;
- pool growth;
- array ownership;
- `ArrayPool<T>` usage;
- `MemoryPool<T>` usage;
- accidental copies;
- duplicate buffering;
- string allocations;
- encoding allocations;
- collection growth;
- unbounded caches;
- queues that can retain work;
- connection-associated memory.

Pay particular attention to memory that grows with:

- connection count;
- queue depth;
- article size;
- concurrency;
- retry count;
- provider count;
- lifetime.

Distinguish transient allocation pressure from retained memory growth.

Do not recommend eliminating every allocation.

A small allocation outside a hot path may be preferable to substantially more complex code.

## Buffer and pool ownership

For pooled or reusable buffers, verify:

- who acquires the buffer;
- who owns it;
- who may read it;
- who may mutate it;
- when ownership transfers;
- when it is returned;
- whether asynchronous operations can still access it;
- whether exceptions return it correctly;
- whether cancellation returns it correctly;
- whether shutdown returns it correctly;
- whether double-return is possible;
- whether use-after-return is possible.

Treat buffer lifetime and ownership as correctness issues as well as performance issues.

Never recommend pooling solely because pooling sounds faster.

Pooling is worthwhile only when its lifecycle, contention, memory retention, and measured allocation cost justify its complexity.

## Copying and data movement

Look for unnecessary:

- array copies;
- buffer copies;
- string conversions;
- encoding conversions;
- stream-to-buffer copies;
- buffer-to-buffer copies;
- materialization;
- intermediate collections;
- duplicate protocol framing.

Determine whether the copy is actually avoidable without compromising:

- ownership;
- lifetime;
- protocol correctness;
- cancellation;
- disposal;
- security;
- readability;
- maintainability.

Do not remove a copy merely because it appears inefficient.

If a copy protects ownership or lifetime, treat that protection as part of the design contract.

## Async and task overhead

Review asynchronous code for:

- unnecessary task creation;
- unnecessary allocations;
- excessive continuation overhead;
- avoidable async state machines;
- unnecessary context capture;
- synchronous blocking;
- `.Wait()`;
- `.Result`;
- blocking synchronization;
- fire-and-forget work;
- unobserved exceptions;
- cancellation-registration overhead;
- excessive task fan-out;
- unbounded concurrency.

Consider whether `ValueTask` or other specialized mechanisms are actually justified by measured hot-path behavior.

Do not recommend replacing `Task` with `ValueTask` merely because it can reduce allocations in theory.

Do not introduce asynchronous complexity where the workload is not performance-sensitive.

## Synchronization and contention

Inspect:

- locks;
- monitors;
- semaphores;
- mutexes;
- concurrent collections;
- channels;
- atomics;
- interlocked operations;
- reader/writer locks;
- shared mutable state;
- global queues;
- per-connection state;
- centralized counters.

Determine whether synchronization occurs:

- per article;
- per message;
- per packet;
- per connection;
- per provider;
- per batch;
- per lifecycle transition.

A synchronization mechanism that is acceptable once per connection may be unacceptable millions of times per second.

Look for contention amplification as concurrency increases.

Do not replace synchronization with lock-free structures merely because lock-free code appears faster.

Correctness, memory ordering, ownership, and maintainability remain mandatory.

## Queueing and backpressure

Review queue and channel behavior for:

- bounded versus unbounded capacity;
- producer/consumer imbalance;
- queue growth;
- memory retention;
- admission control;
- backpressure propagation;
- cancellation;
- shutdown draining;
- dropped work;
- duplicate work;
- starvation;
- head-of-line blocking;
- retry amplification.

Determine whether queue depth is a useful leading indicator of overload.

Do not "optimize" by removing backpressure.

Backpressure is part of the system's stability model.

A throughput increase that causes uncontrolled queue growth is not necessarily an improvement.

## Network and socket performance

For NNTP/network paths, inspect:

- connection reuse;
- connection pooling;
- connection concurrency;
- socket lifetime;
- DNS behavior;
- endpoint selection;
- TCP/TLS setup;
- receive/send buffering;
- read/write sizes;
- protocol pipelining;
- request batching;
- timeout behavior;
- connection churn;
- reconnect behavior;
- provider throttling;
- network backpressure.

Distinguish application-level throughput limitations from:

- remote-server limits;
- network capacity;
- TCP behavior;
- TLS overhead;
- provider throttling;
- DNS;
- endpoint selection;
- connection limits.

Do not attribute a network bottleneck to application code without evidence.

## Throughput, latency, and tail latency

Do not evaluate performance using throughput alone.

Where applicable, examine:

- articles/second;
- bytes/second;
- requests/second;
- average latency;
- median latency;
- P95;
- P99;
- P99.9;
- maximum latency where meaningful;
- queueing delay;
- service time;
- connection setup time;
- provider response time.

A change that increases throughput while materially degrading tail latency may be a regression.

A change that reduces average latency while increasing resource consumption or tail latency may also be a regression.

Determine which metrics matter for the actual workload.

## CPU and scalability

Review:

- CPU utilization;
- single-thread bottlenecks;
- synchronization contention;
- per-connection CPU cost;
- per-article CPU cost;
- parsing/encoding cost;
- cryptographic cost;
- logging cost;
- scheduling overhead;
- thread-pool pressure;
- scalability as concurrency increases.

Distinguish:

- total CPU;
- CPU per unit of work;
- CPU per byte;
- CPU per connection.

A higher total CPU utilization is not automatically bad if useful throughput increased proportionally.

A lower CPU percentage is not automatically good if throughput also decreased.

Prefer normalized measurements where appropriate.

## Logging and diagnostics

Review logging on hot paths for:

- unnecessary string construction;
- interpolation;
- boxing;
- structured logging usage;
- disabled-level work;
- excessive event volume;
- logging inside tight loops;
- payload logging;
- duplicate logging;
- synchronous logging operations.

Preserve diagnostic value.

Do not remove important operational logging solely for theoretical performance.

Where expensive diagnostic data is constructed, determine whether it is correctly guarded against disabled log levels.

Never remove security, failure, or operational diagnostics merely to improve benchmark numbers.

## Production versus benchmark infrastructure

Explicitly separate:

- production implementation;
- benchmark harness;
- fake/test server;
- synthetic workload generator;
- real external endpoint;
- local network behavior;
- benchmark setup and teardown.

A benchmark improvement is not automatically a production improvement.

A benchmark regression is not automatically a production regression.

Determine whether the benchmark actually exercises the production path.

Look for benchmark artifacts such as:

- fake-server behavior that is unrealistically fast;
- zero network latency;
- unlimited remote capacity;
- unrealistic response sizes;
- missing TLS;
- missing authentication;
- simplified protocol behavior;
- client-only timing;
- setup/teardown excluded from measurements;
- benchmark-only allocations;
- benchmark harness contention;
- artificial connection reuse;
- unrealistic concurrency.

Do not optimize production code to satisfy a misleading benchmark.

If the benchmark itself is invalid, identify that separately.

## Real endpoint versus synthetic results

When real NNTP endpoints or controlled network infrastructure are available, distinguish their behavior from local synthetic measurements.

Account for:

- network RTT;
- server processing;
- provider throttling;
- remote connection limits;
- server-side queueing;
- packet loss;
- TLS;
- geographic distance;
- bandwidth limits.

Do not claim a production-network improvement from a local microbenchmark without evidence connecting the two.

## Startup and lifecycle performance

Performance is not limited to the article hot path.

Review startup/shutdown costs where relevant:

- configuration binding;
- validation;
- directory checks;
- DNS;
- dependency probes;
- certificate operations;
- connection establishment;
- hosted-service startup;
- readiness;
- graceful drain;
- shutdown timeout;
- disposal.

Do not optimize startup by moving expensive or externally visible work ahead of required validation.

Do not trade lifecycle correctness for startup speed.

## Resource growth and long-running stability

For a service expected to run continuously, inspect whether the change can cause resource growth over time.

Look for growth correlated with:

- connections;
- articles;
- retries;
- queue depth;
- provider count;
- errors;
- cancellation;
- reconnects;
- shutdown cycles.

Consider:

- memory;
- buffers;
- sockets;
- tasks;
- timers;
- registrations;
- event handlers;
- caches;
- queues;
- metrics state.

A change that performs well for five minutes but leaks resources over many hours is not a performance success.

## Correctness takes precedence

Performance improvements must preserve:

- article correctness;
- protocol correctness;
- cancellation;
- disposal;
- ownership;
- exactly-once settlement;
- ACK/NACK semantics;
- retry semantics;
- timeout semantics;
- graceful shutdown;
- readiness;
- configuration validation;
- logging contracts where observable;
- security boundaries.

Do not accept a measurable speedup that introduces a correctness or lifecycle regression.

Do not recommend weakening correctness guarantees to improve benchmark throughput.

## Avoid theoretical micro-optimization

Do not recommend changes solely because they might:

- remove one allocation;
- reduce one branch;
- avoid one method call;
- replace a loop with LINQ or vice versa;
- replace `Task` with `ValueTask`;
- add `static`;
- change a collection type;
- use `Span<T>`;
- use `Memory<T>`;
- use pooling;
- add aggressive inlining;
- alter struct/class representation;
- change virtual dispatch;
- remove `ConfigureAwait(false)`;
- introduce unsafe code;
- add lock-free synchronization.

First establish that the operation is relevant to the workload.

Then establish that the proposed change is safe.

Then measure it.

If evidence does not justify the change, say so.

## Experimental discipline

For a proposed optimization, identify:

- hypothesis;
- affected execution path;
- baseline;
- independent variable;
- controlled variables;
- workload;
- metrics;
- expected signal;
- acceptable variance;
- correctness checks;
- resource-stability checks.

Prefer one measurable change at a time.

If the expected improvement is smaller than normal measurement noise, do not present the result as meaningful.

When appropriate, recommend multiple repeated runs and compare distributions rather than relying on a single run.

Do not cherry-pick favorable benchmark iterations.

## Benchmark integrity

Check benchmark code for:

- correct warm-up;
- sufficient measurement iterations;
- appropriate invocation counts;
- stable environment;
- representative workload;
- realistic data;
- correct setup/cleanup boundaries;
- accidental measurement of setup;
- accidental exclusion of relevant work;
- dead-code elimination;
- benchmark-only shortcuts;
- incorrect async measurement;
- hidden allocations;
- environmental interference.

For BenchmarkDotNet tests, preserve the project's established runtime, architecture, configuration, and benchmark contract.

Do not use `--no-build` when validating benchmark behavior where the repository requires a clean build and runtime identity check.

## Existing performance baselines

Preserve established performance baselines unless there is explicit evidence that they are obsolete.

When reviewing changes that affect benchmarked paths:

- identify the existing baseline;
- identify the new measurement;
- compare under equivalent conditions;
- determine whether the difference is material;
- identify regressions in secondary metrics even when the headline metric improves.

Do not reset, delete, weaken, or reinterpret a baseline simply because a change performs worse.

If a baseline is no longer representative, document why and identify what evidence supports replacing it.

## Recommendations

For each performance issue, classify it appropriately.

Use categories such as:

- Critical — severe resource growth, scalability failure, correctness-threatening performance behavior, or major regression.
- High — material throughput, latency, CPU, memory, contention, or scalability regression.
- Medium — meaningful performance weakness with limited current impact or incomplete optimization evidence.
- Low — minor inefficiency with credible but limited benefit.
- Observation — useful performance context that does not currently justify a change.
- Experiment — plausible optimization hypothesis requiring controlled measurement before implementation.

For each actionable recommendation, explain:

1. what the current behavior is;
2. why it matters to the actual workload;
3. what evidence supports the concern;
4. what production impact is expected;
5. what the smallest appropriate change would be;
6. how the change should be measured;
7. what correctness/resource invariants must remain intact.

Do not provide vague recommendations such as:

- "optimize this";
- "reduce allocations";
- "use pooling";
- "make it async";
- "use Span";
- "increase concurrency";
- "add caching".

Explain the specific bottleneck and evidence.

## Changes

This is a performance review task.

Do not modify production code, benchmarks, tests, configuration, or performance artifacts unless the user explicitly asks for implementation of the recommended changes.

If implementation is explicitly requested:

- keep changes narrowly scoped;
- establish or preserve a baseline;
- make one meaningful performance change at a time where practical;
- preserve correctness and lifecycle semantics;
- update or add measurements where necessary;
- do not weaken tests or benchmark contracts;
- do not modify unrelated files.

Do not change `.copilot/PERFORMANCE-CONTEXT.md` merely because a review was performed.

Only update the performance checkpoint when the task explicitly requires recording a new verified performance result, decision, or experiment outcome.

## Validation

If no changes are requested, do not alter the repository.

If changes are explicitly requested:

1. Verify the final diff.
2. Confirm that unrelated files were not modified.
3. Build the relevant project(s) using the repository's required configuration.
4. Run the relevant tests.
5. Run the relevant benchmark or controlled performance workload.
6. Verify runtime identity and benchmark configuration where applicable.
7. Compare against the established baseline.
8. Inspect throughput, latency/tails, CPU, allocations/GC, memory, queues, and connections as applicable.
9. Confirm correctness and lifecycle invariants remain intact.
10. Report failures accurately rather than weakening tests or benchmarks.

Do not declare an optimization successful solely because the code compiles.

## Final report

Provide:

- overall performance assessment;
- production execution paths reviewed;
- benchmarks reviewed;
- tests reviewed;
- relevant configuration reviewed;
- relevant topology reviewed;
- existing baseline identified;
- measured evidence available;
- throughput findings;
- latency and tail-latency findings;
- CPU findings;
- allocation/GC findings;
- memory/resource-growth findings;
- buffer/pool ownership findings;
- copying/data-movement findings;
- synchronization/contention findings;
- async/task findings;
- queue/backpressure findings;
- network/socket findings;
- logging/diagnostic findings;
- scalability findings;
- startup/shutdown findings where relevant;
- benchmark-harness versus production-path distinctions;
- real-endpoint versus synthetic-result distinctions;
- issues classified by severity;
- proposed experiments where measurement is required;
- changes made, if explicitly requested;
- build result, if validation was performed;
- test result, if tests were run;
- benchmark result, if benchmarks were run;
- confirmation that correctness, cancellation, settlement, disposal, and shutdown semantics were preserved.
