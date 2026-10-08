---
mode: agent
description: Audit and complete XML documentation across the entire VectorNNTP.NNTPD production project.
---

# VectorNNTP.NNTPD Project Documentation Audit

Perform a complete, project-wide XML documentation audit and remediation pass across the VectorNNTP.NNTPD production project.

This prompt is the PROJECT-LEVEL ORCHESTRATOR.

It is intentionally different from:

.github/prompts/documentation.prompt.md

The reusable Documentation Prompt is a SINGLE-FILE documentation workflow.

This prompt is responsible for auditing the ENTIRE production project and applying the documentation methodology from the reusable Documentation Prompt independently to each production C# file that requires documentation work.

# CRITICAL DISTINCTION FROM THE SINGLE-FILE DOCUMENTATION PROMPT

DO NOT execute `.github/prompts/documentation.prompt.md` as though this task were a single-file task.

The `<TARGET_FILE>` placeholder and the single-file modification restriction defined by that prompt apply to EACH INDIVIDUAL FILE ITERATION performed as part of this project-wide task.

This task itself is NOT restricted to one file.

The project-wide scope is:

src/VectorNNTP.NNTPD/

You must enumerate, audit, and remediate the entire production project.

Use the documentation standards, methodology, XML rules, quality criteria, and documentation-only restrictions established by:

- `.github/prompts/documentation.prompt.md`
- `.github/instructions/documentation.instructions.md`
- `.github/skills/documentation/SKILL.md`

Treat those resources as the canonical methodology for each individual file.

# ABSOLUTE MODIFICATION SCOPE

The ONLY files that may be modified are C# source files under:

src/VectorNNTP.NNTPD/

Do NOT modify:

- tests/VectorNNTP.NNTPD.Tests/
- tests/VectorNNTP.NNTPD.Bench.Tests/
- tools/VectorNNTP.NNTPD.Bench/
- tools/
- `.github/`
- `.copilot/`
- solution files
- project files
- configuration files outside the production project
- analyzer configuration
- test files
- benchmark files
- generated artifacts

Tests and benchmarks may be READ when required to establish production behavior, but MUST NOT be modified.

# PRIMARY OBJECTIVE

The objective is to leave the ENTIRE VectorNNTP.NNTPD production codebase with genuinely useful engineering-contract XML documentation.

This means auditing BOTH:

1. symbols with missing documentation; and
2. symbols that already have XML documentation but whose documentation is inadequate.

Do not equate the presence of XML documentation with adequate documentation.

A grammatically valid `<summary>` can still be inadequate.

A compiler warning-free comment can still be inadequate.

A previous Copilot pass can still have missed an inadequate comment.

# EXECUTE THE ENTIRE TASK CONTINUOUSLY

Execute the complete project-wide audit and remediation continuously from start to finish.

Do NOT stop between:

- files;
- directories;
- namespaces;
- subsystems;
- individual documentation edits.

Do NOT ask for permission to continue.

Only stop for:

- a genuine blocking issue;
- compilation failure requiring user input;
- missing information that cannot reasonably be established from the repository;
- an explicit stop condition.

Do not stop merely because a large portion of the project appears well documented.

Do not stop after documenting the highest-value subsystem.

Do not stop after a convenient number of files.

# PHASE 1 — COMPLETE FILE INVENTORY

First enumerate every C# source file belonging to:

src/VectorNNTP.NNTPD/

Establish the exact current number of production C# files.

Do not assume a previous count is still correct.

The project has previously contained approximately 132–135 C# files.

Report the actual current count.

Do not omit:

- startup code;
- configuration;
- validation;
- lifecycle;
- shutdown;
- RabbitMQ;
- NNTP;
- networking;
- article processing;
- logging;
- certificates;
- utilities;
- extensions;
- infrastructure;
- metadata;
- supporting services;
- small helper classes.

# PHASE 2 — COMPLETE PROJECT AUDIT

Every production C# file must be inspected.

For every file:

1. Read the complete file.
2. Identify all types.
3. Identify all interfaces.
4. Identify records and structs.
5. Identify enums.
6. Identify constructors.
7. Identify methods.
8. Identify properties.
9. Identify meaningful fields.
10. Identify constants.
11. Identify events.
12. Identify operators.
13. Identify indexers.
14. Identify nested types.
15. Identify meaningful private helpers.
16. Inspect all existing XML documentation.
17. Determine whether documentation is actually adequate.

