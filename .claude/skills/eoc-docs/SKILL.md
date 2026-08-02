---
name: eoc-docs
description: Repo bindings for documenting eoc-biomes-compute. Use when starting/ending a working session on this repo, when an architectural decision is made, or when updating files in docs/. Trigger on "log this session", "ADR for X", "update docs", "write up what we did", or after finishing a substantive change.
---
# eoc-docs

Repo-specific bindings for `eoc-biomes-compute`. **Invoke the `docs-log` skill first** — it owns
the method (frontmatter schema, session/ADR templates, sequential numbering, append-only and
supersede rules, `[[wikilinks]]`, the pre-commit checklist). This file only says where things live
here and which conventions differ.

## Path bindings

| `docs-log` concept | This repo |
|---|---|
| INDEX | `docs/INDEX.md` |
| Session logs | `docs/sessions/YYYY-MM-DD-slug.md` |
| ADRs | `docs/adr/NNNN-slug.md` |
| Living architecture doc | `docs/ARCHITECTURE.md` — Unity runtime + memory system reference |
| Living migration doc | `docs/migration.md` — memory architecture plan, carries the "Unresolved" question list |
| Living backlog | `docs/ROADMAP.md` |
| Specs / plans | `docs/superpowers/specs/`, `docs/superpowers/plans/` |

Engine-level design docs live beside the code, not in `docs/`:
`Assets/Workspace/11.0 Biomes/docs/` (`INTEGRATION_DESIGN`, `INTERACTION_DESIGN_II`).

## Local conventions

- **Two living docs, different jobs.** `ARCHITECTURE.md` describes the system as built.
  `migration.md` tracks the memory-architecture plan and owns the open-questions list — resolve
  questions there (strike through + link the ADR), not in `ARCHITECTURE.md`.
- **`ROADMAP.md` is verified against code**, not aspirational. Its standing lesson is
  *configured ≠ executing*: a feature is only "shipped" when a kernel actually reads it. When you
  ship or kill something, move it between the Shipped / Backlog / Dead-code sections in the same
  commit.
- **`ROADMAP.md` owns the code backlog. Todoist owns everything else** (strategy, admin, website,
  cross-project). Cross-link between them; never copy items in either direction.
- **Specs and plans do not use YAML frontmatter** — unlike sessions and ADRs. They open with a
  `# Title — Design` heading and bold `**Date:** / **Status:** / **Files touched:**` lines. Match
  the existing files in `docs/superpowers/specs/`; `docs-log`'s frontmatter rule applies to
  `docs/` proper (INDEX, ADRs, sessions, living docs), not here.
- Specs get an INDEX entry under "Specs / plans", newest first, with the plan cross-linked once it
  exists.
- Backlog tiers map to `Assets/Workspace/11.0 Biomes/docs/INTEGRATION_DESIGN`.
