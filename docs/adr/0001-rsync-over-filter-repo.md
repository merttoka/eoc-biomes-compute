---
status: accepted
date: 2026-04-26
tags: [adr, repo, migration]
related: [[../migration]]
---
# ADR-0001: Plain rsync + fresh `git init` for repo split

## Context

Splitting `10.0 Metaesthetica` and `11.0 Biomes` from `edge-of-chaos-unity-compute` into a new repo. Two methods on the table:

- `git filter-repo --path …` — preserves git history of the moved paths in the new repo
- Plain `rsync` + `git init` — fresh start, no history

`git filter-repo` was not installed (`brew install git-filter-repo` required). User wanted to scaffold immediately.

## Decision

Plain `rsync` + fresh `git init`. Original repo untouched.

## Consequences

- New repo's `git log` starts on 2026-04-26. No `git blame` for biome work prior to that date.
- Faster split: no extra tool install, no clone-and-filter ceremony.
- Reversible: can be redone later with `filter-repo` on a `with-history` branch and force-pushed if history becomes important.
- Old repo still contains the migrated workspaces. Both sides can diverge.

## Related

- [[../sessions/2026-04-26-split-and-daemon-v0]]
