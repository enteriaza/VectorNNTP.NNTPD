---
name: documentation
description: >-
  Adds or improves accurate engineering-contract XML and Markdown documentation for
  VectorNNTP without changing behavior. Use for documentation-only passes, XML docs,
  README/architecture updates, or when the user asks to document a type or file.
  XML documentation is required for every documentable C# symbol whose contract
  matters, including private, internal, and nested declarations.
---

# Documentation

## Purpose

Write useful engineering-contract documentation grounded in real behavior.

Every C# symbol that can legally carry XML documentation and whose behavior or contract is relevant to understanding the code must have an accurate, useful XML documentation comment — regardless of accessibility.

This is not a request for useless comments on every trivial implementation detail. Every documentable symbol receives a relevant comment. The content stays useful and proportional to the symbol.

## When to use

- Documentation-only tasks
- XML docs for types and members
- Updating architecture or configuration docs to match implementation

## Mandatory workflow

1. Read the **complete** target file and relevant usages or tests before writing.
2. Establish lifecycle, ownership, cancellation, threading, errors, and side effects from code.
3. Document only claims supported by implementation or verified repo evidence.
4. Preserve accurate existing docs. Improve docs that are vague, incomplete, misleading, stale, contradictory, or that merely restate the identifier. Do not rewrite correct documentation for stylistic preference.
5. Keep scope to the agreed target(s). Do not change unrelated files.
6. Validate well-formed XML. Use a tag only when it carries contract information.
7. Build the relevant production project. **Do not run tests** unless requested.
8. Report scope, documentation counts, and the build result.

## Accessibility is not a reason to omit documentation

`private` does not mean "no documentation". `internal` does not mean "no documentation".

Document, where the declaration can carry XML documentation:

- public, internal, protected, protected-internal, private, and file-local types and members
- private nested types and their members
- constructors, methods, properties, fields, events, delegates, and indexers
- operators and conversion operators
- enums and enum members
- interfaces and interface members
- records and record members, including parameters of positional records
- generic type parameters and method type parameters
- compiler-visible members of internal records and types when a source declaration exists for them
- test-only production seams that remain in a production project

Accessibility reduction is not a reason to remove useful documentation.

Skip only symbols that cannot legally carry XML documentation, such as local functions only when the chosen tooling cannot attach a comment, compiler-generated members with no source declaration, and statements. Say which symbols were skipped and why.

## What the comment must say

Describe the actual engineering contract. Do not merely restate the identifier.

For each symbol, document whatever is relevant and evidenced by the implementation:

- purpose and responsibility
- inputs and outputs
- state changes
- ownership and lifetime
- concurrency and thread-safety expectations
- cancellation behavior
- validation rules
- failure and error behavior
- exceptions when they are a meaningful part of the contract
- nullability or absence semantics where useful
- resource management
- persistence and durability semantics
- protocol and wire-format behavior
- configuration semantics
- lifecycle relationships
- ordering requirements
- performance characteristics when materially relevant and evidenced
- interaction with other components
- invariants that must remain true
- security-sensitive behavior when applicable

Do not invent guarantees.

For private implementation methods, document the implementation contract where that contract matters to correctness. For a trivial private helper, a concise but meaningful description is enough.

## Required XML elements

Use the tags that match the declaration:

- `<summary>`
- `<param>`
- `<typeparam>`
- `<returns>`
- `<value>`
- `<exception>`
- `<remarks>`
- `<example>`
- `<see>`
- `<seealso>`
- `<inheritdoc />`

Do not add tags for decoration.

Document every parameter and type parameter. Document meaningful return semantics. Document exceptions when the implementation intentionally throws them as part of the contract. Use `<remarks>` for invariants, lifecycle, concurrency, or other detail that would make the summary unwieldy. Use `<see cref="..."/>` when the cross-reference materially helps navigation. `cref` targets must resolve.

Use `<inheritdoc />` only when the base or interface contract is genuinely the correct documentation for that declaration. Do not use it to avoid reading the implementation. A member that adds behavior, failure modes, or lifetime rules beyond the inherited contract needs its own text.

## Documentation-only constraint

Do not change runtime behavior, algorithms, accessibility, signatures, configuration, logging, threading, exception behavior, or protocol behavior. Do not edit tests to accommodate documentation. Do not suppress documentation warnings with `NoWarn`.

If documentation exposes a genuine design defect or ambiguity, report it. Do not silently change implementation behavior.

## Avoid

- Paraphrasing the member name as the entire summary
- Invented exception, performance, or thread-safety claims
- Changing executable behavior, signatures, logging, or configuration in a docs-only task
- Suppressions or `NoWarn` to silence documentation warnings
- Copying personal contact headers from other repos unless this repo already requires them
- Markdown files as a substitute for symbol-level XML documentation

## Report

- Members reviewed, newly documented, improved, and left undocumented
- Declarations that use `<inheritdoc />`
- Declarations that cannot technically accept XML documentation, with the reason
- Tags touched
- Pre-existing design issues found but left untouched
- Build result; confirmation tests were not run when the task is docs-only
- Confirmation that behavior, signatures, accessibility, and configuration were unchanged

## Validation checklist

- [ ] Target file fully read; usages inspected where the contract depends on them
- [ ] Every documentable declaration has accurate XML documentation
- [ ] Claims verified against code; no invented contracts
- [ ] XML well-formed; parameters, type parameters, and meaningful returns documented
- [ ] Scope limited; no behavior, signature, or accessibility change
- [ ] Production project build succeeded with documentation generation and no documentation warnings