Do not consider a file "reviewed" merely because its primary type was inspected.

# PHASE 3 — DOCUMENTATION QUALITY AUDIT

For every meaningful documentable symbol, determine whether the documentation is:

- adequate;
- missing;
- generic/boilerplate;
- incomplete;
- ambiguous;
- misleading;
- technically incorrect.

Documentation is adequate only when it communicates meaningful engineering information appropriate to the symbol.

Apply this test:

"Would this documentation materially help another engineer understand, use, debug, modify, or safely maintain this symbol?"

If the answer is no, the documentation is inadequate.

# IMPORTANT — PARAPHRASE DOCUMENTATION IS NOT SUFFICIENT

A comment that merely restates the symbol name or implementation action is not sufficient documentation.

For example:

```csharp
/// <summary>
/// Handles run accept loop async for back filler listener socket service.
/// </summary>
private async Task RunAcceptLoopAsync(...)
```

is inadequate.

It merely paraphrases the method name.

The documentation should instead describe the actual verified engineering behavior, including relevant:

- accept-loop responsibility;
- cancellation behavior;
- accepted-socket handling;
- ownership;
- lifecycle;
- failure handling;
- shutdown semantics;
- concurrency;
- admission/backpressure behavior;

ONLY where those semantics are actually established by the implementation.

Other examples of suspicious documentation include:

"Handles the connection."
"Processes the article."
"Gets the connection."
"Initializes the service."
"Creates the consumer."
"Provides functionality for..."
"Stores the value."
"Manages the state."
"Returns whether..."

These phrases are not automatically wrong.

They MUST be evaluated against the actual implementation.

# PHASE 4 — SEARCH FOR WEAK DOCUMENTATION

In addition to reading files normally, actively search the entire production project for documentation likely to be inadequate.

Search for phrases including:

- `Gets or sets`
- `Gets the`
- `Sets the`
- `Provides`
- `Provides functionality`
- `Handles`
- `Creates`
- `Initializes`
- `Processes`
- `Manages`
- `Stores`
- `Represents`
- `Performs`
- `Contains`
- `Returns`
- `Checks`
- `Validates`
- `Executes`
- `Invokes`
- `Runs`
- `Sends`
- `Receives`
- `Connects`
- `Disconnects`

Also search for:

- summaries that only repeat a symbol name;
- summaries containing no meaningful engineering information;
- undocumented meaningful members;
- missing `<param>`;
- incorrect `<param>` names;
- missing `<returns>`;
- incorrect `<returns>`;
- properties using `<returns>` instead of `<value>`;
- missing meaningful `<remarks>`;
- broken `<see cref>`;
- broken `<seealso>`;
- stale references;
- documentation contradicting implementation.

Do not blindly rewrite every search match.

Evaluate each occurrence.

# PHASE 5 — APPLY THE SINGLE-FILE METHODOLOGY

For each production file that requires remediation:

Apply the methodology defined by:

`.github/prompts/documentation.prompt.md`

as though that file were the `<TARGET_FILE>` for that individual iteration.

That means:

- read the complete file;
- understand the implementation;
- inspect relevant repository usage;
- establish actual contracts;
- improve documentation;
- preserve good documentation;
- modify only that file during the individual documentation iteration;
- do not change behavior;
- build the production project after the overall work is complete.

IMPORTANT:

The single-file prompt's scope applies to the individual file currently being processed.

It MUST NOT cause this project-wide task to stop after one file.

After completing one file, continue to the next production file requiring remediation.

# PHASE 6 — PRESERVE GOOD DOCUMENTATION

Do not rewrite documentation merely for stylistic preference.

Preserve documentation that is:

- accurate;
- specific;
- complete;
- meaningful;
- useful;
- consistent with implementation.

However, do NOT preserve documentation merely because it exists.

If documentation is:

- generic;
- boilerplate;
- shallow;
- ambiguous;
- incomplete;
- misleading;
- technically inaccurate;

improve it.

