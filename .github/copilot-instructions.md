# VectorNNTP.NNTPD Copilot Instructions

## Project

VectorNNTP.NNTPD is a .NET 10, C# 13 worker service that retrieves unavailable Usenet articles from external NNTP providers, validates them, and returns recovered work through the VectorNNTP pipeline. It is high-throughput, highly concurrent networking infrastructure; correctness, bounded resources, and graceful shutdown are essential.

The solution is `VectorNNTP.NNTPD.sln` and contains:

- `src/VectorNNTP.NNTPD/`: production worker, startup/configuration validation, certificates, RabbitMQ, NNTP transit/listener, article parsing and processing.
- `tests/VectorNNTP.NNTPD.Tests/`: xUnit unit, integration-style, lifecycle, protocol, validation, and regression tests.
- `tools/VectorNNTP.NNTPD.Bench/`: BenchmarkDotNet and controlled transit performance harnesses.
- `tests/VectorNNTP.NNTPD.Bench.Tests/`: benchmark contract and infrastructure tests.
- `tools/`: benchmark and auxiliary tooling projects.
- `.github/workflows/`: build, coverage, dependency, and CodeQL validation.
- `.copilot/PERFORMANCE-CONTEXT.md`: existing performance checkpoint; preserve it and treat source, history, tests, and artifacts as stronger evidence.

## Runtime and architecture

- Target `net10.0`, x64 only, nullable enabled, implicit usings enabled, and C# 13. Release publishing is self-contained Native AOT for `win-x64` and `linux-x64`.
- Startup must be ordered: bind configuration, validate it, create one immutable canonical runtime snapshot, validate directories, validate dependencies, then construct/start DI and hosted services. Do not perform expensive, irreversible, externally visible work before validation.
- Do not infer readiness from service-registration order. Enforce readiness through explicit dependencies/orchestration; `ServiceLifecycle.Ready` and systemd `READY=1` represent the same operational milestone.
- Every admitted unit of work must be settled exactly once on success, failure, cancellation, timeout, and shutdown. Preserve ownership, ACK/NACK, retry, backpressure, and drain invariants.
- Treat external NNTP, RabbitMQ, DNS, Cloudflare, ACME, and database input as untrusted or failure-prone. Preserve cancellation and disposal ownership across asynchronous boundaries.
- Cloudflare validation is required independently of the optional Let's Encrypt issuance flow. ACME-only settings are ignored when issuance is disabled.
- Listener wildcard semantics are explicit: empty address maps to independent `0.0.0.0` and `::` endpoints; `*`, `Any`, `0.0.0.0`, and `::` are wildcard tokens, not published DNS addresses. Configure IPv6-only behavior explicitly and derive wildcard DNS addresses from eligible local interfaces.
- Use UTC and culture-invariant machine-facing formatting. Article logs include structured `MessageId`; protocol TX/RX debug logs exclude payloads and credentials; article outcomes include `MessageId`, `Outcome`, and monotonic elapsed `Duration`.

## Build and test

From the repository root:

```text
dotnet restore VectorNNTP.NNTPD.sln
dotnet build VectorNNTP.NNTPD.sln --configuration Release -p:Platform=x64
dotnet test tests/VectorNNTP.NNTPD.Tests/VectorNNTP.NNTPD.Tests.csproj --configuration Release -p:Platform=x64
dotnet test tests/VectorNNTP.NNTPD.Bench.Tests/VectorNNTP.NNTPD.Bench.Tests.csproj --configuration Release -p:Platform=x64
```

Use the repository's isolated regression/watchdog tooling for potentially blocking lifecycle or concurrency tests. Do not use `--no-build` for benchmark validation: clean, build, verify runtime identity, then run matching Debug/x64/net10.0/win-x64 settings. Preserve CI diagnostics and existing baselines.

Builds enable .NET analyzers, code style, and generated documentation. Completion requires zero errors and warnings attributable to the change; fix source/design issues rather than suppressing diagnostics. Do not weaken, skip, or remove tests.

## Engineering conventions

- Follow `CONTRIBUTING.md` and `.editorconfig`: explicit access modifiers, `_camelCase` private fields, PascalCase public members/constants, camelCase locals/parameters, x64 platform, and a 160-character practical line limit.
- Prefer simple designs and existing abstractions. Do not add DI seams solely to mock straightforward startup logic.
- Prefer async I/O, `ConfigureAwait(false)` where appropriate, deterministic coordination over sleeps, and explicit cancellation. Avoid blocking waits, fire-and-forget work with unobserved failures, unnecessary LINQ/allocations/copies/boxing, and contention in hot paths.
- Performance changes require a baseline, one measurable change at a time, stable configuration, and evidence covering throughput, latency/tails, CPU, allocations/GC, memory, queues, and connections. Do not optimize speculative theory.
- Use structured source-generated logging where the existing project pattern applies; guard expensive disabled-level work and never log secrets.
- Treat documentation as useful engineering-contract guidance, not XML tag counting: meaningfully document public, protected, and internal symbols where appropriate, and document private helpers or tests when they contain non-obvious intent such as behavior, contracts, invariants, lifecycle, concurrency, validation, performance, or logging. Do not blindly document private helpers or add boilerplate merely to satisfy warning or coverage counts; preserve accurate useful documentation, improve vague, incomplete, misleading, or technically incorrect documentation, and keep documentation passes behavior-preserving. Valid XML syntax alone is not sufficient. Every `.cs` file needs the repository header and required attribution: `Copyright © Chris Knipe cknipe@opticnetworks.net`.
- Keep behavior/API changes narrowly scoped, add regression coverage for behavior changes, and update related documentation/history when an architectural decision or measured result changes.
- Do not create temporary, baseline, snapshot, diagnostic, generated, or other working artifacts at repository root. Place generated/working artifacts under the repository `artifacts/` directory using a producer/type subdirectory (for example, baseline source snapshots go under `artifacts/baselines/`, not root-level `_baseline_*.cs`). Do not rely on `.gitignore` to hide misplaced artifacts; write them to the correct location. Before creating an artifact, explicitly choose its output directory instead of relying on the current working directory. Preserve existing producer-specific artifact contracts where intentionally documented.

## Additional Instructions

- Follow repository instruction files for C#/.NET, documentation, and performance changes: `.github/instructions/csharp.instructions.md`, `documentation.instructions.md`, and `performance.instructions.md`.
- Follow repository instruction files for testing, performance, documentation, and C# conventions in VectorNNTP.NNTPD, including deterministic concurrency testing discipline, isolated regression gate usage for hung tests, and no weakening/skipping tests.
