---
mode: agent
description: Perform the canonical, additive, single-file XML documentation pass for VectorNNTP.NNTPD.
---

# Canonical VectorNNTP.NNTPD documentation-only pass

Documentation-only pass for:

`<TARGET_FILE>`

IMPORTANT: Execute this entire task continuously from start to finish. Do not stop between individual steps, edits, or files. The ONLY file you may modify is `<TARGET_FILE>`. Stop only for a genuine blocking issue, compilation failure, or another condition that requires my input.

## Objective

Document `<TARGET_FILE>` to the engineering-contract standard established by completed VectorNNTP.NNTPD documentation passes.

This is not a tag-counting exercise.

Documentation must help an engineer understand the code's:

- responsibility;
- observable behavior;
- relationships;
- contracts;
- constraints;
- side effects;
- failure behavior;
- concurrency;
- lifecycle;
- resource ownership;
- framework integration;
- material performance implications.

Base every claim on the actual implementation and verified repository usage.

## Read the entire file first

Before editing:

1. Read the entire target file from beginning to end.
2. Understand every type, partial type, member, existing XML comment, attribute, generator declaration, and helper.
3. Search relevant repository call sites and inspect related types and implementations.
4. Verify actual inputs, outputs, state changes, exceptions, cancellation, concurrency, resource ownership, I/O, buffering, logging, allocation, and performance behavior where relevant.
5. Understand framework-, compiler-, dependency-injection-, and source-generator-consumed contracts.
6. Inspect `.copilot/PERFORMANCE-CONTEXT.md` when the target participates in a performance-sensitive path or the documentation requires verified performance context.
7. Do not infer semantics from names, parameter names, comments, or log-message wording alone.
8. Do not begin writing documentation until the implementation and relevant usage have been understood.
9. Do not modify any other file.

## Documentation standard

For each type or partial type, write a precise `<summary>` and use `<remarks>` for useful architectural context, invariants, constraints, lifecycle, concurrency, or other substantive information.

Avoid generic wording such as:

- “Provides functionality for”;
- “Handles”;
- “Contains”;
- “Provides methods for”;
- “Represents”.

Use such wording only when expanded into an actual engineering contract.

For every public, protected, internal, or otherwise meaningful method:

- Explain what it actually does.
- Explain meaningful parameters.
- Explain return values where applicable.
- Explain meaningful side effects.
- Explain state changes.
- Explain failure behavior.
- Explain asynchronous, cancellation, and thread-safety behavior where relevant.
- Explain material performance behavior where supported by evidence.
- Add `<param>` and `<returns>` only when applicable.
- Add private documentation only when it captures a non-obvious architectural decision, algorithm, invariant, state transition, error rule, validation rule, resource/lifetime rule, representation mapping, or framework contract.
- Do not invent behavior.
- Do not mechanically repeat documentation that is already accurate and sufficient.

For properties:

- Use `<value>` rather than `<returns>`.
- Document useful meaning, state, lifecycle, mutability, ownership, or other non-obvious semantics.

Document constants and fields when their meaning is part of an important contract.

Document enum types according to their actual purpose.

Document enum members when their meaning is non-obvious or materially useful to an engineer.

Do not invent numeric stability guarantees for enum values.

Use `<exception>` only for an established exception contract.

Distinguish carefully between:

- exceptions thrown by a method;
- exception values passed into a method;
- exceptions logged by a method;
- exceptions inspected by a method;
- exceptions caught internally;
- failures represented through return values or result objects.

For generated or framework-integrated declarations:

- document the semantic contract exposed to callers;
- document framework-relevant behavior when it materially affects maintainers;
- do not document speculative compiler or source-generator implementation details.

For generated logging:

- document declared severity only when established by the declaration;
- document meaningful structured properties only when they are observable and established by the declaration;
- document event identifiers only when their meaning is established by the implementation or repository conventions;
- do not imply that a log entry is always emitted when filtering may suppress it;
- do not imply that an exception parameter is thrown when it is merely recorded.

Use valid XML documentation:

- `<summary>` for primary descriptions;
- `<param>` only for actual parameters;
- `<returns>` only for methods/functions that return values;
- `<value>` for properties and indexers;
- `<exception>` only for established exception contracts;
- `<remarks>` for substantive additional context;
- `<typeparam>` only for actual generic parameters;
- `<seealso>` only where genuinely useful.