# PHASE 7 — DOCUMENT ENGINEERING CONTRACTS

Where applicable, documentation should communicate:

- responsibility;
- architectural role;
- observable behavior;
- parameters;
- return semantics;
- state transitions;
- invariants;
- ownership;
- resource lifetime;
- concurrency;
- thread-safety;
- cancellation;
- disposal;
- retry;
- timeout;
- failure behavior;
- protocol behavior;
- configuration semantics;
- lifecycle;
- framework integration;
- logging semantics;
- material performance behavior.

Only document claims supported by:

- implementation;
- verified repository usage;
- established architecture;
- tests used as behavioral evidence;
- benchmark evidence;
- `.copilot/PERFORMANCE-CONTEXT.md`;
- reliable repository history.

Do not invent guarantees.

Do not invent exception contracts.

Do not invent performance characteristics.

Do not invent thread-safety guarantees.

# PRIVATE MEMBERS

Private members do not automatically require XML documentation.

However, document private members when they encode meaningful engineering knowledge such as:

- lifecycle;
- state transitions;
- ownership;
- resource lifetime;
- concurrency;
- cancellation;
- disposal;
- retry;
- validation;
- protocol behavior;
- error classification;
- synchronization;
- non-obvious algorithms;
- performance-sensitive behavior;
- framework requirements;
- important invariants.

Do not flood trivial private implementation details with meaningless comments.

# TYPES

Document meaningful types with useful engineering descriptions.

Avoid:

"Provides functionality for..."
"Handles..."
"Represents..."
"Contains..."

unless the description goes beyond the generic phrase and explains the actual responsibility.

# METHODS

For meaningful methods, document:

- what the method actually does;
- meaningful parameters;
- return values;
- state changes;
- side effects;
- cancellation;
- asynchronous behavior;
- ownership;
- disposal;
- concurrency;
- failure behavior;
- retry behavior;
- timeout behavior;
- protocol behavior;
- material performance characteristics.

Use `<param>` only for actual parameters.

Use `<returns>` only for returned values.

Use `<exception>` only where a meaningful thrown-exception contract is established.

# PROPERTIES

Use `<value>` for property semantics.

Explain what the property means rather than merely describing the accessor.

Avoid generic:

"Gets or sets..."

unless the accessor behavior itself is part of the meaningful contract.

# FIELDS AND CONSTANTS

Document fields and constants when their meaning contributes to understanding:

- state;
- limits;
- protocol behavior;
- configuration;
- lifecycle;
- synchronization;
- ownership;
- resource management;
- timing;
- operational behavior.

# ENUMS

Document enum types according to their actual purpose.

Document enum members when their meaning is not obvious or participates in an important contract.

Do not invent numeric-value guarantees.

# LOGGING

For source-generated or framework-integrated logging:

- document semantic meaning;
- document severity when established;
- document meaningful structured properties when they form part of the observable diagnostic contract;
- document event semantics when established.

Do not imply logs are always emitted if filtering can suppress them.

Do not imply an exception parameter is thrown when it is only recorded.

Do not alter:

- event IDs;
- levels;
- templates;
- structured logging fields.

# XML CORRECTNESS

Ensure:

- `<summary>` is correct;
- `<param>` names match actual parameters;
- `<returns>` applies only to returned values;
- `<value>` applies to properties/indexers;
- `<typeparam>` names match actual generic parameters;
- `<exception>` reflects actual established contracts;
- `<remarks>` contains substantive information;
- `<seealso>` is genuinely useful;
- XML is well formed;
- references resolve;
- documentation does not contradict implementation.

# DOCUMENTATION-ONLY RESTRICTION

Only XML documentation comments may change.

Do NOT modify:

- executable statements;
- control flow;
- algorithms;
- signatures;
- accessibility;
- types;
- constants;
- enum values;
- attributes;
- source generators;
- logging event IDs;
- logging levels;
- logging templates;
- structured logging fields;
- configuration;
- dependency injection;
- lifecycle behavior;
- exception handling;
- project files;
- analyzer configuration;
- tests;
- benchmarks.

If you discover a production defect:

1. Leave it untouched.
2. Record it separately.
3. Do not fix it as part of this task.

