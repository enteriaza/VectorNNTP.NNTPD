---
applyTo: "**/*.{cs,md}"
---

# Documentation standards

- Every `.cs` file must start with the repository-required header containing the file's specific purpose/responsibility and `Copyright © Chris Knipe cknipe@opticnetworks.net`. During a documentation pass, do not modify headers in files outside the explicitly selected target file.
- Documentation is an engineering contract, not tag counting or warning suppression. Base claims on the actual implementation and verified repository usage, not names, parameter names, comments, or assumptions.
- Provide meaningful XML documentation for documentable types and members at every accessibility level where the documentation communicates useful engineering information. Include tests and private helpers when they communicate a non-obvious contract, invariant, lifecycle rule, ownership rule, state transition, validation rule, error rule, framework contract, or other engineering intent.
- Explain purpose, observable behavior, invariants, parameters, returns, exceptions, lifecycle, ownership, threading, cancellation, performance, logging, I/O, and side effects where applicable and supported by the implementation or verified repository evidence.
- Use `<summary>` for primary descriptions, `<param>` for actual parameters, `<returns>` only for returned values, `<value>` for properties and indexers, `<exception>` only for established thrown-exception contracts, and `<remarks>`, `<typeparam>`, and `<seealso>` only when substantively applicable.
- Preserve existing accurate and useful XML documentation. Improve documentation when it is vague, incomplete, misleading, or technically incorrect rather than deleting it. Do not rewrite good documentation merely for stylistic preference.
- Documentation changes must remain scope-limited to the explicitly selected target file unless the user explicitly requests a broader documentation change. Do not modify unrelated source files merely to improve documentation consistency.
- Treat source-generated and framework-integrated declarations according to their actual semantic contract. Distinguish thrown exceptions from exception parameters, logged exceptions, inspected exceptions, caught exceptions, and failures represented through results or return values.
- For generated logging, document declared severity, event semantics, and meaningful structured properties only when established by the declaration or repository conventions. Do not imply that logs are always emitted when filtering can suppress them.
- Ensure XML is well-formed, tags are not duplicated, references point to real members/types/parameters, parameter names exist, generic parameters exist, property documentation does not use `<returns>`, and prose does not contradict or invent behavior.
- Use `<see cref>`, `<seealso>`, and `<c>` when they materially improve understanding. Do not add references merely for decoration.
- Avoid boilerplate and meaningless private documentation. Do not document a symbol merely because it is technically documentable or because doing so increases documentation coverage.
- Do not alter behavior, API design, namespaces, signatures, accessibility, tests, source-generation declarations, logging behavior, configuration, analyzer settings, or production logic merely to improve documentation.
- Never hide documentation diagnostics with suppressions, `NoWarn`, exclusions, or analyzer configuration changes. Resolve underlying documentation issues where possible.
- Documentation-only passes build the target production project and do not run tests unless explicitly requested.
- When reviewing documentation-sensitive changes, confirm that no executable behavior changed and that useful existing documentation was not unnecessarily removed.
- Follow established terminology and formatting in `CONTRIBUTING.md` and `.editorconfig`.
- The reusable single-file documentation workflow is `.github/prompts/documentation.prompt.md`.
- Detailed documentation methodology is `.github/skills/documentation/SKILL.md`.
