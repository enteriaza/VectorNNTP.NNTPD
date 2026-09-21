---
name: implementation-and-verification
description: >-
  Guides complete VectorNNTP implementation tasks from investigation through
  focused change, tests, build/format/analysis, and evidence-based reporting.
  Use for feature work, bug fixes, or multi-step engineering tasks that should
  run continuously without pausing for routine approvals.
---

# Implementation and verification

## Purpose

Execute a complete engineering task continuously, safely, and with verified results.

## When to use

- Feature implementation or bugfix spanning multiple steps
- User expects inspect → implement → test → report in one pass

## Mandatory workflow

1. **Restate** the task and acceptance criteria.
2. **Inspect** repo state and relevant implementation (`git status` / key files).
3. Identify affected components and dependencies; choose other skills as needed.
4. Inspect applicable RFCs (`nntp-protocol`) and docs when relevant.
5. Plan a **focused** implementation (smallest correct change).
6. Implement the change.
7. Add or update meaningful tests (`testing`).
8. Run relevant verification for this repo, typically:
   - `dotnet build VectorNNTP.NNTPD.sln -c Release`
   - `dotnet test VectorNNTP.NNTPD.sln -c Release --no-build` (or build+test as needed)
   - `dotnet format VectorNNTP.NNTPD.sln --verify-no-changes`
   - Treat warnings as errors / analyzers already enforced by `Directory.Build.props`
9. Inspect the final diff for unintended changes.
10. Report actual commands, results, and remaining limitations.

## Continuity

Execute investigation, implementation, testing, and verification continuously.

**Stop and ask only when:**

- Essential information is missing
- Action is destructive or externally consequential (force push, production DNS, secret rotation without authorization)
- Permissions are unavailable
- A genuine product decision cannot be safely inferred

Do not stop for approval between routine steps.

## Honesty gates

- Never claim completion if a required verification step did not run or failed.
- Never claim RFC conformance, throughput targets, or live integrations without evidence.
- Preserve unrelated user changes.

## Related skills

Load as applicable: `nntp-protocol`, `lifecycle-and-concurrency`, `configuration-and-security`, `performance-engineering`, `testing`, `documentation`, `code-review`.

## Report template

1. Acceptance criteria vs outcome  
2. Files changed  
3. Tests/build/format/analysis — exact results  
4. Unverified assumptions / follow-ups  
5. Confirmation of untouched out-of-scope areas  

## Validation checklist

- [ ] Criteria restated
- [ ] Implementation inspected before editing
- [ ] Relevant skills/RFCs consulted
- [ ] Tests added for real contracts
- [ ] Build/test/format (as applicable) actually run
- [ ] Diff reviewed; report evidence-based
