# VectorNNTP agent instructions

Concise routing for Cursor agents (including JetBrains Rider ACP). This file is an entry point; it does not prove that Rider ACP automatically loads it.

## Before substantive work

1. Read the engineering skill index: [`.cursor/skills/README.md`](.cursor/skills/README.md).
2. Identify the task domain and **explicitly read** the relevant [`.cursor/skills/<skill-name>/SKILL.md`](.cursor/skills/) file(s) before implementing, reviewing, testing, or documenting.
3. Combine skills when appropriate (do not default to a single skill when several apply).
4. Follow the core engineering rules in [`.cursor/rules/vectornntp-core.mdc`](.cursor/rules/vectornntp-core.mdc). Protocol data remains byte-oriented throughout the data plane ([`docs/architecture.md`](docs/architecture.md#byte-oriented-protocol-data-plane)). Application logging prefers source-generated structured methods ([`docs/architecture.md`](docs/architecture.md#source-generated-structured-logging)).
5. Follow the placement policy in [`.cursor/rules/repository-artifacts.mdc`](.cursor/rules/repository-artifacts.mdc): production in `src/`/`tests/`; permanent docs only in `docs/`; experiments, benches, and generated reports in `.artifacts/`. Historical lookup starts at [`.artifacts/ARTIFACT-INDEX.md`](.artifacts/ARTIFACT-INDEX.md).

## Discovery honesty

- Treat the skill index as a **routing guide**, not proof that Cursor automatically loaded or invoked a skill.
- Never claim a rule or skill was automatically loaded unless that was **directly verified in the current session**.
- If a required instruction or skill file is missing or unreadable, state that and continue only where safe.

## Skill map (see index for detail)

| Skill | Path |
|-------|------|
| `code-review` | `.cursor/skills/code-review/SKILL.md` |
| `testing` | `.cursor/skills/testing/SKILL.md` |
| `documentation` | `.cursor/skills/documentation/SKILL.md` |
| `performance-engineering` | `.cursor/skills/performance-engineering/SKILL.md` |
| `nntp-protocol` | `.cursor/skills/nntp-protocol/SKILL.md` |
| `lifecycle-and-concurrency` | `.cursor/skills/lifecycle-and-concurrency/SKILL.md` |
| `configuration-and-security` | `.cursor/skills/configuration-and-security/SKILL.md` |
| `implementation-and-verification` | `.cursor/skills/implementation-and-verification/SKILL.md` |
