---
name: documentation
description: >-
  Adds or improves accurate engineering-contract XML and Markdown documentation for
  VectorNNTP without changing behavior. Use for documentation-only passes, XML docs,
  README/architecture updates, or when the user asks to document a type or file.
---

# Documentation

## Purpose

Write useful engineering-contract documentation grounded in real behavior.

## When to use

- Documentation-only tasks
- XML docs for types/members
- Updating architecture/config docs to match implementation

## Mandatory workflow

1. Read the **complete** target file and relevant usages/tests before writing.
2. Establish lifecycle, ownership, cancellation, threading, errors, and side effects from code.
3. Document only claims supported by implementation or verified repo evidence.
4. Preserve accurate existing docs; improve vague/wrong docs; do not restyle for preference.
5. Keep scope to the agreed target(s); do not “drive-by” unrelated files.
6. Validate well-formed XML (`<summary>`, `<param>`, `<returns>`, `<value>`, `<exception>`, `<remarks>` only when applicable).
7. Build the relevant production project; **do not run tests** unless requested.
8. Report scope and build result.

## What to document

Document symbols when the contract helps maintainers: invariants, ownership, state transitions, cancellation, validation, failure modes, framework integration, performance constraints **when evidenced**.

Skip obvious private noise. Do not invent guarantees.

## Avoid

- Paraphrasing the member name as the entire summary
- Invented exception/performance/thread-safety claims
- Changing executable behavior, signatures, logging, or config in a docs-only task
- Suppressions / `NoWarn` to silence doc warnings
- Copying personal contact headers from other repos unless this repo already requires them

## Report

- Members reviewed / newly documented / improved
- Tags touched
- Intentionally unchanged items and why
- Pre-existing design issues found but left untouched
- Build result; confirmation tests were not run (if docs-only)
- Confirmation behavior unchanged

## Validation checklist

- [ ] Target file fully read; usages inspected
- [ ] Claims verified against code
- [ ] XML well-formed; no invented contracts
- [ ] Scope limited; no behavior change
- [ ] Production project build succeeded
