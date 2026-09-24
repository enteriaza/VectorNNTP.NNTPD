---
name: code-review
description: >-
  Reviews VectorNNTP/.NET changes for correctness, concurrency, resource lifetime,
  cancellation, shutdown, security, performance, and architecture consistency with
  evidence-backed findings. Use when reviewing pull requests, diffs, or when the
  user asks for a code review. Does not automatically modify code on review-only tasks.
---

# Code review

## Purpose

Review changed code as a senior .NET systems engineer. Prefer defects and operational risks over style.

## When to use

- User asks for a review, PR review, or “look at this change”
- Diff may affect lifecycle, concurrency, networking, config, or protocol

## Mandatory workflow

1. Identify every changed file; read **complete** changed files (not only the hunk).
2. Inspect callers, consumers, interfaces, ownership, lifecycle, and failure paths.
3. Inspect existing tests covering the behavior.
4. Compare against documented architecture (`docs/architecture.md`) and current contracts.
5. Classify each finding as **Defect**, **Risk**, or **Improvement**.
6. Report with severity, location, impact, evidence, and practical remediation.
7. Do **not** auto-edit on review-only tasks unless the user asked for fixes.

## Priority order

1. Correctness  
2. Data / session / connection integrity  
3. Concurrency and thread safety  
4. Resource lifetime and disposal  
5. Cancellation and shutdown  
6. Reliability and failure handling  
7. Security / trust boundaries  
8. Performance (material impact only)  
9. Observability  
10. Maintainability  

## Project constraints

- Target project may be `VectorNNTP.NNTPD` — do not import BackFiller RabbitMQ / ACK-NACK settlement invariants unless this repo actually has that contract.
- Lifecycle truth lives in `ApplicationLifecycle`, `ApplicationServiceManager`, and systemd helpers — verify against code, not memory.
- Protocol claims require `docs/standards/rfcs/` (see `nntp-protocol` skill).
- Protocol **representation** is byte-oriented (`docs/architecture.md` § Byte-Oriented Protocol Data Plane). A silent `bytes → string → bytes` conversion is a defect unless an explicit boundary is justified.
- Application logging prefers source-generated structured methods (`docs/architecture.md` § Source-Generated Structured Logging). Preformatted interpolated log messages and extra protocol-byte conversions solely for logging are defects unless the logging API cannot consume the original representation.
- Config claims require `docs/configuration.md` (see `configuration-and-security` skill).

## Protocol data-plane checklist

When the diff touches parse, dispatch, responses, framing, or other protocol bytes:

- [ ] Does protocol data remain bytes?
- [ ] Is there a bytes→string conversion?
- [ ] If yes, what explicit boundary requires it?
- [ ] Is the conversion on a hot path?
- [ ] Does the string immediately become bytes again?
- [ ] Is static protocol text pre-encoded?
- [ ] Are dynamic protocol fields kept byte-oriented where practical?
- [ ] Is there an unnecessary allocation caused by representation conversion?
- [ ] Is the conversion documented/obvious enough that a future reviewer understands why it exists?

“The API currently takes a string” is not automatically sufficient justification.

## Look for

- Incorrect state transitions; partial init; swallowed exceptions; lost background failures
- Races on shared state; double-complete; abandoned tasks; premature dispose
- Blocking `.Wait()` / `.Result` on async paths; fire-and-forget without observation
- Unbounded queues, buffers, task fan-out, or connection growth
- Secret leakage in logs/exceptions/tests
- Tests that cannot fail when the production contract breaks

## Avoid

- Style preferences presented as defects
- Unrelated refactoring recommendations
- Findings without inspecting surrounding code
- Speculative micro-optimizations without evidence

## Report format

For each finding:

```markdown
### [Defect|Risk|Improvement] — short title
- **Severity:** Critical | High | Medium | Low
- **Location:** path + symbol
- **Impact:** what breaks in production
- **Evidence:** what you read / traced
- **Remediation:** smallest practical fix
```

End with: files reviewed, residual unknowns, tests **not** run (unless you ran them).

## Validation checklist

- [ ] Complete changed files read
- [ ] Callers/consumers/lifecycle inspected where relevant
- [ ] Findings classified and evidenced
- [ ] No auto-edit on review-only request
- [ ] No BackFiller-only invariants asserted without repo evidence
