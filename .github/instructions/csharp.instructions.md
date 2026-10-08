---
applyTo: "**/*.cs"
---

# C# project conventions

- Target `net10.0` with C# 13, nullable enabled, implicit usings enabled, and x64 platform settings. Use explicit access modifiers and follow the naming, formatting, and style rules in `CONTRIBUTING.md` and `.editorconfig`.
- Preserve the repository's file-specific header format and required attribution. During targeted changes, do not modify headers in unrelated files.
- Treat documentation as an engineering contract. Add meaningful XML documentation for public, protected, internal, and other symbols where it communicates useful engineering information. Document private helpers and test symbols when they contain non-obvious contracts, invariants, lifecycle rules, ownership rules, state transitions, validation rules, failure behavior, or framework intent. Do not add documentation merely to increase coverage or satisfy warning counts.
- Keep startup bind, validate, normalize, and canonical-snapshot logic authoritative. After validation, use the immutable validated runtime snapshot rather than rebinding or independently interpreting raw configuration in later services.
- Preserve explicit lifecycle and readiness ordering. Do not infer readiness from DI registration order or service construction order.
- Prefer asynchronous I/O and explicit cancellation and disposal ownership. Never introduce blocking waits, unobserved fire-and-forget tasks, arbitrary sleeps, polling-based synchronization, or timing hacks in concurrency and lifecycle code.
- Preserve exactly-once work settlement, bounded admission, backpressure, ACK/NACK semantics, retry behavior, ownership transfer, cancellation behavior, and shutdown-drain guarantees.
- Treat provider, protocol, network, configuration, database, DNS, certificate, and other external inputs as failure-prone or untrusted. Validate boundaries and preserve safe failure behavior.
- Make resource ownership explicit for sockets, streams, connections, buffers, pooled memory, timers, cancellation registrations, tasks, and other disposable resources. Avoid use-after-disposal, use-after-returned-buffer, double disposal, double-return, and resource leaks.
- Prefer existing abstractions, patterns, and lifecycle mechanisms. Do not introduce new interfaces, wrappers, DI indirection, or abstractions solely to make straightforward code easier to test or mock.
- Keep hot paths allocation-conscious. Avoid unnecessary allocations, copies, boxing, closures, LINQ, temporary collections, task creation, synchronization, lock contention, and retained large buffers. Do not introduce speculative micro-optimizations without credible workload evidence.
- Use source-generated structured logging where established by the repository. Preserve event IDs, levels, templates, and structured property names unless the change explicitly requires otherwise. Never log credentials, secrets, or article payloads.
- Keep machine-facing formatting UTC and culture-invariant. Preserve structured correlation fields and diagnostic contracts where they are established by the implementation.
- Builds must remain analyzer-, style-, nullable-, and documentation-warning free for the affected scope. Fix diagnostics in source; do not add suppressions, `NoWarn`, exclusions, disabled analyzers, or equivalent mechanisms to hide them.
- Preserve existing behavior, API contracts, lifecycle guarantees, and regression coverage unless a behavioral change is explicitly intended. When behavior changes, add or update meaningful regression protection for the actual contract or failure mode.
