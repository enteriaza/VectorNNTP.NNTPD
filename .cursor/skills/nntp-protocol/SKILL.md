---
name: nntp-protocol
description: >-
  Implements and reviews NNTP protocol behavior against the repository local RFC
  standards library under docs/standards/rfcs/. Use when adding or changing NNTP
  commands, responses, multiline framing, capabilities, AUTHINFO, TLS/STARTTLS,
  LIST extensions, compression, or protocol conformance tests.
---

# NNTP protocol

## Purpose

Implement and review NNTP behavior from **local RFC text**, not memory.

## When to use

- Any NNTP command, response, session state, or framing work
- Protocol conformance tests
- TLS/AUTH/LIST/streaming/compression extensions for NNTP
- Reviews of protocol-sensitive diffs

## Standards library

Primary path: `docs/standards/rfcs/` (see `docs/standards/rfcs/README.md`).

| Start with | Role |
|------------|------|
| `rfc3977.txt` | Current NNTP base (obsoletes 977) |
| `rfc6048.txt` | LIST additions (updates 3977/2980) |
| `rfc2980.txt` | Common extensions (informational; updated by later RFCs) |
| `rfc4642.txt` / `rfc8143.txt` | TLS with NNTP |
| `rfc4643.txt` | Authentication |
| `rfc4644.txt` | Streaming feeds |
| `rfc8054.txt` | Compression |
| `rfc5538.txt` | `news` / `nntp` URI schemes |
| `rfc977.txt` | Historical only (obsoleted) |

Do **not** treat every file as applicable to every feature. Check obsolescence/updates in the README and RFC headers. Consult RFC Editor errata when behavior is ambiguous.

## Mandatory workflow

1. Identify the feature and which RFCs apply.
2. **Read the relevant sections** of the local RFC text before coding.
3. Check updates, obsolescence, and errata relationships.
4. Separate **MUST/SHOULD/MAY** (mandatory vs optional).
5. Capture commands, response codes, formats, state transitions, and connection rules that apply.
6. Account for multiline responses, dot-stuffing, line endings, and command sequencing where relevant.
7. Consider AUTH, TLS, CAPABILITIES, and session state only when the feature needs them.
8. Implement against that reading; cite RFC + section in notes/docs when useful.
9. Add conformance tests for valid paths **and** malformed/unexpected input.
10. Do not claim full RFC conformance without evidence covering the claimed scope.

## Representation

Protocol data remains **byte-oriented** throughout the data plane. Authoritative rule: [docs/architecture.md](../../../docs/architecture.md#byte-oriented-protocol-data-plane).

Do not convert wire bytes to `string` for parse/dispatch, and do not build responses by constructing strings only to encode them again. Convert at an explicit boundary (auth provider, ingest API, logging, configuration), not as the default protocol representation. Any hot-path bytes↔string conversion needs a written justification.

## Framing and I/O reminders (verify in RFC text)

- Multiline termination and dot-stuffing rules from the applicable base/extension RFC
- CRLF line discipline for NNTP
- Capability advertisement vs actual enabled features
- Never assume a connected TCP socket implies a valid NNTP session state

## Avoid

- Implementing from memory or generic “how NNTP usually works”
- Mixing obsolete RFC 977 requirements over RFC 3977 without intentional historical mode
- Inventing response codes or optional features as mandatory
- Live network tests when deterministic unit tests suffice

## Related skills

- `testing` for deterministic protocol tests
- `performance-engineering` for hot-path parsers/I/O
- `configuration-and-security` for TLS ports / bind settings

## Validation checklist

- [ ] Applicable RFCs identified and local sections read
- [ ] Updates/obsolescence/errata considered
- [ ] Mandatory vs optional distinguished
- [ ] Framing/state/sequencing handled where relevant
- [ ] Protocol data remains bytes; any string conversion has an explicit boundary justification
- [ ] Conformance + malformed-input tests added or justified
- [ ] No unverified conformance claims