Avoid:

- duplicate tags;
- malformed XML;
- invalid property `<returns>`;
- broken `<see cref>` or `<seealso>` references;
- references to nonexistent members;
- incomplete prose;
- boilerplate;
- invented guarantees;
- documentation that contradicts implementation.

Avoid boilerplate such as:

- “Gets or sets”;
- “Performs the operation”;
- “Provides access to”;
- “Implements the contract”;
- “Logs the error”;

unless expanded enough to communicate the actual engineering semantics.

Valid XML syntax alone is not sufficient.

The documentation must also be accurate, useful, and consistent with the implementation.

## Preserve good documentation

Preserve accurate, useful existing XML documentation verbatim.

If existing documentation is:

- vague;
- incomplete;
- misleading;
- technically incorrect;

improve it rather than deleting it.

Do not rewrite good documentation merely for stylistic preference.

Do not add documentation merely to increase documentation coverage.

Do not add boilerplate to technically documentable symbols that have no meaningful engineering information to communicate.

Do not perform unrelated documentation cleanup elsewhere in the repository.

## Documentation-only restriction

This is strictly a documentation-only task.

Do not:

- refactor;
- rename anything;
- change signatures;
- change accessibility;
- change types;
- change constants;
- change enum values;
- change event IDs;
- change log levels;
- change message templates;
- change structured logging fields;
- change attributes;
- change source-generation behavior;
- change exception handling;
- change filtering;
- change logging calls;
- change validation;
- change algorithms;
- change configuration;
- change analyzer settings;
- change tests;
- change benchmark code;
- modify related source files.

Do not "fix" discovered production, test, logging, architecture, performance, or design issues as part of this documentation pass.

If an issue is discovered:

1. Leave it untouched.
2. Record it for the final report.
3. Distinguish it clearly from documentation changes.

The only file permitted to change is:

`<TARGET_FILE>`

## Validation

After editing:

1. Verify that only `<TARGET_FILE>` was modified by this pass.
2. Inspect the complete final diff.
3. Confirm that no executable/source behavior changed.
4. Confirm that no unrelated formatting or refactoring occurred.
5. Confirm that useful existing documentation was not unnecessarily removed.
6. Validate XML documentation and references.
7. Build only the production project containing `<TARGET_FILE>`.
8. Do not build the entire solution unless explicitly required to diagnose a blocking issue.
9. Do not run tests unless explicitly requested.
10. Confirm correct use of property `<value>`, method `<returns>`, generic `<typeparam>`, parameter `<param>`, and meaningful `<exception>` contracts.
11. Confirm that no source-generation declaration, attribute, event ID, log level, message template, or structured logging field changed.
12. Confirm that no production behavior changed.

The target production-project build must succeed.

Do not suppress warnings or weaken analyzer configuration to make the documentation pass succeed.

If the build exposes a pre-existing warning unrelated to the documentation change, leave it untouched and report it separately.

## Final report

Report:

- Total members reviewed.
- Members newly documented.
- Members whose documentation was meaningfully improved.
- Changes by tag:
  - `<summary>`
  - `<param>`
  - `<returns>`
  - `<exception>`
  - `<remarks>`
  - `<value>`
  - `<seealso>`
- Documentation intentionally left unchanged and why.
- Pre-existing logging/design/code-quality/performance concerns intentionally left untouched.
- Build result.
- Confirmation that tests were not run.
- Confirmation that only `<TARGET_FILE>` was modified.
- Confirmation that no production behavior changed.
- Confirmation that no source-generation behavior changed.
- Confirmation that no logging event IDs, levels, templates, or structured fields changed.

## Reusability

This prompt is intended for repeated use against individual C# files.

Replace `<TARGET_FILE>` with the selected file.

Adapt repository and call-site inspection to the target file, while retaining the complete documentation-only restriction, single-file scope, production-project build, no-tests-by-default rule, and final reporting requirements.

Do not weaken the workflow because a target appears simple.

Do not skip repository usage inspection merely because the file is small.

Follow `.github/instructions/documentation.instructions.md` for permanent documentation standards.

Follow `.github/skills/documentation/SKILL.md` for detailed documentation methodology.

Treat those instructions and the skill as complementary:

- this prompt defines the concrete single-file execution workflow;
- the instructions define the durable repository-wide documentation standards;
- the skill defines the detailed methodology for inspecting and documenting code.
