---
name: eoc-docs
description: Use when starting/ending a working session on eoc-biomes-compute, when an architectural decision is made, or when updating files in docs/. Maintains session logs (docs/sessions/), ADRs (docs/adr/), INDEX.md, and Obsidian-flavored frontmatter per repo conventions. Trigger on phrases like "log this session", "ADR for X", "update docs", "write up what we did", or after finishing a substantive change.
---
# eoc-docs

Maintain documentation for `eoc-biomes-compute`. Append-only, Obsidian-flavored markdown.

## Layout

```
docs/
├── INDEX.md                    handcrafted table of contents (≤30 lines)
├── migration.md                living architecture doc
├── adr/
│   └── NNNN-slug.md            one decision per file, sequential numbering
└── sessions/
    └── YYYY-MM-DD-slug.md      one per working session
```

`README.md` at repo root has **no frontmatter** (GitHub renders it literally).

## Frontmatter (all docs in `docs/`)

```yaml
---
status: draft | accepted | open | living | closed | superseded
date: YYYY-MM-DD
tags: [...]
related: [[wikilink]], [[wikilink]]
---
```

Status conventions:
- ADRs: `accepted` once decided. If overridden later: new ADR has `supersedes: ADR-NNNN`, old gets `status: superseded` and `superseded-by: ADR-NNNN`.
- Sessions: `closed` after writing.
- Architecture docs (e.g. `migration.md`): `living`.
- INDEX: `living`.

## When to act

### End of substantive session
Write `docs/sessions/YYYY-MM-DD-<slug>.md`:

```markdown
---
status: closed
date: YYYY-MM-DD
tags: [session, ...]
related: [[../migration]], [[../adr/NNNN-...]]
---
# <session title>

## Shipped
- bullets of what was committed (file paths welcome)

## Decided
- bullets; promote architecturally significant ones to ADRs and link
- non-architectural decisions stay inline

## Open / next session
1. concrete handoff items, numbered
```

Then update `INDEX.md` to add the session entry (one line).

### Architectural decision made
Create `docs/adr/NNNN-<slug>.md` (NNNN = next sequential, four digits):

```markdown
---
status: accepted
date: YYYY-MM-DD
tags: [adr, ...]
related: [[../migration]], [[../adr/NNNN-...]]
---
# ADR-NNNN: <decision in one line>

## Context
What's the problem. What options were on the table.

## Decision
What was chosen.

## Consequences
Tradeoffs. What's now true. Outgrowth points.

## Related
[[wikilinks]] to other ADRs / sessions / code paths.
```

Then update `INDEX.md`.

### Updating top-level `README.md`

Review on every substantive session. Keep current — README is what a stranger (or future-you) reads first.

Update when any of these change:
- **Layout** (new top-level dirs, new major modules in `memory/`, new Unity workspaces)
- **Getting started** (new install steps, new entry-point scripts, new env vars)
- **Concepts** (new architectural pillars worth surfacing — e.g. when feedback mechanisms ship, when a new modality is wired)
- **Status** (anything that flips from "planned" to "shipped")

Rules:
- **No frontmatter** (GitHub renders it as literal text on the repo home page)
- Keep concise — README is a landing page, not full docs. Link to `docs/` for depth
- Don't duplicate ADR rationale in the README — link instead
- Strip out v0/scaffolding-era language once features mature

### Updating `migration.md`
Keep it the *current* architecture doc. When an open question gets resolved:
1. Create the ADR
2. Strike through the question in `migration.md`'s "Unresolved" list with the resolution: `~~Q?~~ → **answer** ([[adr/NNNN-...]])`

Past decisions live in ADRs. `migration.md` references them; doesn't duplicate the rationale.

## Conventions

- **Append-only** for sessions and ADRs. Never rewrite history; supersede with new entries.
- **`[[wikilinks]]`** for cross-references (Obsidian-friendly, plain-text otherwise). Path-relative from the file's location.
- **Slug**: kebab-case, ≤6 words, descriptive.
- **Date**: `YYYY-MM-DD`. No time.
- **Style**: see global CLAUDE.md — concise, sacrifice grammar. Bullets over prose.
- **INDEX.md** is handcrafted (so summaries stay useful), not auto-generated. Update it the same commit as the new session/ADR.

## Sanity checks before commit

- [ ] New session/ADR has frontmatter with required keys
- [ ] INDEX.md updated with the new entry
- [ ] Cross-links use `[[...]]` syntax with correct relative paths
- [ ] If an ADR resolves a `migration.md` open question, the question is struck through with link
- [ ] ADR number is sequential (`ls docs/adr/ | tail -1` to confirm)
- [ ] README.md reviewed; updated if layout / install / concepts / status changed