# PROJECT-WIDE COMPLETION REQUIREMENT

Do not declare completion until:

- every production C# file has been inspected;
- every meaningful documentable symbol has been evaluated;
- existing documentation has been quality-audited;
- missing meaningful documentation has been added;
- generic/paraphrase documentation has been evaluated and improved where inadequate;
- incomplete documentation has been improved;
- ambiguous documentation has been clarified;
- misleading documentation has been corrected;
- technically incorrect documentation has been corrected;
- useful existing documentation has been preserved;
- trivial implementation details have not been flooded with meaningless comments.

# FINAL PROJECT-WIDE VERIFICATION

After remediation:

1. Re-enumerate all production C# files.
2. Verify the complete project was audited.
3. Inspect the complete diff.
4. Verify every changed line is XML documentation.
5. Verify no executable source changed.
6. Verify no API or signature changed.
7. Verify no logging contract changed.
8. Verify no configuration changed.
9. Verify no analyzer configuration changed.
10. Verify no files outside src/VectorNNTP.NNTPD/ changed.
11. Validate XML documentation.
12. Build ONLY:

src/VectorNNTP.NNTPD/VectorNNTP.NNTPD.csproj

using the repository's normal Release/x64 configuration.

Do NOT build the entire solution unless a genuine blocking issue requires it.

Do NOT run tests.

# FINAL REPORT

Provide a project-wide report.

## File coverage

Report exact counts for:

- production C# files discovered;
- production C# files audited;
- production C# files modified;
- production C# files requiring no changes.

## Remediation

Report exact or reliably derived counts for:

- newly documented symbols;
- improved symbols;
- generic/paraphrase documentation replaced;
- incomplete documentation improved;
- ambiguous documentation clarified;
- misleading documentation corrected;
- technically incorrect documentation corrected.

Do not invent exact numbers.

If exact numbers cannot be established reliably, state that explicitly.

## Documentation examples

Provide several representative examples of:

- missing documentation that was added;
- generic documentation that was replaced;
- inadequate paraphrase documentation that was rewritten;
- meaningful existing documentation that was deliberately preserved.

For at least one example, identify the previous documentation and explain why it was inadequate.

## Remaining gaps

This section is mandatory.

Identify every production file containing any remaining meaningful documentation gap.

For each:

- file;
- symbol;
- gap;
- reason it remains.

If there are no remaining meaningful gaps, state exactly:

"Zero remaining meaningful XML documentation gaps were identified in the VectorNNTP.NNTPD production project."

Do not claim zero gaps merely because the project builds.

## Remaining generic documentation

Separately identify any production files that still contain generic or paraphrase-style XML documentation.

For each:

- file;
- symbol;
- current documentation;
- why it was retained.

If none remain, state:

"Zero materially inadequate generic or paraphrase XML documentation comments remain in the VectorNNTP.NNTPD production project."

Do not omit this section.

## Pre-existing engineering issues

Report compiler, nullable, analyzer, lifecycle, concurrency, resource ownership, performance, logging, configuration, or architectural issues discovered but intentionally left untouched.

Do not fix them.

## Validation

Report:

- exact build command;
- build result;
- tests not run;
- exact files modified;
- confirmation that only src/VectorNNTP.NNTPD/ was modified;
- confirmation that only XML documentation changed;
- confirmation that no executable behavior changed;
- confirmation that no API/signature changed;
- confirmation that no logging contract changed;
- confirmation that no warning suppression was introduced.

# DEFINITION OF DONE

The task is complete only when the ENTIRE VectorNNTP.NNTPD production project has been independently audited for:

1. missing documentation;
2. generic documentation;
3. shallow/paraphrase documentation;
4. incomplete documentation;
5. ambiguous documentation;
6. misleading documentation;
7. technically incorrect documentation.

The reusable single-file Documentation Prompt must be treated as the methodology for each individual file, NOT as the scope of this project-wide task.

The project-wide task MUST continue from file to file until the entire production project has been audited.

Do not stop after one file.

Do not stop after one subsystem.

Do not stop after the highest-value files.

Do not declare success merely because the build succeeds.

The objective is a genuinely well-documented ENTIRE VectorNNTP.NNTPD production codebase.
