---
name: performance-engineering
description: >-
  Engineers and reviews high-performance .NET networking code for VectorNNTP with
  evidence-based measurement of allocations, throughput, latency, and resource
  lifetime. Use when optimizing hot paths, profiling, benchmarking, reducing
  allocations, or reviewing performance-sensitive networking changes.
---

# Performance engineering

## Purpose

Improve real workload performance without sacrificing correctness or operational safety.

## When to use

- Hot-path / networking / buffering changes
- Allocation or GC concerns under load
- Benchmark or profiling tasks
- Performance reviews of diffs

## Mandatory workflow

1. State the **actual objective and workload** (what is being optimized, for whom).
2. Inspect the full data-flow hot path before changing it.
3. Establish a reproducible baseline when measurements exist or can be run.
4. Change one meaningful factor at a time where practical.
5. Preserve protocol correctness, lifecycle, cancellation, disposal, and backpressure.
6. Distinguish **measured results** from **hypotheses**.
7. Report methodology, workload, environment, and numbers actually obtained.

## Consider

- Allocations, copies, boxing, closures, LINQ, temporary collections
- Buffering, pooling ownership, LOH, retained growth vs transient pressure
- Backpressure and bounded queues — do not remove them to inflate throughput
- Async overhead, task fan-out, lock contention, syscalls
- Throughput, latency (median + tails), CPU per work unit, memory, error rate under sustained load
- Network attribution: app CPU vs TCP/TLS vs remote limits vs RTT

## Project constraints

- NNTPD targets sustained high throughput; **do not claim** 40+ Gbps (or any target) without evidence from this environment.
- Phase 0 host/lifecycle/logging must stay off the future packet hot path where architecture requires it (`docs/architecture.md`).
- No established allocation budget in-repo ⇒ do not invent one; measure first.
- Do not import BackFiller benchmark baselines or RabbitMQ settlement rules.

## Avoid

- Speculative micro-optimizations without path/frequency evidence
- Unbounded queues, buffers, tasks, or connection creation
- Unsafe pooling (use-after-return, double-return)
- Blocking waits on high-concurrency paths
- Optimizing production to satisfy an unrealistic harness
- Trading correctness for a prettier number

## Report

- Objective and workload
- Baseline vs after (or “not measured”)
- Correctness/lifecycle impact
- Residual risks and next measurement steps

## Validation checklist

- [ ] Objective/workload stated
- [ ] Hot path understood
- [ ] Hypotheses labeled vs measured
- [ ] Correctness and shutdown preserved
- [ ] No unverified throughput claims
