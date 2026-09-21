# VectorNNTP Cursor skills index

Project-local Agent Skills for Cursor (including JetBrains Rider). Location: `.cursor/skills/<skill-name>/SKILL.md`.

Core engineering rules: [`.cursor/rules/vectornntp-core.mdc`](../rules/vectornntp-core.mdc).  
Project entry point: [`AGENTS.md`](../../AGENTS.md) (routing only; honor by Rider ACP is **not** assumed).

## Reliable discovery (Rider ACP)

Project skills **may not** appear in Rider ACP’s injected skill list.

**Reliable fallback:** match the task to rows below, then **explicitly read** each relevant `SKILL.md` before substantive work.

- Presence of these files does **not** prove automatic invocation.
- Distinguish **inferred** skill selection (chose from this index) from **observed** skill invocation (the platform injected or auto-opened the skill in this session).
- Never claim automatic loading or invocation unless verified in the current session.
- If a skill file is missing or unreadable, say so and continue only where safe.

## How to select skills

1. Read this index before substantive repository work.
2. Match the user request and affected code to one or more skills below.
3. **Read** the matching `SKILL.md` file(s) before implementing, reviewing, testing, or documenting.
4. Follow each skill’s mandatory workflow and validation checklist.
5. **Combine** skills when needed (e.g. protocol feature → `nntp-protocol` + `testing` + `implementation-and-verification`). Do not default to a single skill when several apply.

These skills are for **VectorNNTP** (including `VectorNNTP.NNTPD`). They intentionally exclude BackFiller-only RabbitMQ / work-settlement assumptions.

## Skills

| Skill | Path | Use when |
|-------|------|----------|
| `code-review` | [code-review/SKILL.md](code-review/SKILL.md) | Reviewing diffs/PRs for defects, risks, and evidence-backed findings without auto-editing |
| `testing` | [testing/SKILL.md](testing/SKILL.md) | Designing or fixing deterministic regression tests |
| `documentation` | [documentation/SKILL.md](documentation/SKILL.md) | XML/docs-only passes that must not change behavior |
| `performance-engineering` | [performance-engineering/SKILL.md](performance-engineering/SKILL.md) | Hot-path, allocation, networking, or throughput work with evidence |
| `nntp-protocol` | [nntp-protocol/SKILL.md](nntp-protocol/SKILL.md) | Implementing or reviewing NNTP behavior against local RFCs |
| `lifecycle-and-concurrency` | [lifecycle-and-concurrency/SKILL.md](lifecycle-and-concurrency/SKILL.md) | Startup/shutdown, cancellation, ownership, races, hosted services |
| `configuration-and-security` | [configuration-and-security/SKILL.md](configuration-and-security/SKILL.md) | Options, validation, secrets, bind addresses, Cloudflare env vars |
| `implementation-and-verification` | [implementation-and-verification/SKILL.md](implementation-and-verification/SKILL.md) | End-to-end feature/bugfix tasks from inspect → verify → report |

## Relation to core instructions

The core rule establishes identity, inspection-first discipline, and honesty about verification. Skills add **focused procedures**. Prefer the core rule for every task; explicitly read skills for depth.

## Out of scope for this skill set

- GitHub Actions / Copilot workflow YAML from other repositories
- Inventing CI pipelines
- Claiming 40+ Gbps (or any throughput target) without measured evidence
- Copying RabbitMQ ACK/NACK settlement invariants into NNTPD
