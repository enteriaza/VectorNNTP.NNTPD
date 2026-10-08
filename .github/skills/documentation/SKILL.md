---
name: documentation
description: Add and improve accurate XML documentation in the VectorNNTP.NNTPD C# codebase without changing behavior.
---

# Documentation Skill

## Inspect before writing

Read the complete target file before writing any documentation.

Then inspect partial siblings, interfaces/base types, callers, consumers, tests, protocol/configuration documentation, and relevant history where needed. Search actual repository usage.

Establish lifecycle, ownership, cancellation, threading, state, error, logging, I/O, buffering, allocation, and performance assumptions before writing prose.

Do not begin writing documentation until the implementation and relevant usage have been understood.

Do not infer semantics from names, parameter names, comments, or log-message wording alone. Verify important claims against the implementation and repository usage.

Do not modify files other than the explicitly selected documentation target.

## Identify meaningful symbols

Review types, constructors, methods, properties, fields, constants, events, operators, indexers, nested types, enum members, test fixtures, and helpers at every accessibility level.

Document symbols whose contract, invariant, state transition, mapping, resource lifetime, concurrency rule, validation rule, failure behavior, framework integration, or verified performance characteristic would help an engineer understand or safely maintain the code.

Public, protected, and internal symbols should be meaningfully documented where appropriate.

Private helpers and tests should be documented when they contain non-obvious engineering intent.

Do not manufacture comments for obvious private implementation details.

Do not document a symbol merely because it is technically documentable or because doing so increases documentation coverage.

## Write accurate engineering-contract XML

The goal is useful engineering-contract documentation, not XML tag counting or warning suppression.

Use concise <summary> text and accurate <param>, <returns>, <value>, and <typeparam> elements.

Use <remarks> for substantive context such as architectural intent, lifecycle, invariants, concurrency, cancellation, resource ownership, validation rules, failure behavior, or other information that materially helps maintainers.

Use <exception> only when the documented member establishes a meaningful thrown-exception contract.

Distinguish carefully between:

* exceptions thrown by a method;
* exception parameters supplied to a method;
* exceptions caught internally;
* exceptions logged or otherwise recorded;
* failures represented through return values or result objects.

Use <see cref>, <seealso>, and <c> when references genuinely clarify the contract. Do not add references merely for decoration.

For generated logging or other source-generated/framework declarations:

* document the semantic behavior established by the declaration;
* document applicable severity where it is actually established;
* document meaningful structured properties when they form part of the observable diagnostic contract;
* do not document speculative compiler/source-generator internals;
* do not imply that a log is always emitted when filtering can suppress it.

Document concurrency, cancellation, disposal, side effects, protocol/configuration constraints, and performance characteristics only when supported by implementation, verified repository usage, or reliable project evidence.

Never invent behavior, guarantees, external stability, performance characteristics, or exception contracts.

## Preserve scope and existing quality

Preserve accurate and useful existing documentation.

If existing documentation is vague, incomplete, misleading, or technically incorrect, improve it rather than deleting it.

Do not rewrite good documentation merely for stylistic preference.

Documentation passes are documentation-only and scope-limited:

* add missing documentation where it provides meaningful engineering value;
* improve existing documentation where it is inaccurate or insufficient;
* modify only the selected target file;
* do not alter production behavior;
* do not alter API design or signatures;
* do not alter source-generation behavior;
* do not alter logging behavior;
* do not alter configuration;
* do not alter analyzer settings;
* do not modify tests or related source files.

Never add boilerplate, warning suppressions, NoWarn, exclusions, or placeholder text.

If a code, architecture, logging, performance, or design issue is discovered, leave it untouched and report it separately.

## XML correctness

Ensure:

* <summary> is used for primary descriptions;
* <param> refers only to actual parameters;
* <returns> is used only for methods/functions that return values;
* <value> is used for properties/indexers;
* <typeparam> refers only to actual generic parameters;
* <exception> describes actual documented exception contracts;
* <remarks> contains substantive additional context;
* <seealso> is used only where genuinely useful;
* XML is well-formed;
* tags are not duplicated;
* references point to real members/types/parameters;
* prose is grammatically complete;
* documentation does not contradict the implementation.

Valid XML syntax alone is not sufficient. Documentation must also be accurate and useful.

## Validate and report

After editing:

1. Confirm that only the selected target file was modified.
2. Inspect the final diff carefully.
3. Confirm that no executable behavior changed.
4. Confirm that no unrelated formatting or refactoring occurred.
5. Confirm that useful existing documentation was not unnecessarily removed.
6. Validate XML documentation and references.
7. Build only the target production project.
8. Do not run tests unless explicitly requested.

The build must succeed.

Report:

* total members reviewed;
* members newly documented;
* existing members whose documentation was meaningfully improved;
* documentation changes by tag:
  * <summary>
  * <param>
  * <returns>
  * <exception>
  * <remarks>
  * <value>
  * <seealso>
* documentation intentionally left unchanged and why;
* pre-existing code/design/logging/performance concerns discovered but intentionally left untouched;
* build result;
* confirmation that tests were not run;
* confirmation that only the selected target file was modified;
* confirmation that production behavior was unchanged.
